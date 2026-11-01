using System.Runtime.InteropServices;
using System.Text;

namespace UpDevice.Service.Native;

internal static class NativeMethods
{
    // ---------- 窗口 ----------
    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassNameW(IntPtr hWnd, StringBuilder name, int maxCount);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    internal static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    // ---------- 电源 ----------
    [DllImport("kernel32.dll")]
    internal static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    // ---------- 进程映像名 ----------
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr handle);

    internal const uint ProcessQueryLimitedInformation = 0x1000;

    // ---------- 控制器 ----------
    [DllImport("xinput1_4.dll")]
    internal static extern uint XInputGetState(uint userIndex, out XINPUT_STATE state);

    internal const uint ErrorSuccess = 0;
    internal const uint ErrorDeviceNotConnected = 1167;

    [StructLayout(LayoutKind.Sequential)]
    internal struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    /// <summary>空闲时长（毫秒），来自系统最后一次键鼠输入。</summary>
    internal static uint GetIdleMilliseconds()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info))
        {
            return 0;
        }

        // dwTime 是 32 位 tick，直接相减可正确处理回绕。
        return unchecked((uint)Environment.TickCount - info.dwTime);
    }

    internal static string GetWindowTextSafe(IntPtr hWnd)
    {
        var buffer = new StringBuilder(512);
        var length = GetWindowTextW(hWnd, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString() : string.Empty;
    }

    internal static string GetClassNameSafe(IntPtr hWnd)
    {
        var buffer = new StringBuilder(256);
        var length = GetClassNameW(hWnd, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString() : string.Empty;
    }

    /// <summary>
    /// 取进程映像文件名（带扩展名，如 workbuddy.exe）。
    /// 用 QueryFullProcessImageName 而不是 Process.MainModule：后者对提权进程会抛异常。
    /// </summary>
    internal static string? GetProcessImageName(uint processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            if (!QueryFullProcessImageNameW(handle, 0, buffer, ref size))
            {
                return null;
            }

            var path = buffer.ToString(0, size);
            var index = path.LastIndexOf('\\');
            return index >= 0 ? path[(index + 1)..] : path;
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}
