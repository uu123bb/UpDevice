namespace UpDevice.Shared;

/// <summary>
/// 定位 config.json。
///
/// 两种部署形态：
///   扁平（开发 / 早期版本）：两个 exe 与 config.json 同目录
///   启动器（正式分发）：
///     UpDevice/
///     ├── UpDevice.Launcher.exe
///     ├── config.json          ← 和启动器同级
///     └── UpDevice/            ← 程序目录，整包更新时会被替换
///         ├── UpDevice.Service.exe
///         └── UpDevice.Tray.exe
///
/// 程序目录会被整包覆盖，所以配置必须放在外面 —— 否则每次更新都会冲掉 server 和 secret。
/// </summary>
public static class ConfigPath
{
    public const string FileName = "config.json";

    private const string LauncherFileName = "UpDevice.Launcher.exe";

    /// <summary>优先级：环境变量 → 启动器同级 → 程序自身目录。</summary>
    public static string Resolve()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("UPDEVICE_CONFIG");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment;
        }

        var baseDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var own = Path.Combine(baseDirectory, FileName);

        if (TryGetLauncherDirectory(baseDirectory) is { } launcherDirectory)
        {
            var shared = Path.Combine(launcherDirectory, FileName);
            if (File.Exists(shared) || File.Exists(Path.Combine(launcherDirectory, LauncherFileName)))
            {
                return shared;
            }
        }

        return own;
    }

    private static string? TryGetLauncherDirectory(string baseDirectory)
    {
        try
        {
            return Directory.GetParent(baseDirectory)?.FullName;
        }
        catch
        {
            return null;
        }
    }
}
