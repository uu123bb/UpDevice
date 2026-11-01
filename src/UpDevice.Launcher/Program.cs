using UpDevice.Shared;

namespace UpDevice.Launcher;

/// <summary>
/// UpDevice.Launcher.exe —— 只是个入口，不是独立程序。
///
///   无参数                  把托盘拉起来，然后立刻退出
///   --apply-update &lt;zip&gt;   等进程退出、替换 UpDevice/、再拉起托盘，然后退出
///   --version &lt;x.y.z&gt;      配合上一条，写入 version.txt
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Logging.Initialize();

        var zipPath = GetOption(args, "--apply-update");
        if (zipPath is not null)
        {
            return ApplyUpdate(zipPath, GetOption(args, "--version"));
        }

        Logging.Info("==== UpDevice.Launcher 启动 ====");
        Updater.StartApps();
        Logging.Info("==== UpDevice.Launcher 退出 ====");
        return 0;
    }

    private static int ApplyUpdate(string zipPath, string? version)
    {
        Logging.Info($"==== 应用更新: {zipPath} (version={version ?? "-"}) ====");

        try
        {
            Updater.ApplyAsync(zipPath, version).GetAwaiter().GetResult();
            Logging.Info("更新完成，正在重启");
        }
        catch (Exception ex)
        {
            Logging.Error($"应用更新失败: {ex.Message}");
            // 失败了也得让用户有程序可用，退回旧版本继续跑。
            Updater.StartApps();
            return 1;
        }

        Updater.StartApps();
        Logging.Info("==== UpDevice.Launcher 退出 ====");
        return 0;
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }
}
