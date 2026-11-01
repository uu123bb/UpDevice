namespace UpDevice.Shared.Config;

/// <summary>
/// config.json 的完整结构。字段与托盘 Web 配置页一一对应。
/// </summary>
public sealed class AppConfig
{
    /// <summary>当前配置结构版本。加字段或改语义时 +1，并在 ConfigStore 里补迁移。</summary>
    public const int CurrentVersion = 2;

    /// <summary>
    /// 配置结构版本。**默认 0 表示「未标注版本」**（v1 时代写的文件没有这个字段），
    /// 加载后会被升级到 <see cref="CurrentVersion"/>。
    /// 不能把默认值设成 CurrentVersion，否则老配置会被误判成新版，迁移就永远不触发。
    /// </summary>
    public int Version { get; set; }

    /// <summary>开始上报 / 停止上报（对应配置页的切换式按钮）。</summary>
    public bool ReportingEnabled { get; set; } = true;

    public RemoteConfig Remote { get; set; } = new();

    public ReportConfig Report { get; set; } = new();

    /// <summary>SMTC 单独上报成一个设备时的配置（v2 新增）。</summary>
    public SmtcConfig Smtc { get; set; } = new();

    public TextConfig Texts { get; set; } = new();

    /// <summary>把越界值收敛到合理范围，避免手改配置把程序搞挂。</summary>
    public void Normalize()
    {
        Report ??= new ReportConfig();
        Remote ??= new RemoteConfig();
        Smtc ??= new SmtcConfig();
        Texts ??= new TextConfig();

        Remote.ServerUrl = (Remote.ServerUrl ?? string.Empty).Trim();
        Remote.Secret = Remote.Secret ?? string.Empty;
        Remote.DeviceId = string.IsNullOrWhiteSpace(Remote.DeviceId) ? Environment.MachineName : Remote.DeviceId.Trim();
        Remote.ShowName = string.IsNullOrWhiteSpace(Remote.ShowName) ? Remote.DeviceId : Remote.ShowName.Trim();

        Report.IntervalSeconds = Math.Clamp(Report.IntervalSeconds, 1, 3600);
        Report.IdleTimeoutSeconds = Math.Clamp(Report.IdleTimeoutSeconds, 0, 86400);
        Report.ControllerDeadzone = Math.Clamp(Report.ControllerDeadzone, 0.0, 1.0);

        Report.StatusFormat = Report.StatusFormat is StatusFormats.Process or StatusFormats.Window
            or StatusFormats.WindowThenProcess or StatusFormats.ProcessThenWindow
            ? Report.StatusFormat
            : StatusFormats.Window;

        Report.SmtcMode = Report.SmtcMode is SmtcModes.Off or SmtcModes.Idle or SmtcModes.Separate
            ? Report.SmtcMode
            : SmtcModes.Idle;

        Smtc.DeviceId = (Smtc.DeviceId ?? string.Empty).Trim();
        Smtc.ShowName = (Smtc.ShowName ?? string.Empty).Trim();
        Smtc.PendingRemoveId = (Smtc.PendingRemoveId ?? string.Empty).Trim();

        Texts.Shutdown = string.IsNullOrWhiteSpace(Texts.Shutdown) ? "设备关闭" : Texts.Shutdown;
        Texts.BatteryOnBattery = string.IsNullOrWhiteSpace(Texts.BatteryOnBattery) ? "未在充电 %battery%" : Texts.BatteryOnBattery;
        Texts.BatteryOnAc = string.IsNullOrWhiteSpace(Texts.BatteryOnAc) ? "正在充电 %battery%" : Texts.BatteryOnAc;
    }

    public static AppConfig CreateDefault() => new() { Version = CurrentVersion };
}

public sealed class RemoteConfig
{
    /// <summary>远端 Sleepy 实例地址，例如 https://sleepy.example.com</summary>
    public string ServerUrl { get; set; } = string.Empty;

    /// <summary>与服务端 SLEEPY_SECRET 一致的密钥。</summary>
    public string Secret { get; set; } = string.Empty;

    /// <summary>设备 ID（API 的 id 字段）。默认取机器名。</summary>
    public string DeviceId { get; set; } = Environment.MachineName;

    /// <summary>页面上显示的设备名。</summary>
    public string ShowName { get; set; } = Environment.MachineName;
}

public sealed class ReportConfig
{
    /// <summary>上报间隔（秒），默认 5。</summary>
    public int IntervalSeconds { get; set; } = 5;

    /// <summary>不重复上报：仅在状态发生变化时才发送，默认开启。</summary>
    public bool OnlyOnChange { get; set; } = true;

    /// <summary>上报形式，见 <see cref="StatusFormats"/>。</summary>
    public string StatusFormat { get; set; } = StatusFormats.Window;

    /// <summary>SMTC 上报模式，见 <see cref="SmtcModes"/>。</summary>
    public string SmtcMode { get; set; } = SmtcModes.Idle;

    /// <summary>超过该秒数无输入即视为待机，0 表示禁用。</summary>
    public int IdleTimeoutSeconds { get; set; } = 120;

    /// <summary>是否采集控制器输入（防止手柄游戏被判为待机）。</summary>
    public bool ControllerEnabled { get; set; }

    /// <summary>是否采集全部控制器；关闭时只看 1 号与 2 号。</summary>
    public bool ControllerAll { get; set; }

    /// <summary>控制器死区（0.00 - 1.00）。</summary>
    public double ControllerDeadzone { get; set; } = 0.10;

    /// <summary>便携式设备模式：上报电池与电源状态。</summary>
    public bool PortableMode { get; set; }
}

public sealed class TextConfig
{
    /// <summary>设备关机时上报的状态文案。</summary>
    public string Shutdown { get; set; } = "设备关闭";

    /// <summary>使用电池时的文案，%battery% 会替换为电量。</summary>
    public string BatteryOnBattery { get; set; } = "未在充电 %battery%";

    /// <summary>连接电源时的文案，%battery% 会替换为电量。</summary>
    public string BatteryOnAc { get; set; } = "正在充电 %battery%";
}

public static class StatusFormats
{
    public const string Process = "process";
    public const string Window = "window";
    public const string WindowThenProcess = "window+process";
    public const string ProcessThenWindow = "process+window";
}

public static class SmtcModes
{
    public const string Off = "off";

    /// <summary>仅在待机时把播放信息写进本设备的文案。</summary>
    public const string Idle = "idle";

    /// <summary>播放信息单独上报成一个设备。</summary>
    public const string Separate = "separate";
}

/// <summary>SMTC 单独上报成一个设备时的设置。</summary>
public sealed class SmtcConfig
{
    /// <summary>上报 SMTC 信息时使用的设备 ID，不能与本机设备 ID 相同。</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>上报 SMTC 信息时使用的设备名。</summary>
    public string ShowName { get; set; } = string.Empty;

    /// <summary>
    /// 待删除的 SMTC 设备 ID。关闭单独上报（或改了设备 ID）时由托盘写入，
    /// 主进程删除成功后清空 —— 这样即使当时主进程没在跑，下次启动也会补删。
    /// </summary>
    public string PendingRemoveId { get; set; } = string.Empty;
}
