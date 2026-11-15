# 官方 SCS 遥测 SDK 头文件

这些 `.h` 是 **SCS Software 官方 Telemetry SDK** 的头文件，原样拷贝，**不要手改**。
项目自己的逻辑只写在上一层的 `plugin.cpp` 里。

- 来源：<https://github.com/RenCloud/scs-sdk-plugin> 的 `scs_sdk/include/`（该仓库镜像官方 SDK）
- 版本：SDK 1.12 前后的头文件包（含 ETS2 1.18 / ATS 1.05 的游戏版本常量）
- 用途：`scs_telemetry_init` / `scs_telemetry_shutdown` 的 ABI、通道名常量、配置属性名常量

## 更新方式

官方 SDK 出新版时，整目录替换即可，但**必须**重新核对：

1. `scs_telemetry_init_params_v100_t` 的字段顺序与大小（`scs_check_size` 会在编译期校验，错了编不过）。
2. `plugin.cpp` 里用到的通道名是否还存在（`common/scssdk_telemetry_*_channels.h`）。
3. 与 C# 侧的共享内存布局无关 —— 那是我们自己的结构体，见 `TruckTelemetryReader.cs`。
