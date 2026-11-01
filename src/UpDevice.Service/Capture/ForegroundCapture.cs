using System.Diagnostics;
using System.Text;
using UpDevice.Shared;

namespace UpDevice.Service.Capture;

/// <summary>一次前台窗口快照。</summary>
public sealed record ForegroundInfo(string ProcessName, string WindowTitle, string ClassName, DateTime CapturedAt)
{
    public static readonly ForegroundInfo Empty = new(string.Empty, string.Empty, string.Empty, DateTime.MinValue);

    public bool IsEmpty => WindowTitle.Length == 0 && ProcessName.Length == 0;
}

/// <summary>
/// 轮询前台窗口，取进程名 / 窗口标题 / 窗口类名。
/// 轮询而不是挂钩子：前台切换事件（SetWinEventHook）虽然更实时，但对手柄、UWP 应用容易出现漏事件，轮询 400ms 已足够。
/// </summary>
public sealed class ForegroundCapture : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(400);

    private readonly Dictionary<uint, string> _processNameCache = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly uint _selfProcessId = (uint)Environment.ProcessId;

    private volatile ForegroundInfo _current = ForegroundInfo.Empty;
    private volatile bool _locked;
    private volatile bool _desktop;
    private Task? _loop;

    public ForegroundInfo Current => _current;

    /// <summary>锁屏中 —— 应当直接置为离线。</summary>
    public bool IsLocked => _locked;

    /// <summary>前台是桌面 —— 文案显示待机，但在不在线仍由空闲时间决定。</summary>
    public bool IsDesktop => _desktop;

    public void Start()
    {
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                CaptureOnce();
            }
            catch (Exception ex)
            {
                Logging.Warn($"前台窗口采集失败: {ex.Message}");
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

    private void CaptureOnce()
    {
        // 锁屏时前台窗口可能是空的或停在 LockApp 上，先判一次会话状态。
        // 会话没锁定就先把锁屏标记清掉，避免解锁后一直停在离线。
        var sessionLocked = Native.NativeMethods.IsSessionLocked();
        if (!sessionLocked)
        {
            _locked = false;
        }

        var hWnd = Native.NativeMethods.GetForegroundWindow();
        if (hWnd == IntPtr.Zero)
        {
            if (sessionLocked)
            {
                _locked = true;
            }

            return;
        }

        Native.NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
        if (pid == 0 || pid == _selfProcessId)
        {
            return;
        }

        var title = Native.NativeMethods.GetWindowTextSafe(hWnd);
        var className = Native.NativeMethods.GetClassNameSafe(hWnd);
        var process = ResolveProcessName(pid);

        var info = new ForegroundInfo(process, title, className, DateTime.Now);

        switch (ForegroundFilter.Evaluate(info, sessionLocked))
        {
            case ForegroundVerdict.Locked:
                // 锁屏：直接离线。
                _locked = true;
                return;

            case ForegroundVerdict.Desktop:
                // 桌面：保留 _current（回到应用能立刻恢复），只是文案显示待机。
                _desktop = true;
                return;

            case ForegroundVerdict.Skip:
                // 临时浮层：什么都不改，保留上一次的判断结果。
                return;

            default:
                _desktop = false;
                _current = info;
                return;
        }
    }

    private string ResolveProcessName(uint pid)
    {
        lock (_processNameCache)
        {
            if (_processNameCache.TryGetValue(pid, out var cached))
            {
                return cached;
            }
        }

        // 优先取带扩展名的映像名（workbuddy.exe），拿不到再退回 ProcessName + .exe。
        var name = Native.NativeMethods.GetProcessImageName(pid);
        if (string.IsNullOrEmpty(name))
        {
            try
            {
                using var process = Process.GetProcessById((int)pid);
                name = process.ProcessName + ".exe";
            }
            catch
            {
                name = string.Empty;
            }
        }

        if (name.Length > 0)
        {
            lock (_processNameCache)
            {
                if (_processNameCache.Count > 256)
                {
                    _processNameCache.Clear();
                }

                _processNameCache[pid] = name;
            }
        }

        return name;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
