using System.Text;
using UpDevice.Shared.Config;

namespace UpDevice.Shared.Trucking;

/// <summary>一个模板变量的元信息，设置页直接用它渲染变量表（代码是唯一事实来源）。</summary>
/// <param name="Name">变量名，模板里写成 %name%。</param>
/// <param name="Aliases">兼容写法（例如需求里的拼写错误）。</param>
/// <param name="Label">中文名。</param>
/// <param name="Explain">渲染出来的样子 / 取值说明。</param>
public sealed record TruckingVariable(string Name, string[] Aliases, string Label, string Explain);

/// <summary>
/// %变量% 模板引擎。只做字符串拼装，不碰任何采集逻辑，方便单独测试。
/// </summary>
public static class TruckingFormat
{
    /// <summary>模板里支持的全部变量。</summary>
    public static readonly IReadOnlyList<TruckingVariable> Variables = new[]
    {
        new TruckingVariable("game", Array.Empty<string>(), "当前游戏", "American Truck Simulator / Euro Truck Simulator 2"),
        new TruckingVariable("driver_status", new[] { "dirver_status" }, "驾驶员状态", "工作中 / 自由如风"),
        new TruckingVariable("driver_sleepy", Array.Empty<string>(), "驾驶员仍可驾驶", "下次休息 2 时 15 分"),
        new TruckingVariable("speed", Array.Empty<string>(), "车速", "85 kph / 53 mph"),
        new TruckingVariable("fuel_left", Array.Empty<string>(), "燃油剩余", "可用燃油 53%"),
        new TruckingVariable("fuel_distance", Array.Empty<string>(), "续驶里程", "仍可行驶 780 km / 485 mi"),
        new TruckingVariable("damage_vehicle", Array.Empty<string>(), "车损", "车损 3%"),
        new TruckingVariable("damage_trailer", Array.Empty<string>(), "挂损", "挂损 0%"),
        new TruckingVariable("damage_cargo", Array.Empty<string>(), "货损", "货损 0%"),
        new TruckingVariable("job_cargo", Array.Empty<string>(), "货物信息", "医疗用品（无任务时为 无任务）"),
        new TruckingVariable("job_lefttime", Array.Empty<string>(), "任务剩余时间", "任务剩余 3 时 20 分（游戏未提供截止时间时为 任务剩余 --）"),
        new TruckingVariable("job_destination", Array.Empty<string>(), "任务终点", "柏林 - 物流中心"),
        new TruckingVariable("nav_speed_limit", Array.Empty<string>(), "限速", "限速 80 kph"),
        new TruckingVariable("nav_leftdistance", Array.Empty<string>(), "导航剩余里程", "剩余 128 km / 80 mi"),
        new TruckingVariable("nav_lefttime", Array.Empty<string>(), "导航剩余时间", "预计使用 1 时 32 分"),
        new TruckingVariable("nav_eta", Array.Empty<string>(), "预计到达时间", "18:45"),
        new TruckingVariable("smtc", Array.Empty<string>(), "当前音乐", "泠鸢yousa - 一梦千霄（NetEase）"),
    };

    /// <summary>渲染模板。<c>%%</c> 输出一个百分号，认不出的变量原样保留（方便用户看出拼错）。</summary>
    public static string Render(string? format, TruckTelemetrySnapshot telemetry, string? smtcText, bool imperial)
    {
        var text = string.IsNullOrEmpty(format) ? TruckingConfig.DefaultFormat : format;
        var builder = new StringBuilder(text.Length + 32);

        var index = 0;
        while (index < text.Length)
        {
            var start = text.IndexOf('%', index);
            if (start < 0)
            {
                builder.Append(text, index, text.Length - index);
                break;
            }

            builder.Append(text, index, start - index);

            var end = text.IndexOf('%', start + 1);
            if (end < 0)
            {
                // 落单的 %，原样留着。
                builder.Append(text, start, text.Length - start);
                break;
            }

            var token = text[(start + 1)..end];
            if (token.Length == 0)
            {
                builder.Append('%');
            }
            else
            {
                var value = TryResolve(token, telemetry, smtcText, imperial);
                builder.Append(value ?? text[start..(end + 1)]);
            }

            index = end + 1;
        }

        return builder.ToString().Trim();
    }

    /// <summary>按变量名取渲染结果；认不出返回 null。</summary>
    public static string? TryResolve(string token, TruckTelemetrySnapshot telemetry, string? smtcText, bool imperial)
    {
        var name = token.Trim().ToLowerInvariant();

        // 需求原文里把 driver_status 写成了 dirver_status，两种都认。
        if (name == "dirver_status")
        {
            name = "driver_status";
        }

        return name switch
        {
            "game" => GameName(telemetry),
            "driver_status" => telemetry.JobActive ? "工作中" : "自由如风",
            "driver_sleepy" => DriverSleepy(telemetry),
            "speed" => Speed(telemetry.SpeedMps, imperial),
            "fuel_left" => FuelLeft(telemetry),
            "fuel_distance" => FuelDistance(telemetry.FuelRangeKm, imperial),
            "damage_vehicle" => $"车损 {Percent(telemetry.WearTruck)}",
            "damage_trailer" => $"挂损 {Percent(telemetry.WearTrailer)}",
            "damage_cargo" => $"货损 {Percent(telemetry.CargoDamage)}",
            "job_cargo" => telemetry.JobActive || telemetry.HasJobCargo ? CargoText(telemetry) : "无任务",
            "job_lefttime" => JobLeftTime(telemetry),
            "job_destination" => JobDestination(telemetry),
            "nav_speed_limit" => SpeedLimit(telemetry.SpeedLimitMps, imperial),
            "nav_leftdistance" => NavDistance(telemetry.NavDistanceMeters, imperial),
            "nav_lefttime" => NavTime(telemetry.NavTimeSeconds),
            "nav_eta" => NavEta(telemetry),
            "smtc" => string.IsNullOrWhiteSpace(smtcText) ? "无播放" : smtcText.Trim(),
            _ => null,
        };
    }

    private static string GameName(TruckTelemetrySnapshot telemetry)
    {
        if (!string.IsNullOrEmpty(telemetry.GameName))
        {
            return telemetry.GameName;
        }

        return TruckGames.FromTelemetryGameId(telemetry.GameId)?.DisplayName ?? "未知游戏";
    }

    private static string DriverSleepy(TruckTelemetrySnapshot telemetry)
    {
        // rest.stop 是「距下次必须休息还有多少游戏内分钟」，负数表示已经超时。
        var minutes = telemetry.RestStopMinutes;
        if (minutes <= 0)
        {
            return "需要休息";
        }

        return $"下次休息 {Duration(minutes)}";
    }

    private static string Speed(double metersPerSecond, bool imperial)
    {
        var value = Math.Abs(metersPerSecond);
        return imperial
            ? $"{Round(value * 2.236936)} mph"
            : $"{Round(value * 3.6)} kph";
    }

    private static string SpeedLimit(double metersPerSecond, bool imperial)
    {
        if (metersPerSecond <= 0.05)
        {
            return "限速 --";
        }

        return $"限速 {Speed(metersPerSecond, imperial)}";
    }

    private static string FuelLeft(TruckTelemetrySnapshot telemetry)
    {
        if (telemetry.FuelCapacityLiters <= 0.01)
        {
            return "可用燃油 --";
        }

        var percent = Math.Clamp(telemetry.FuelLiters / telemetry.FuelCapacityLiters * 100.0, 0, 100);
        return $"可用燃油 {Round(percent)}%";
    }

    private static string FuelDistance(double rangeKm, bool imperial)
    {
        if (rangeKm <= 0.01)
        {
            return "仍可行驶 --";
        }

        return imperial
            ? $"仍可行驶 {Round(rangeKm * 0.621371)} mi"
            : $"仍可行驶 {Round(rangeKm)} km";
    }

    private static string JobDestination(TruckTelemetrySnapshot telemetry)
    {
        var city = telemetry.DestinationCity;
        var company = telemetry.DestinationCompany;

        if (city.Length == 0 && company.Length == 0)
        {
            return telemetry.JobActive ? "任务终点 --" : "无任务";
        }

        if (city.Length == 0)
        {
            return company;
        }

        return company.Length == 0 ? city : $"{city} - {company}";
    }

    private static string CargoText(TruckTelemetrySnapshot telemetry)
    {
        if (!telemetry.HasJobCargo)
        {
            return "货物 --";
        }

        return telemetry.CargoMassKg > 0
            ? $"{telemetry.CargoName}（{telemetry.CargoMassKg / 1000.0:0.#} t）"
            : telemetry.CargoName;
    }

    /// <summary>
    /// 任务剩余时间 = 截止时间 - 当前游戏时间。
    /// SCS 遥测 SDK 目前不提供任务截止时间（job 只暴露货损），所以这里通常打不出数字。
    /// </summary>
    private static string JobLeftTime(TruckTelemetrySnapshot telemetry)
    {
        if (!telemetry.JobActive)
        {
            return "无任务";
        }

        if (telemetry.JobDeadlineMinutes <= 0)
        {
            return "任务剩余 --";
        }

        var left = telemetry.JobDeadlineMinutes - telemetry.GameTimeMinutes;
        return left <= 0 ? "任务已超时" : $"任务剩余 {Duration(left)}";
    }

    private static string NavDistance(double meters, bool imperial)
    {
        if (meters <= 1)
        {
            return "剩余 0 km";
        }

        return imperial
            ? $"剩余 {Round(meters / 1609.344)} mi"
            : $"剩余 {Round(meters / 1000.0)} km";
    }

    private static string NavTime(double seconds)
    {
        if (seconds <= 1)
        {
            return "预计使用 --";
        }

        return $"预计使用 {Duration((int)Math.Round(seconds / 60.0))}";
    }

    /// <summary>ETA 用**游戏内时间**算：导航剩余时间加在游戏时钟上，和仪表盘的到达时间一致。</summary>
    private static string NavEta(TruckTelemetrySnapshot telemetry)
    {
        if (telemetry.NavTimeSeconds <= 1)
        {
            return "--:--";
        }

        var minutes = telemetry.GameTimeMinutes + (int)Math.Round(telemetry.NavTimeSeconds / 60.0);
        var ofDay = ((minutes % 1440) + 1440) % 1440;
        return $"{ofDay / 60:00}:{ofDay % 60:00}";
    }

    /// <summary>把分钟数写成「3 时 20 分」；不足一小时只写「20 分」。</summary>
    private static string Duration(int totalMinutes)
    {
        var minutes = Math.Max(0, totalMinutes);
        var hours = minutes / 60;
        var rest = minutes % 60;
        return hours > 0 ? $"{hours} 时 {rest} 分" : $"{rest} 分";
    }

    private static int Percent(double ratio)
    {
        return Round(Math.Clamp(ratio, 0, 1) * 100.0);
    }

    private static int Round(double value)
    {
        return (int)Math.Round(value, MidpointRounding.AwayFromZero);
    }

    /// <summary>设置页预览用的一帧假数据：够真实，又能同时看到所有变量的形状。</summary>
    public static TruckTelemetrySnapshot Sample()
    {
        return new TruckTelemetrySnapshot
        {
            GameId = TruckGames.Ets2.TelemetryGameId,
            GameName = TruckGames.Ets2.DisplayName,
            Paused = false,
            JobActive = true,
            TrailerAttached = true,
            GameTimeMinutes = (14 * 60) + 52,
            SpeedMps = 23.6,
            SpeedLimitMps = 22.2,
            FuelLiters = 372,
            FuelCapacityLiters = 700,
            FuelRangeKm = 780,
            WearTruck = 0.032,
            WearTrailer = 0.004,
            CargoDamage = 0,
            NavDistanceMeters = 128_400,
            NavTimeSeconds = 5_520,
            RestStopMinutes = 135,
            CargoMassKg = 18_500,
            JobDeadlineMinutes = (17 * 60) + 12,
            CargoName = "医疗用品",
            DestinationCity = "柏林",
            DestinationCompany = "物流中心",
            AgeMs = 0,
        };
    }

    /// <summary>预览用的示例音乐文案。</summary>
    public const string SampleSmtc = "泠鸢yousa - 一梦千霄（NetEase）";
}
