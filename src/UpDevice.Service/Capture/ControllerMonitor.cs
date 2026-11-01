using UpDevice.Shared;
using UpDevice.Shared.Config;

namespace UpDevice.Service.Capture;

/// <summary>
/// 轮询 Xbox 控制器（XInput）。默认只看 1 号与 2 号（index 0/1），可配置为全部 4 个。
/// 存在意义：只用摇杆玩游戏时键鼠完全不动，不采集控制器就会被判成待机。
/// </summary>
public sealed class ControllerMonitor : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private readonly ActivityMonitor _activity;
    private readonly Func<ReportConfig> _config;
    private readonly CancellationTokenSource _cts = new();

    private volatile bool _inputDetected;

    public ControllerMonitor(ActivityMonitor activity, Func<ReportConfig> config)
    {
        _activity = activity;
        _config = config;
    }

    /// <summary>最近一次轮询是否检测到有效输入（仅供日志/调试用）。</summary>
    public bool InputDetected => _inputDetected;

    public void Start()
    {
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                Poll();
            }
            catch (Exception ex)
            {
                Logging.Warn($"控制器轮询失败: {ex.Message}");
            }

            try
            {
                await Task.Delay(PollInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void Poll()
    {
        var config = _config();
        if (!config.ControllerEnabled)
        {
            _inputDetected = false;
            return;
        }

        var count = config.ControllerAll ? 4u : 2u;
        var deadzone = config.ControllerDeadzone;
        var detected = false;

        for (uint index = 0; index < count; index++)
        {
            var result = Native.NativeMethods.XInputGetState(index, out var state);
            if (result != Native.NativeMethods.ErrorSuccess)
            {
                continue;
            }

            if (HasInput(in state.Gamepad, deadzone))
            {
                detected = true;
            }
        }

        _inputDetected = detected;
        if (detected)
        {
            _activity.NotifyControllerActivity();
        }
    }

    private static bool HasInput(in Native.NativeMethods.XINPUT_GAMEPAD pad, double deadzone)
    {
        if (pad.wButtons != 0)
        {
            return true;
        }

        var left = Normalize(pad.sThumbLX, pad.sThumbLY);
        var right = Normalize(pad.sThumbRX, pad.sThumbRY);
        if (left > deadzone || right > deadzone)
        {
            return true;
        }

        return pad.bLeftTrigger / 255.0 > deadzone || pad.bRightTrigger / 255.0 > deadzone;
    }

    private static double Normalize(short x, short y)
    {
        var fx = x / 32767.0;
        var fy = y / 32767.0;
        return Math.Sqrt((fx * fx) + (fy * fy));
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
