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
    private volatile bool _idleSurface;
    private Task? _loop;

    public ForegroundInfo Current => _current;

    /// <summary>前台是桌面或锁屏 —— 用户没在用应用，应当按待机处理。</summary>
    public bool IsIdleSurface => _idleSurface;

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
        var sessionLocked = Native.NativeMethods.IsSessionLocked();

        var hWnd = Native.NativeMethods.GetForegroundWindow();
        if (hWnd == IntPtr.Zero)
        {
            if (sessionLocked)
            {
                _idleSurface = true;
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
            case ForegroundVerdict.Idle:
                // 桌面 / 锁屏：确实是「不在用」，由 Reporter 报待机并置离线。
                // 这里保留 _current，回到应用时能立刻恢复。
                _idleSurface = true;
                return;

            case ForegroundVerdict.Skip:
                // 临时浮层：不更新，保留上一次的应用名。
                return;

            default:
                _idleSurface = false;
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
