using System.Runtime.InteropServices;

namespace UpDevice.Service.Native;

/// <summary>
/// 隐藏窗口，用来接收系统消息与托盘指令。
///
/// 关键点：这里创建的是**普通顶层窗口**（Style = 0，不带 WS_VISIBLE，坐标丢到屏幕外），
/// 不是 HWND_MESSAGE 消息专用窗口。消息专用窗口收不到 WM_QUERYENDSESSION / WM_ENDSESSION
/// 这类系统广播，用它就永远等不到关机通知。
/// </summary>
internal sealed class HiddenMessageWindow : NativeWindow, IDisposable
{
    private const int WM_QUERYENDSESSION = 0x0011;
    private const int WM_ENDSESSION = 0x0016;
    private const int WM_COPYDATA = 0x004A;

    /// <summary>自定义消息：请求退出消息循环（跨线程投递，必须回到 UI 线程执行）。</summary>
    private const int WM_REQUEST_EXIT = 0x8000 + 1;

    /// <summary>系统即将关机/注销。</summary>
    public event Action? SessionEnding;

    /// <summary>来自托盘进程的指令（WM_COPYDATA 的文本内容）。</summary>
    public event Action<string>? CommandReceived;

    public HiddenMessageWindow()
    {
        var createParams = new CreateParams
        {
            Caption = "UpDevice.Service.Hidden",
            X = -32000,
            Y = -32000,
            Width = 0,
            Height = 0,
            Style = 0,
        };

        CreateHandle(createParams);
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_QUERYENDSESSION:
                // 关机前把离线状态发出去，再放行。
                SessionEnding?.Invoke();
                m.Result = new IntPtr(1);
                return;

            case WM_ENDSESSION:
                if (m.WParam != IntPtr.Zero)
                {
                    SessionEnding?.Invoke();
                }

                m.Result = IntPtr.Zero;
                return;

            case WM_COPYDATA:
                var command = ReadCopyDataString(m.LParam);
                if (!string.IsNullOrWhiteSpace(command))
                {
                    CommandReceived?.Invoke(command);
                }

                m.Result = new IntPtr(1);
                return;

            case WM_REQUEST_EXIT:
                // 后台线程报告完状态后，把「退出」投递回 UI 线程执行。
                Application.ExitThread();
                m.Result = IntPtr.Zero;
                return;
        }

        base.WndProc(ref m);
    }

    /// <summary>从任意线程请求退出消息循环。</summary>
    public void RequestExit()
    {
        if (Handle != IntPtr.Zero)
        {
            NativeMethods.PostMessage(Handle, WM_REQUEST_EXIT, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private static string? ReadCopyDataString(IntPtr lParam)
    {
        if (lParam == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var data = Marshal.PtrToStructure<COPYDATASTRUCT>(lParam);
            return data.cbData > 0 && data.lpData != IntPtr.Zero
                ? Marshal.PtrToStringUni(data.lpData, data.cbData / 2)
                : null;
        }
        catch
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct COPYDATASTRUCT
    {
        public IntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            DestroyHandle();
        }
    }
}
