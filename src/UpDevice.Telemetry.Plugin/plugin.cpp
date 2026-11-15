/**
 * UpDevice 遥测插件（ATS / ETS2）
 *
 * 作用只有一个：把 SCS 遥测 SDK 里的少量通道写到一块命名共享内存，
 * 由 UpDevice.Service 读取并拼成上报文案。
 *
 * 设计约束（都很重要）：
 *   - 这个 DLL 会被注入到别人的游戏进程里，任何异常都可能让游戏崩。所以：
 *     不用 STL、不分配堆、不抛异常、不开线程、不写日志文件，只做赋值与内存拷贝。
 *   - 不依赖 C 运行时（连 memcpy/strlen 都自己写），MinGW 与 MSVC 编出来都能用。
 *   - 共享内存布局是两边约定死的，C# 侧见 TruckTelemetryReader.cs；改动要同步版本号。
 */

#include <windows.h>
#include <stddef.h>

#include "scssdk.h"
#include "scssdk_telemetry.h"
#include "scssdk_telemetry_channel.h"
#include "scssdk_telemetry_event.h"

#include "common/scssdk_telemetry_common_channels.h"
#include "common/scssdk_telemetry_common_configs.h"
#include "common/scssdk_telemetry_job_common_channels.h"
#include "common/scssdk_telemetry_trailer_common_channels.h"
#include "common/scssdk_telemetry_truck_common_channels.h"

// ---------------------------------------------------------------------------
// 共享内存布局（与 C# 的 TruckTelemetryReader 逐字节对应）
// ---------------------------------------------------------------------------

#define UPD_MAGIC 0x54445055u /* 内存里是 'U' 'P' 'D' 'T' */
#define UPD_LAYOUT_VERSION 1u
#define UPD_NAME_LENGTH 64
#define UPD_SHARED_SIZE 320

#define UPD_GAME_UNKNOWN 0u
#define UPD_GAME_ETS2 1u
#define UPD_GAME_ATS 2u

#define UPD_FLAG_PAUSED 0x01u
#define UPD_FLAG_JOB_ACTIVE 0x02u
#define UPD_FLAG_TRAILER_ATTACHED 0x04u

#pragma pack(push, 1)
struct UpdTruckState
{
    unsigned int magic;      // 0
    unsigned int struct_size; // 4
    unsigned int version;    // 8
    unsigned int sequence;   // 12  写入序号，奇数表示正在写（seqlock）
    unsigned int game;       // 16
    unsigned int flags;      // 20
    unsigned int game_time;  // 24  游戏内当天分钟数
    float speed;             // 28  m/s
    float speed_limit;       // 32  m/s
    float fuel_amount;       // 36  L
    float fuel_capacity;     // 40  L
    float fuel_range;        // 44  km
    float wear_truck;        // 48  0..1
    float wear_trailer;      // 52  0..1
    float cargo_damage;      // 56  0..1
    float nav_distance;      // 60  m
    float nav_time;          // 64  s
    float rest_stop;         // 68  游戏内分钟
    unsigned int cargo_mass; // 72  kg
    unsigned int job_deadline; // 76 游戏内分钟，0 = 游戏没提供
    char cargo_name[UPD_NAME_LENGTH];           // 80
    char destination_city[UPD_NAME_LENGTH];     // 144
    char destination_company[UPD_NAME_LENGTH];  // 208
    unsigned int updated_ms;  // 272 插件写入时的 GetTickCount
    unsigned char reserved[44]; // 276..319
};
#pragma pack(pop)

// 编译期校验：偏移错了宁可不编译，也别让游戏加载一个会读错内存的插件。
typedef char upd_check_magic[offsetof(UpdTruckState, magic) == 0 ? 1 : -1];
typedef char upd_check_cargo_name[offsetof(UpdTruckState, cargo_name) == 80 ? 1 : -1];
typedef char upd_check_city[offsetof(UpdTruckState, destination_city) == 144 ? 1 : -1];
typedef char upd_check_company[offsetof(UpdTruckState, destination_company) == 208 ? 1 : -1];
typedef char upd_check_updated[offsetof(UpdTruckState, updated_ms) == 272 ? 1 : -1];
typedef char upd_check_size[sizeof(UpdTruckState) == UPD_SHARED_SIZE ? 1 : -1];

// ---------------------------------------------------------------------------
// 通道回调的 context：用一个小整数标识是哪个值，避免每帧比较字符串
// ---------------------------------------------------------------------------

enum UpdChannel
{
    UPD_CH_SPEED = 1,
    UPD_CH_SPEED_LIMIT,
    UPD_CH_FUEL_AMOUNT,
    UPD_CH_FUEL_RANGE,
    UPD_CH_NAV_DISTANCE,
    UPD_CH_NAV_TIME,
    UPD_CH_GAME_TIME,
    UPD_CH_REST_STOP,
    UPD_CH_WEAR_ENGINE,
    UPD_CH_WEAR_TRANSMISSION,
    UPD_CH_WEAR_CABIN,
    UPD_CH_WEAR_CHASSIS,
    UPD_CH_WEAR_WHEELS,
    UPD_CH_TRAILER_WEAR_BODY,
    UPD_CH_TRAILER_WEAR_CHASSIS,
    UPD_CH_TRAILER_WEAR_WHEELS,
    UPD_CH_TRAILER_CONNECTED,
    UPD_CH_TRAILER_CARGO_DAMAGE,
    UPD_CH_JOB_CARGO_DAMAGE,
};

// ---------------------------------------------------------------------------
// 状态
// ---------------------------------------------------------------------------

static HANDLE g_mapping = NULL;
static UpdTruckState *g_shared = NULL;
static UpdTruckState g_state; // 插件内部的最新值，按帧整体发布
static scs_telemetry_register_for_channel_t g_register_channel = NULL;
static scs_telemetry_unregister_from_channel_t g_unregister_channel = NULL;
static scs_telemetry_register_for_event_t g_register_event = NULL;
static scs_telemetry_unregister_from_event_t g_unregister_event = NULL;

// ---------------------------------------------------------------------------
// 极小的字符串工具（不依赖 CRT）
// ---------------------------------------------------------------------------

static bool upd_streq(const char *a, const char *b)
{
    if (a == NULL || b == NULL)
    {
        return false;
    }

    while (*a != '\0' && *a == *b)
    {
        ++a;
        ++b;
    }

    return *a == *b;
}

static void upd_copy_string(char *destination, const char *source)
{
    int i = 0;
    if (source != NULL)
    {
        while (i < UPD_NAME_LENGTH - 1 && source[i] != '\0')
        {
            destination[i] = source[i];
            ++i;
        }
    }

    for (; i < UPD_NAME_LENGTH; ++i)
    {
        destination[i] = '\0';
    }
}

static void upd_clear_string(char *destination)
{
    for (int i = 0; i < UPD_NAME_LENGTH; ++i)
    {
        destination[i] = '\0';
    }
}

// ---------------------------------------------------------------------------
// 共享内存
// ---------------------------------------------------------------------------

static void upd_open_shared_memory(void)
{
    if (g_shared != NULL)
    {
        return;
    }

    // Local\ 前缀：同一登录会话内可见，不需要跨会话（游戏与主进程都在用户会话里）。
    g_mapping = CreateFileMappingA(INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE, 0, UPD_SHARED_SIZE,
                                   "Local\\UpDeviceTruckTelemetry");
    if (g_mapping == NULL)
    {
        return;
    }

    void *view = MapViewOfFile(g_mapping, FILE_MAP_WRITE, 0, 0, UPD_SHARED_SIZE);
    if (view == NULL)
    {
        CloseHandle(g_mapping);
        g_mapping = NULL;
        return;
    }

    g_shared = static_cast<UpdTruckState *>(view);
}

static void upd_close_shared_memory(void)
{
    if (g_shared != NULL)
    {
        // 清零等于告诉读取方「插件已卸载」，避免读到上一局的残留数据。
        upd_clear_string(g_shared->cargo_name);
        g_shared->magic = 0;
        UnmapViewOfFile(g_shared);
        g_shared = NULL;
    }

    if (g_mapping != NULL)
    {
        CloseHandle(g_mapping);
        g_mapping = NULL;
    }
}

/// 按帧把内部状态整块发布出去（seqlock：写入期间序号是奇数）。
static void upd_publish(void)
{
    if (g_shared == NULL)
    {
        return;
    }

    g_state.magic = UPD_MAGIC;
    g_state.struct_size = UPD_SHARED_SIZE;
    g_state.version = UPD_LAYOUT_VERSION;
    g_state.updated_ms = (unsigned int)GetTickCount();

    unsigned int sequence = g_shared->sequence;
    g_shared->sequence = sequence + 1;

    // 整块拷贝，但跳过 sequence 字段本身（offset 12..15），否则会把写入标记覆盖掉。
    const unsigned char *source = reinterpret_cast<const unsigned char *>(&g_state);
    unsigned char *target = reinterpret_cast<unsigned char *>(g_shared);
    for (unsigned int i = 0; i < offsetof(UpdTruckState, sequence); ++i)
    {
        target[i] = source[i];
    }

    for (unsigned int i = offsetof(UpdTruckState, game); i < UPD_SHARED_SIZE; ++i)
    {
        target[i] = source[i];
    }

    g_shared->sequence = sequence + 2;
}

// ---------------------------------------------------------------------------
// 通道回调
// ---------------------------------------------------------------------------

static float upd_clamp_unit(float value)
{
    if (value < 0.0f)
    {
        return 0.0f;
    }

    if (value > 1.0f)
    {
        return 1.0f;
    }

    return value;
}

static void SCSAPIFUNC upd_channel_callback(const scs_string_t name, const scs_u32_t index,
                                            const scs_value_t *const value, const scs_context_t context)
{
    (void)name;
    (void)index;

    if (value == NULL)
    {
        return;
    }

    const int channel = (int)(reinterpret_cast<size_t>(context));
    switch (channel)
    {
    case UPD_CH_SPEED:
        g_state.speed = value->value_float.value;
        break;
    case UPD_CH_SPEED_LIMIT:
        g_state.speed_limit = value->value_float.value;
        break;
    case UPD_CH_FUEL_AMOUNT:
        g_state.fuel_amount = value->value_float.value;
        break;
    case UPD_CH_FUEL_RANGE:
        g_state.fuel_range = value->value_float.value;
        break;
    case UPD_CH_NAV_DISTANCE:
        g_state.nav_distance = value->value_float.value;
        break;
    case UPD_CH_NAV_TIME:
        g_state.nav_time = value->value_float.value;
        break;
    case UPD_CH_GAME_TIME:
        g_state.game_time = value->value_u32.value;
        break;
    case UPD_CH_REST_STOP:
        g_state.rest_stop = (float)value->value_s32.value;
        break;

    // 车损取各部件的最大值：玩家更关心「最坏的那个」，而不是平均值。
    case UPD_CH_WEAR_ENGINE:
    case UPD_CH_WEAR_TRANSMISSION:
    case UPD_CH_WEAR_CABIN:
    case UPD_CH_WEAR_CHASSIS:
    case UPD_CH_WEAR_WHEELS:
    {
        float wear = upd_clamp_unit(value->value_float.value);
        if (wear > g_state.wear_truck)
        {
            g_state.wear_truck = wear;
        }

        break;
    }

    case UPD_CH_TRAILER_WEAR_BODY:
    case UPD_CH_TRAILER_WEAR_CHASSIS:
    case UPD_CH_TRAILER_WEAR_WHEELS:
    {
        float wear = upd_clamp_unit(value->value_float.value);
        if (wear > g_state.wear_trailer)
        {
            g_state.wear_trailer = wear;
        }

        break;
    }

    case UPD_CH_TRAILER_CONNECTED:
        if (value->value_bool.value)
        {
            g_state.flags |= UPD_FLAG_TRAILER_ATTACHED;
        }
        else
        {
            g_state.flags &= ~UPD_FLAG_TRAILER_ATTACHED;
            g_state.wear_trailer = 0.0f;
        }

        break;

    case UPD_CH_TRAILER_CARGO_DAMAGE:
    case UPD_CH_JOB_CARGO_DAMAGE:
        g_state.cargo_damage = upd_clamp_unit(value->value_float.value);
        break;

    default:
        break;
    }
}

// ---------------------------------------------------------------------------
// 配置事件：油量上限、货物名、目的地这些只在配置里出现，通道拿不到
// ---------------------------------------------------------------------------

static const scs_named_value_t *upd_find_attribute(const scs_named_value_t *attributes, const char *name)
{
    if (attributes == NULL)
    {
        return NULL;
    }

    for (const scs_named_value_t *attribute = attributes; attribute->name != NULL; ++attribute)
    {
        if (upd_streq(attribute->name, name))
        {
            return attribute;
        }
    }

    return NULL;
}

static void upd_apply_truck_config(const scs_named_value_t *attributes)
{
    const scs_named_value_t *capacity = upd_find_attribute(attributes, SCS_TELEMETRY_CONFIG_ATTRIBUTE_fuel_capacity);
    if (capacity != NULL && capacity->value.type == SCS_VALUE_TYPE_float)
    {
        g_state.fuel_capacity = capacity->value.value_float.value;
    }
}

static void upd_apply_job_config(const scs_named_value_t *attributes)
{
    const scs_named_value_t *cargo = upd_find_attribute(attributes, SCS_TELEMETRY_CONFIG_ATTRIBUTE_cargo);
    const scs_named_value_t *city = upd_find_attribute(attributes, SCS_TELEMETRY_CONFIG_ATTRIBUTE_destination_city);
    const scs_named_value_t *company = upd_find_attribute(attributes, SCS_TELEMETRY_CONFIG_ATTRIBUTE_destination_company);
    const scs_named_value_t *mass = upd_find_attribute(attributes, SCS_TELEMETRY_CONFIG_ATTRIBUTE_cargo_mass);

    // 任务结束时游戏会再发一次 job 配置（货物为空），这里据此清掉任务标记。
    const bool has_cargo = cargo != NULL && cargo->value.type == SCS_VALUE_TYPE_string
                           && cargo->value.value_string.value != NULL && cargo->value.value_string.value[0] != '\0';

    if (has_cargo)
    {
        upd_copy_string(g_state.cargo_name, cargo->value.value_string.value);
        g_state.flags |= UPD_FLAG_JOB_ACTIVE;
    }
    else
    {
        upd_clear_string(g_state.cargo_name);
        g_state.flags &= ~UPD_FLAG_JOB_ACTIVE;
        g_state.cargo_damage = 0.0f;
    }

    if (city != NULL && city->value.type == SCS_VALUE_TYPE_string)
    {
        upd_copy_string(g_state.destination_city, city->value.value_string.value);
    }
    else
    {
        upd_clear_string(g_state.destination_city);
    }

    if (company != NULL && company->value.type == SCS_VALUE_TYPE_string)
    {
        upd_copy_string(g_state.destination_company, company->value.value_string.value);
    }
    else
    {
        upd_clear_string(g_state.destination_company);
    }

    g_state.cargo_mass = (mass != NULL && mass->value.type == SCS_VALUE_TYPE_u32)
                             ? (unsigned int)mass->value.value_u32.value
                             : 0u;
}

static void SCSAPIFUNC upd_event_callback(const scs_event_t event, const void *const event_info,
                                          const scs_context_t context)
{
    (void)context;

    switch (event)
    {
    case SCS_TELEMETRY_EVENT_frame_end:
        upd_publish();
        break;

    case SCS_TELEMETRY_EVENT_paused:
        g_state.flags |= UPD_FLAG_PAUSED;
        break;

    case SCS_TELEMETRY_EVENT_started:
        g_state.flags &= ~UPD_FLAG_PAUSED;
        break;

    case SCS_TELEMETRY_EVENT_configuration:
    {
        const scs_telemetry_configuration_t *configuration =
            static_cast<const scs_telemetry_configuration_t *>(event_info);
        if (configuration == NULL || configuration->id == NULL)
        {
            break;
        }

        if (upd_streq(configuration->id, SCS_TELEMETRY_CONFIG_truck))
        {
            upd_apply_truck_config(configuration->attributes);
        }
        else if (upd_streq(configuration->id, SCS_TELEMETRY_CONFIG_job))
        {
            upd_apply_job_config(configuration->attributes);
        }

        break;
    }

    default:
        break;
    }
}

// ---------------------------------------------------------------------------
// 注册
// ---------------------------------------------------------------------------

static void upd_register_channel(const char *name, const scs_u32_t index, const scs_value_type_t type, int id)
{
    if (g_register_channel == NULL)
    {
        return;
    }

    g_register_channel(name, index, type, SCS_TELEMETRY_CHANNEL_FLAG_none, upd_channel_callback,
                       reinterpret_cast<scs_context_t>(static_cast<size_t>(id)));
}

static void upd_register_channels(void)
{
    upd_register_channel(SCS_TELEMETRY_TRUCK_CHANNEL_speed, SCS_U32_NIL, SCS_VALUE_TYPE_float, UPD_CH_SPEED);
    upd_register_channel(SCS_TELEMETRY_TRUCK_CHANNEL_navigation_speed_limit, SCS_U32_NIL, SCS_VALUE_TYPE_float,
                         UPD_CH_SPEED_LIMIT);
    upd_register_channel(SCS_TELEMETRY_TRUCK_CHANNEL_fuel, SCS_U32_NIL, SCS_VALUE_TYPE_float, UPD_CH_FUEL_AMOUNT);
    upd_register_channel(SCS_TELEMETRY_TRUCK_CHANNEL_fuel_range, SCS_U32_NIL, SCS_VALUE_TYPE_float, UPD_CH_FUEL_RANGE);
    upd_register_channel(SCS_TELEMETRY_TRUCK_CHANNEL_navigation_distance, SCS_U32_NIL, SCS_VALUE_TYPE_float,
                         UPD_CH_NAV_DISTANCE);
    upd_register_channel(SCS_TELEMETRY_TRUCK_CHANNEL_navigation_time, SCS_U32_NIL, SCS_VALUE_TYPE_float,
                         UPD_CH_NAV_TIME);
    upd_register_channel(SCS_TELEMETRY_CHANNEL_game_time, SCS_U32_NIL, SCS_VALUE_TYPE_u32, UPD_CH_GAME_TIME);
    upd_register_channel(SCS_TELEMETRY_CHANNEL_next_rest_stop, SCS_U32_NIL, SCS_VALUE_TYPE_s32, UPD_CH_REST_STOP);

    upd_register_channel(SCS_TELEMETRY_TRUCK_CHANNEL_wear_engine, SCS_U32_NIL, SCS_VALUE_TYPE_float,
                         UPD_CH_WEAR_ENGINE);
    upd_register_channel(SCS_TELEMETRY_TRUCK_CHANNEL_wear_transmission, SCS_U32_NIL, SCS_VALUE_TYPE_float,
                         UPD_CH_WEAR_TRANSMISSION);
    upd_register_channel(SCS_TELEMETRY_TRUCK_CHANNEL_wear_cabin, SCS_U32_NIL, SCS_VALUE_TYPE_float, UPD_CH_WEAR_CABIN);
    upd_register_channel(SCS_TELEMETRY_TRUCK_CHANNEL_wear_chassis, SCS_U32_NIL, SCS_VALUE_TYPE_float,
                         UPD_CH_WEAR_CHASSIS);
    upd_register_channel(SCS_TELEMETRY_TRUCK_CHANNEL_wear_wheels, SCS_U32_NIL, SCS_VALUE_TYPE_float,
                         UPD_CH_WEAR_WHEELS);

    // 挂车是带索引的通道，各版本索引起点不同（老版本 0 起、多挂车版本 1 起），两个都试。
    for (scs_u32_t index = 0; index <= 1; ++index)
    {
        upd_register_channel(SCS_TELEMETRY_TRAILER_CHANNEL_connected, index, SCS_VALUE_TYPE_bool,
                             UPD_CH_TRAILER_CONNECTED);
        upd_register_channel(SCS_TELEMETRY_TRAILER_CHANNEL_wear_body, index, SCS_VALUE_TYPE_float,
                             UPD_CH_TRAILER_WEAR_BODY);
        upd_register_channel(SCS_TELEMETRY_TRAILER_CHANNEL_wear_chassis, index, SCS_VALUE_TYPE_float,
                             UPD_CH_TRAILER_WEAR_CHASSIS);
        upd_register_channel(SCS_TELEMETRY_TRAILER_CHANNEL_wear_wheels, index, SCS_VALUE_TYPE_float,
                             UPD_CH_TRAILER_WEAR_WHEELS);
        upd_register_channel(SCS_TELEMETRY_TRAILER_CHANNEL_cargo_damage, index, SCS_VALUE_TYPE_float,
                             UPD_CH_TRAILER_CARGO_DAMAGE);
    }

    upd_register_channel(SCS_TELEMETRY_JOB_CHANNEL_cargo_damage, SCS_U32_NIL, SCS_VALUE_TYPE_float,
                         UPD_CH_JOB_CARGO_DAMAGE);
}

// ---------------------------------------------------------------------------
// 入口
// ---------------------------------------------------------------------------

static void upd_reset_state(void)
{
    unsigned char *raw = reinterpret_cast<unsigned char *>(&g_state);
    for (unsigned int i = 0; i < sizeof(UpdTruckState); ++i)
    {
        raw[i] = 0;
    }

    g_state.game = UPD_GAME_UNKNOWN;
}

extern "C" {

__declspec(dllexport) SCSAPI_RESULT scs_telemetry_init(const scs_u32_t version,
                                                       const scs_telemetry_init_params_t *const params)
{
    if (version != SCS_TELEMETRY_VERSION_1_00 && version != SCS_TELEMETRY_VERSION_1_01)
    {
        return SCS_RESULT_unsupported;
    }

    if (params == NULL)
    {
        return SCS_RESULT_invalid_parameter;
    }

    const scs_telemetry_init_params_v100_t *parameters =
        static_cast<const scs_telemetry_init_params_v100_t *>(params);

    g_register_event = parameters->register_for_event;
    g_unregister_event = parameters->unregister_from_event;
    g_register_channel = parameters->register_for_channel;
    g_unregister_channel = parameters->unregister_from_channel;

    if (g_register_event == NULL || g_register_channel == NULL)
    {
        return SCS_RESULT_invalid_parameter;
    }

    upd_reset_state();

    const scs_string_t game_id = parameters->common.game_id;
    if (upd_streq(game_id, "eurotrucks2"))
    {
        g_state.game = UPD_GAME_ETS2;
    }
    else if (upd_streq(game_id, "amtrucks"))
    {
        g_state.game = UPD_GAME_ATS;
    }

    // 事件必须先注册：配置事件在 init 返回后、其它回调之前就会发过来，漏了就拿不到货物信息。
    g_register_event(SCS_TELEMETRY_EVENT_configuration, upd_event_callback, NULL);
    g_register_event(SCS_TELEMETRY_EVENT_frame_end, upd_event_callback, NULL);
    g_register_event(SCS_TELEMETRY_EVENT_paused, upd_event_callback, NULL);
    g_register_event(SCS_TELEMETRY_EVENT_started, upd_event_callback, NULL);

    upd_register_channels();
    upd_open_shared_memory();
    upd_publish();

    return SCS_RESULT_ok;
}

__declspec(dllexport) SCSAPI_VOID scs_telemetry_shutdown(void)
{
    upd_close_shared_memory();

    if (g_unregister_event != NULL)
    {
        g_unregister_event(SCS_TELEMETRY_EVENT_configuration);
        g_unregister_event(SCS_TELEMETRY_EVENT_frame_end);
        g_unregister_event(SCS_TELEMETRY_EVENT_paused);
        g_unregister_event(SCS_TELEMETRY_EVENT_started);
    }

    g_register_event = NULL;
    g_unregister_event = NULL;
    g_register_channel = NULL;
    g_unregister_channel = NULL;
}

} // extern "C"
