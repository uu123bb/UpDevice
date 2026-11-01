using UpDevice.Shared;
using UpDevice.Shared.Config;

namespace UpDevice.Tray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Logging.Initialize();
        Logging.Info("==== UpDevice.Tray 启动 ====");

        using var singleInstance = new Mutex(true, @"Local\UpDevice.Tray", out var isFirstInstance);
        if (!isFirstInstance)
        {
            Logging.Warn("已有 UpDevice.Tray 在运行，本次退出");
            return;
        }

        ApplicationConfiguration.Initialize();

        var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        var store = ConfigStore.Load(configPath);
        Logging.Info($"配置文件: {store.Path}");

        var web = new TrayWebHost(store);
        try
        {
            web.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"配置页面启动失败：{ex.Message}", "UpDevice", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        using var context = new TrayContext(store, web);
        Application.Run(context);

        web.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Logging.Info("==== UpDevice.Tray 退出 ====");
    }
}
