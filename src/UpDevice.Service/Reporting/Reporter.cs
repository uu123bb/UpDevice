using UpDevice.Service.Capture;
using UpDevice.Service.Media;
using UpDevice.Shared;
using UpDevice.Shared.Config;

namespace UpDevice.Service.Reporting;

/// <summary>
/// 组装设备状态并上报。
///
/// 文案结构：[电池状态]核心内容
///   核心内容 = 前台信息（活跃）/ 待机（无输入）/ SMTC 播放信息（待机且有播放）
/// 示例：
///   workbuddy.exe
///   WorkBuddy（workbuddy.exe）
///   待机
///   泠鸢yousa - 一梦千霄（NetEase）
///   [未在充电 98%]WorkBuddy
///   [正在充电 50%]American Truck Simulator（正在使用控制器）
///   [设备关闭]
/// </summary>
public sealed class Reporter : IDisposable
{
    /// <summary>无输入时的固定文案。</summary>
    private const string IdleText = "待机";

    /// <summary>控制器正在被使用时追加的标记。</summary>
    private const string ControllerInUseText = "（正在使用控制器）";

    /// <summary>控制器输入后多少秒内仍认为「正在使用控制器」。</summary>
    private const double ControllerActiveSeconds = 10;

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
    private (bool Using, string Status)? _lastSmtcSent;
    private string? _lastForegroundText;
    private bool _warnedNoServer;
    private bool _warnedSmtcInvalid;
    private bool _smtcDeviceDeleted;

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
            _lastSmtcSent = null;
            _lastForegroundText = null;
            _warnedNoServer = false;
            _warnedSmtcInvalid = false;
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

        // 关闭 SMTC 单独上报后，要把留下那个设备删掉。托盘的改动可能发生在主进程没运行时，
        // 所以这一步放在循环里做，标记只有删除成功才清除。
        await ProcessPendingRemovalAsync(config, token).ConfigureAwait(false);

        await SendAsync(config, config.Remote.DeviceId, config.Remote.ShowName, usingFlag, status, isMain: true, token)
            .ConfigureAwait(false);

        if (config.Report.SmtcMode == SmtcModes.Separate)
        {
            await SyncSmtcDeviceAsync(config, token).ConfigureAwait(false);
        }
    }

    /// <summary>主设备与 SMTC 设备各自去重、各自上报。</summary>
    private async Task SendAsync(
        AppConfig config,
        string deviceId,
        string showName,
        bool usingFlag,
        string status,
        bool isMain,
        CancellationToken token)
    {
        lock (_gate)
        {
            var last = isMain ? _lastSent : _lastSmtcSent;
            if (config.Report.OnlyOnChange && last is { } previous
                && previous.Using == usingFlag && previous.Status == status)
            {
                return;
            }
        }

        var payload = new DevicePayload
        {
            Id = deviceId,
            ShowName = showName,
            Using = usingFlag,
            AppName = status,
            Status = status,
        };

        if (_dryRun)
        {
            Logging.Info($"[DRY-RUN] {deviceId} using={usingFlag} status=\"{status}\"");
            Remember(usingFlag, status, isMain);
            return;
        }

        var ok = await _client.SendAsync(config.Remote, payload, token).ConfigureAwait(false);
        if (ok)
        {
            Logging.Info($"已上报 [{deviceId}] using={usingFlag} status=\"{status}\"");
            Remember(usingFlag, status, isMain);
        }
    }

    private async Task ProcessPendingRemovalAsync(AppConfig config, CancellationToken token)
    {
        var pending = config.Smtc.PendingRemoveId;
        if (string.IsNullOrWhiteSpace(pending))
        {
            return;
        }

        var removed = await _client.RemoveDeviceAsync(config.Remote, pending, token).ConfigureAwait(false);
        if (!removed)
        {
            return;
        }

        config.Smtc.PendingRemoveId = string.Empty;
        _store.Save(config);
        Logging.Info($"SMTC 设备 {pending} 已删除，标记清除");
    }

    private void Remember(bool usingFlag, string status, bool isMain)
    {
        lock (_gate)
        {
            if (isMain)
            {
                _lastSent = (usingFlag, status);
            }
            else
            {
                _lastSmtcSent = (usingFlag, status);
            }
        }
    }

    /// <summary>按当前各项采集结果算出要上报的状态。</summary>
    public (bool Using, string Status) Compose(AppConfig config)
    {
        var idle = IsDeviceIdle(config);
        var core = idle ? ComposeIdle(config) : ComposeActive(config);

        var prefix = string.Empty;
        if (config.Report.PortableMode && _battery.Format(config.Texts) is { } batteryText)
        {
            prefix = $"[{batteryText}]";
        }

        return (!idle, prefix + core);
    }

    private string ComposeIdle(AppConfig config)
    {
        // 只有「仅在待机时上报」才把播放信息写进本设备；单独上报模式下它归 SMTC 设备。
        if (config.Report.SmtcMode == SmtcModes.Idle && _smtc.Current is { IsPlaying: true } playing)
        {
            return FormatNowPlaying(playing);
        }

        return IdleText;
    }

    /// <summary>
    /// 同步 SMTC 设备。
    ///
    /// 规则：
    ///   没有可用的 SMTC 会话 → **删除该设备**（不挂一个待机的僵尸设备）
    ///   暂停播放             → 保留歌曲信息，但置为离线
    ///   本机待机             → 置为离线（不管是否在播），信息保留
    ///   正在播放且本机活跃   → 在线
    /// </summary>
    private async Task SyncSmtcDeviceAsync(AppConfig config, CancellationToken token)
    {
        var smtc = config.Smtc;

        if (string.IsNullOrWhiteSpace(smtc.DeviceId) || string.IsNullOrWhiteSpace(smtc.ShowName))
        {
            WarnSmtcOnce("SMTC 单独上报已开启，但 SMTC 设备 ID 或设备名为空，本次跳过");
            return;
        }

        if (string.Equals(smtc.DeviceId, config.Remote.DeviceId, StringComparison.OrdinalIgnoreCase))
        {
            WarnSmtcOnce("SMTC 设备 ID 不能与本机设备 ID 相同，本次跳过");
            return;
        }

        var nowPlaying = _smtc.Current;

        if (nowPlaying is null)
        {
            // 只删一次，避免每轮都打一次 remove。
            if (_smtcDeviceDeleted)
            {
                return;
            }

            var removed = await _client.RemoveDeviceAsync(config.Remote, smtc.DeviceId, token).ConfigureAwait(false);
            if (removed)
            {
                _smtcDeviceDeleted = true;
                lock (_gate)
                {
                    _lastSmtcSent = null;
                }

                Logging.Info($"没有可用的 SMTC 会话，已删除设备 {smtc.DeviceId}");
            }

            return;
        }

        // 会话重新出现，恢复上报。
        _smtcDeviceDeleted = false;

        var idle = IsDeviceIdle(config);
        var usingFlag = nowPlaying.IsPlaying && !idle;
        await SendAsync(config, smtc.DeviceId, smtc.ShowName, usingFlag, FormatNowPlaying(nowPlaying), isMain: false, token)
            .ConfigureAwait(false);
    }

    /// <summary>本机是否处于待机：无有效前台窗口，或超过超时时间没有输入。</summary>
    private bool IsDeviceIdle(AppConfig config)
    {
        return _foreground.Current.IsEmpty
            || _activity.IsIdle(config.Report.IdleTimeoutSeconds, config.Report.ControllerEnabled);
    }

    private void WarnSmtcOnce(string message)
    {
        if (_warnedSmtcInvalid)
        {
            return;
        }

        _warnedSmtcInvalid = true;
        Logging.Warn(message);
    }

    private string ComposeActive(AppConfig config)
    {
        var text = FormatForeground(_foreground.Current, config.Report.StatusFormat);

        if (text.Length == 0)
        {
            // 前台窗口暂时取不到（例如切到锁屏），沿用上一次的内容而不是发空字符串。
            text = _lastForegroundText ?? IdleText;
        }
        else
        {
            _lastForegroundText = text;
        }

        if (config.Report.ControllerEnabled && _activity.SecondsSinceControllerInput() <= ControllerActiveSeconds)
        {
            text += ControllerInUseText;
        }

        return text;
    }

    private static string FormatNowPlaying(NowPlaying playing)
    {
        return string.IsNullOrWhiteSpace(playing.Artist)
            ? $"{playing.Title}（{playing.Source}）"
            : $"{playing.Artist} - {playing.Title}（{playing.Source}）";
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

    /// <summary>用全角括号拼接，与文案约定一致（窗口名（进程名））。</summary>
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

        return $"{first}（{second}）";
    }

    /// <summary>收到关机信号时上报离线（不调用 /api/device/remove，设备保留在页面上）。</summary>
    public void ReportShutdown()
    {
        ReportOffline($"[{_store.Current.Texts.Shutdown}]");
    }

    /// <summary>立即上报离线，供托盘「退出并停止上报」等场景使用。</summary>
    public void ReportOffline(string status)
    {
        try
        {
            var config = _store.Current;
            if (string.IsNullOrWhiteSpace(config.Remote.ServerUrl))
            {
                return;
            }

            var payload = new DevicePayload
            {
                Id = config.Remote.DeviceId,
                ShowName = config.Remote.ShowName,
                Using = false,
                AppName = status,
                Status = status,
            };

            Logging.Info($"上报离线 status=\"{status}\"");

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var ok = _client.SendAsync(config.Remote, payload, timeout.Token).GetAwaiter().GetResult();
            Logging.Info(ok ? "离线上报完成" : "离线上报未成功");
        }
        catch (Exception ex)
        {
            Logging.Warn($"离线上报失败: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
