namespace UpDevice.Shared;

/// <summary>
/// 目录布局（参考 ClassIsland-Misha）：
///
/// <code>
/// UpDevice/
/// ├── UpDevice.Launcher.exe      # 启动器
/// ├── app-1.0.1.1-0/            # 版本化程序目录，更新时整目录新增
/// │   ├── UpDevice.Service.exe
/// │   └── UpDevice.Tray.exe
/// └── data/                     # 用户数据，与程序分离，更新不受影响
///     ├── Config/config.json
///     ├── Logs/
///     ├── Cache/Update/         # 下载的更新包
///     └── Temp/
/// </code>
///
/// 开发时（bin/Debug/...）不在 app-* 目录里，此时全部退回程序自身目录，
/// 保持以前的扁平行为，不会莫名其妙造出 data 目录。
/// </summary>
public static class AppPaths
{
    public const string AppDirectoryPrefix = "app-";
    public const string DataDirectoryName = "data";
    public const string LauncherFileName = "UpDevice.Launcher.exe";
    public const string ConfigFileName = "config.json";

    private static string BaseDirectory =>
        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>当前程序所在目录名，正式分发时形如 <c>app-1.0.1.1-0</c>。</summary>
    public static string CurrentDirectoryName => Path.GetFileName(BaseDirectory);

    /// <summary>程序自己是否就装在 app-* 目录里（托盘、主进程是这种）。</summary>
    public static bool IsInsideAppDirectory =>
        CurrentDirectoryName.StartsWith(AppDirectoryPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>根目录：在 app-* 里就是它的上一级，否则就是程序目录本身（启动器属于这种）。</summary>
    public static string Root => IsInsideAppDirectory
        ? Directory.GetParent(BaseDirectory)?.FullName ?? BaseDirectory
        : BaseDirectory;

    /// <summary>
    /// 是否处于版本化布局。
    /// 除了「自己在 app-* 里」，还要认「自己所在目录下有 app-*」—— 启动器就在根目录，
    /// 它的日志和 data 也应该进 data/，不能散在根上。
    /// </summary>
    public static bool IsVersionedLayout => IsInsideAppDirectory || HasAppSubdirectory(Root);

    private static bool HasAppSubdirectory(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                && Directory.EnumerateDirectories(directory, AppDirectoryPrefix + "*").Any();
        }
        catch
        {
            return false;
        }
    }

    public static string DataDirectory => IsVersionedLayout
        ? Path.Combine(Root, DataDirectoryName)
        : BaseDirectory;

    public static string ConfigDirectory => IsVersionedLayout
        ? Path.Combine(DataDirectory, "Config")
        : BaseDirectory;

    public static string LogsDirectory => IsVersionedLayout
        ? Path.Combine(DataDirectory, "Logs")
        : Path.Combine(BaseDirectory, "logs");

    public static string UpdateCacheDirectory => IsVersionedLayout
        ? Path.Combine(DataDirectory, "Cache", "Update")
        : Path.Combine(BaseDirectory, "updates");

    public static string TempDirectory => IsVersionedLayout
        ? Path.Combine(DataDirectory, "Temp")
        : Path.Combine(BaseDirectory, "temp");

    public static string LauncherPath => Path.Combine(Root, LauncherFileName);

    /// <summary>配置文件路径。环境变量优先，便于测试时指到别处。</summary>
    public static string ConfigFile
    {
        get
        {
            var fromEnvironment = Environment.GetEnvironmentVariable("UPDEVICE_CONFIG");
            return string.IsNullOrWhiteSpace(fromEnvironment)
                ? Path.Combine(ConfigDirectory, ConfigFileName)
                : fromEnvironment;
        }
    }

    public static void EnsureDirectories()
    {
        foreach (var directory in new[] { ConfigDirectory, LogsDirectory, UpdateCacheDirectory, TempDirectory })
        {
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch
            {
                // 建不出来就让调用方在写文件时报错，不在这里中断启动。
            }
        }
    }

    /// <summary>
    /// 旧布局把 config.json 放在启动器旁边或程序目录里，
    /// 换成版本化布局后应该落到 data/Config 下 —— 这里做一次性搬运。
    /// </summary>
    public static void MigrateLegacyConfig()
    {
        if (!IsVersionedLayout)
        {
            return;
        }

        var target = ConfigFile;
        if (File.Exists(target))
        {
            return;
        }

        var baseDirectory = AppContext.BaseDirectory;
        foreach (var legacy in new[] { Path.Combine(Root, ConfigFileName), Path.Combine(baseDirectory, ConfigFileName) })
        {
            if (!File.Exists(legacy))
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(ConfigDirectory);
                File.Copy(legacy, target, overwrite: false);
                Logging.Info($"已把旧配置从 {legacy} 迁移到 {target}");
                return;
            }
            catch (Exception ex)
            {
                Logging.Warn($"迁移旧配置失败: {ex.Message}");
            }
        }
    }

    /// <summary>从 <c>app-1.0.1.1-0</c> 里取出版本号；不是这种名字返回 null。</summary>
    public static Version? ParseAppDirectory(string? directoryName)
    {
        if (string.IsNullOrWhiteSpace(directoryName)
            || !directoryName.StartsWith(AppDirectoryPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rest = directoryName[AppDirectoryPrefix.Length..];

        // 末尾的 -0 / -1 是同一版本的重复安装序号，去掉再解析。
        var lastDash = rest.LastIndexOf('-');
        if (lastDash > 0)
        {
            rest = rest[..lastDash];
        }

        return Version.TryParse(rest, out var version) ? version : null;
    }

    /// <summary>枚举根目录下所有版本化程序目录，按版本从高到低排序。</summary>
    public static IReadOnlyList<(string Path, Version Version, int Revision)> EnumerateAppDirectories(string root)
    {
        if (!Directory.Exists(root))
        {
            return Array.Empty<(string, Version, int)>();
        }

        var items = new List<(string Path, Version Version, int Revision)>();

        foreach (var directory in Directory.EnumerateDirectories(root, AppDirectoryPrefix + "*"))
        {
            var name = Path.GetFileName(directory);
            if (ParseAppDirectory(name) is not { } version)
            {
                continue;
            }

            var revision = 0;
            var lastDash = name.LastIndexOf('-');
            if (lastDash > 0 && int.TryParse(name[(lastDash + 1)..], out var parsed))
            {
                revision = parsed;
            }

            items.Add((directory, version, revision));
        }

        return items
            .OrderByDescending(item => item.Version)
            .ThenByDescending(item => item.Revision)
            .ToList();
    }
}
