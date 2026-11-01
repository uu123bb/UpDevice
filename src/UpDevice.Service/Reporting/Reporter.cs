using UpDevice.Service.Capture;
using UpDevice.Service.Config;
using UpDevice.Service.Media;

namespace UpDevice.Service.Reporting;

/// <summary>
/// 组装设备状态并上报。
/// 状态优先级（待机时）：SMTC 正在播放 &gt; 便携模式电池文案 &gt; 保留最后一次前台信息。
/// </summary>
public sealed class Reporter : IDisposable
{
    private readonly ConfigStore _store;
    private readonly ForegroundCapture _foreground;
    private readonly ActivityMonitor _activity;
    private readonly BatteryMonitor _battery;
    private readonly SmtcMonitor _smtc;
    private readonly SleepyClient _client;
    private readonly bool _dryRun;

    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();

    private (bool Using, string Status)? _lastSent;
    private string? _lastActiveStatus;
    private bool _warnedNoServer;

    public Reporter(
        ConfigStore store,
        ForegroundCapture foreground,
        ActivityMonitor activity,
        BatteryMonitor battery,
        SmtcMonitor smtc,
        SleepyClient client,
        bool dryRun)
    {
        _store = store;
        _foreground = foreground;
        _activity = activity;
        _battery = battery;
        _smtc = smtc;
        _client = client;
        _dryRun = dryRun;

        // 配置变了就清掉去重缓存，让新配置下一轮立刻生效。
        _store.Reloaded += _ => ResetDedupe();
    }

    public void Start()
    {
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void ResetDedupe()
    {
        lock (_gate)
        {
            _lastSent = null;
            _lastActiveStatus = null;
            _warnedNoServer = false;
        }
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var config = _store.Current;
            try
            {
                await TickAsync(config, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Logging.Error($"上报循环异常: {ex.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, config.Report.IntervalSeconds)), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task TickAsync(AppConfig config, CancellationToken token)
    {
        if (!config.ReportingEnabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(config.Remote.ServerUrl))
        {
            if (!_warnedNoServer)
            {
                _warnedNoServer = true;
                Logging.Warn("尚未配置远端 Sleepy 实例，暂不上报（配置后会自动开始）");
            }

            return;
        }

        var (usingFlag, status) = Compose(config);

        lock (_gate)
        {
            if (config.Report.OnlyOnChange && _lastSent is { } last && last.Using == usingFlag && last.Status == status)
            {
                return;
            }
        }

        var payload = new DevicePayload
        {
            Id = config.Remote.DeviceId,
            ShowName = config.Remote.ShowName,
            Using = usingFlag,
            Status = status,
        };

        if (_dryRun)
        {
            Logging.Info($"[DRY-RUN] using={usingFlag} status=\"{status}\"");
            Remember(usingFlag, status);
            return;
        }

        var ok = await _client.SendAsync(config.Remote, payload, token).ConfigureAwait(false);
        if (ok)
        {
            Logging.Info($"已上报 using={usingFlag} status=\"{status}\"");
            Remember(usingFlag, status);
        }
    }

    private void Remember(bool usingFlag, string status)
    {
        lock (_gate)
        {
            _lastSent = (usingFlag, status);
        }
    }

    /// <summary>按当前各项采集结果算出要上报的状态。</summary>
    public (bool Using, string Status) Compose(AppConfig config)
    {
        var foreground = _foreground.Current;
        var idle = _activity.IsIdle(config.Report.IdleTimeoutSeconds, config.Report.ControllerEnabled);

        if (!idle)
        {
            var active = FormatForeground(foreground, config.Report.StatusFormat);
            if (active.Length > 0)
            {
                _lastActiveStatus = active;
            }

            return (true, active.Length > 0 ? active : _lastActiveStatus ?? string.Empty);
        }

        // 待机：优先让 SMTC 覆盖（"有 SMTC 信息盖过去"）。
        if (config.Report.SmtcMode == SmtcModes.Idle && _smtc.Current is { } playing)
        {
            return (false, $"{playing.Source} - {playing.Title}");
        }

        // 便携模式：待机时用电池文案。
        if (config.Report.PortableMode && _battery.Format(config.Texts) is { } batteryText)
        {
            return (false, batteryText);
        }

        // 其余情况保留最后一次前台信息，只是标记为离线。
        var retained = _lastActiveStatus ?? FormatForeground(foreground, config.Report.StatusFormat);
        return (false, retained);
    }

    private static string FormatForeground(ForegroundInfo info, string format)
    {
        var process = info.ProcessName;
        var title = info.WindowTitle;

        return format switch
        {
            StatusFormats.Process => process,
            StatusFormats.WindowThenProcess => Join(title, process),
            StatusFormats.ProcessThenWindow => Join(process, title),
            _ => string.IsNullOrWhiteSpace(title) ? process : title,
        };
    }

    private static string Join(string first, string second)
    {
        if (string.IsNullOrWhiteSpace(second))
        {
            return first ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(first))
        {
            return second;
        }

        return $"{first} ({second})";
    }

    /// <summary>
    /// 收到关机信号时上报离线（只置 using=false，不调用 /api/device/remove，设备保留在页面上）。
    /// 在窗口消息线程上同步等待，所以给 3 秒预算，超时就放弃，不能卡住注销流程。
    /// </summary>
    public void ReportShutdown()
    {
        try
        {
            var config = _store.Current;
            if (!config.ReportingEnabled || string.IsNullOrWhiteSpace(config.Remote.ServerUrl))
            {
                return;
            }

            var payload = new DevicePayload
            {
                Id = config.Remote.DeviceId,
                ShowName = config.Remote.ShowName,
                Using = false,
                Status = config.Texts.Shutdown,
            };

            Logging.Info($"收到关机信号，上报离线 status=\"{config.Texts.Shutdown}\"");

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var ok = _client.SendAsync(config.Remote, payload, timeout.Token).GetAwaiter().GetResult();
            Logging.Info(ok ? "关机上报完成" : "关机上报未成功");
        }
        catch (Exception ex)
        {
            Logging.Warn($"关机上报失败: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
