using System.Runtime.InteropServices;
using System.Text;


namespace UpDevice.Shared;

/// <summary>
/// 与主进程的指令通道：主进程持有一个标题固定的隐藏窗口，这里用 WM_COPYDATA 发字符串指令。
/// </summary>
public static class Ipc
{
    private const int WM_COPYDATA = 0x004A;
    private const string ServiceWindowCaption = "UpDevice.Service.Hidden";

    /// <summary>主进程是否在运行。</summary>
    public static bool IsServiceRunning => FindWindow(null, ServiceWindowCaption) != IntPtr.Zero;

    public static bool SendCommand(string command)
    {
        var handle = FindWindow(null, ServiceWindowCaption);
        if (handle == IntPtr.Zero)
        {
            Logging.Warn($"未找到主进程窗口，指令 {command} 未送达");
            return false;
        }

        var bytes = Encoding.Unicode.GetBytes(command);
        var buffer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            var data = new COPYDATASTRUCT
            {
                dwData = IntPtr.Zero,
                cbData = bytes.Length,
                lpData = buffer,
            };

            SendMessage(handle, WM_COPYDATA, IntPtr.Zero, ref data);
            return true;
        }
        catch (Exception ex)
        {
            Logging.Warn($"发送指令 {command} 失败: {ex.Message}");
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? className, string windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref COPYDATASTRUCT lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct COPYDATASTRUCT
    {
        public IntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }
}
