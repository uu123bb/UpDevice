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
    private Task? _loop;

    public ForegroundInfo Current => _current;

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
        var hWnd = Native.NativeMethods.GetForegroundWindow();
        if (hWnd == IntPtr.Zero)
        {
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

        // 桌面、锁屏之类没有标题的窗口不覆盖已有信息，避免状态在息屏时抖成空。
        if (info.IsEmpty)
        {
            return;
        }

        _current = info;
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
