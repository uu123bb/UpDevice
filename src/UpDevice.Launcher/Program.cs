using UpDevice.Shared;

namespace UpDevice.Launcher;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Logging.Initialize();
        Logging.Info("==== UpDevice.Launcher 启动 ====");

        using var singleInstance = new Mutex(true, @"Local\UpDevice.Launcher", out var isFirstInstance);
        if (!isFirstInstance)
        {
            Logging.Warn("已有启动器在运行，本次退出");
            return;
        }

        ApplicationConfiguration.Initialize();

        // --no-start：只开启动器窗口，不自动拉起程序（调试用）
        var autoStart = !args.Any(a => a.Equals("--no-start", StringComparison.OrdinalIgnoreCase));
        Application.Run(new LauncherForm(autoStart));

        Logging.Info("==== UpDevice.Launcher 退出 ====");
    }
}
