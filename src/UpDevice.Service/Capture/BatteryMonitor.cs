using UpDevice.Shared;
using UpDevice.Shared.Config;

namespace UpDevice.Service.Capture;

/// <summary>
/// 电池与电源状态（便携式设备模式）。用 GetSystemPowerStatus 而不是 Win32 电池 API：
/// 只需要电量百分比与是否接电源，没必要上 SetupAPI。
/// </summary>
public sealed class BatteryMonitor : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly CancellationTokenSource _cts = new();

    private volatile bool _hasBattery;
    private volatile int _percent = -1;
    private volatile bool _onAc;

    /// <summary>是否拿到了有效电量。</summary>
    public bool IsAvailable => _hasBattery && _percent >= 0;

    public bool OnAc => _onAc;

    public int Percent => _percent;

    public void Start()
    {
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Poll();
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
        try
        {
            if (!Native.NativeMethods.GetSystemPowerStatus(out var status))
            {
                return;
            }

            _onAc = status.ACLineStatus == 1;
            _percent = status.BatteryLifePercent == 255 ? -1 : status.BatteryLifePercent;
            _hasBattery = _percent >= 0;
        }
        catch (Exception ex)
        {
            Logging.Warn($"电池状态读取失败: {ex.Message}");
        }
    }

    /// <summary>按文案模板生成电池状态文本；拿不到电量时返回 null。</summary>
    public string? Format(TextConfig texts)
    {
        if (!IsAvailable)
        {
            return null;
        }

        var template = _onAc ? texts.BatteryOnAc : texts.BatteryOnBattery;
        return template.Replace("%battery%", $"{_percent}%", StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
