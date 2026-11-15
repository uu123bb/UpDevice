using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using UpDevice.Shared;
using UpDevice.Shared.Config;
using UpDevice.Shared.Trucking;

namespace UpDevice.Tray.Trucking;

/// <summary>设置页「遥测插件管理」里一个游戏的状态。</summary>
/// <param name="Id">游戏 id。</param>
/// <param name="DisplayName">展示名。</param>
/// <param name="Path">当前使用的游戏根目录，没探测到为 null。</param>
/// <param name="Source">路径来源：<c>steam</c> / <c>custom</c> / <c>none</c>。</param>
/// <param name="Installed">插件 DLL 是否已就位。</param>
/// <param name="PluginPath">插件目标路径（未探测到根目录时为空串）。</param>
/// <param name="Message">给用户看的补充说明。</param>
public sealed record TruckGameStatus(
    string Id,
    string DisplayName,
    string? Path,
    string Source,
    bool Installed,
    string PluginPath,
    string Message);

/// <summary>
/// ATS / ETS2 的插件安装与卸载。
///
/// 托盘是提权运行的（app.manifest 要求管理员），所以能直接往 Program Files 下的
/// 游戏目录里写文件，不需要用户在资源管理器里手工复制。
/// </summary>
public static class TruckPluginManager
{
    /// <summary>装进游戏 plugins 目录的文件名。改名会让已安装的旧文件变成孤儿，别动。</summary>
    public const string PluginFileName = "UpDevice.Telemetry.dll";

    private const string PluginsRelative = @"bin\win_x64\plugins";

    /// <summary>随程序分发的插件本体，由 CI 在发布前放到 Telemetry/ 下。</summary>
    public static string BundledPluginPath => System.IO.Path.Combine(AppContext.BaseDirectory, "Telemetry", PluginFileName);

    public static IReadOnlyList<TruckGameStatus> Detect(TruckingConfig config)
    {
        var libraries = SteamLibraries().ToList();
        return TruckGames.All.Select(game => Detect(game, config, libraries)).ToList();
    }

    private static TruckGameStatus Detect(
        TruckGameDefinition game,
        TruckingConfig config,
        IReadOnlyList<string> libraries)
    {
        var custom = config.For(game.Id).Path;
        if (!string.IsNullOrWhiteSpace(custom))
        {
            if (NormalizeRoot(custom) is { } customRoot)
            {
                return Build(game, customRoot, "custom");
            }

            return new TruckGameStatus(game.Id, game.DisplayName, null, "none", false, string.Empty,
                $"自定义路径无效：{custom}");
        }

        foreach (var library in libraries)
        {
            var candidate = System.IO.Path.Combine(library, "steamapps", "common", game.InstallFolder);
            if (Directory.Exists(candidate) && NormalizeRoot(candidate) is { } root)
            {
                return Build(game, root, "steam");
            }
        }

        return new TruckGameStatus(game.Id, game.DisplayName, null, "none", false, string.Empty,
            "未找到 Steam 版安装目录，可手动指定");
    }

    private static TruckGameStatus Build(TruckGameDefinition game, string root, string source)
    {
        var plugins = System.IO.Path.Combine(root, PluginsRelative);
        var target = System.IO.Path.Combine(plugins, PluginFileName);
        var installed = File.Exists(target);

        return new TruckGameStatus(game.Id, game.DisplayName, root, source, installed,
            installed ? target : System.IO.Path.Combine(plugins, PluginFileName), string.Empty);
    }

    /// <summary>
    /// 把插件复制到游戏目录。<paramref name="pathOverride"/> 非空时先用它替换自定义路径。
    /// </summary>
    public static (bool Ok, string Message) Install(string gameId, string? pathOverride, TruckingConfig config)
    {
        var game = TruckGames.Find(gameId);
        if (game is null)
        {
            return (false, "未知的游戏");
        }

        if (!File.Exists(BundledPluginPath))
        {
            return (false, $"找不到插件文件 {BundledPluginPath}，请确认程序完整（开发环境需先编译插件）");
        }

        var root = ResolveRoot(game, pathOverride, config);
        if (root is null)
        {
            return (false, "还没有可用的游戏目录，请先指定路径");
        }

        var plugins = System.IO.Path.Combine(root, PluginsRelative);
        var target = System.IO.Path.Combine(plugins, PluginFileName);

        try
        {
            Directory.CreateDirectory(plugins);

            // 游戏正在运行时 DLL 被占用，覆盖会失败 —— 给出可读提示而不是异常堆栈。
            File.Copy(BundledPluginPath, target, overwrite: true);
            Logging.Info($"已安装遥测插件: {target}");
            return (true, $"已安装到 {target}");
        }
        catch (Exception ex)
        {
            Logging.Warn($"安装遥测插件失败: {ex.Message}");
            return (false, DescribeIoError(ex, game));
        }
    }

    public static (bool Ok, string Message) Uninstall(string gameId, string? pathOverride, TruckingConfig config)
    {
        var game = TruckGames.Find(gameId);
        if (game is null)
        {
            return (false, "未知的游戏");
        }

        var root = ResolveRoot(game, pathOverride, config);
        if (root is null)
        {
            return (false, "还没有可用的游戏目录，请先指定路径");
        }

        var target = System.IO.Path.Combine(root, PluginsRelative, PluginFileName);
        if (!File.Exists(target))
        {
            return (true, "插件本来就不在，无需卸载");
        }

        try
        {
            File.Delete(target);
            Logging.Info($"已卸载遥测插件: {target}");
            return (true, $"已从 {target} 移除");
        }
        catch (Exception ex)
        {
            Logging.Warn($"卸载遥测插件失败: {ex.Message}");
            return (false, DescribeIoError(ex, game));
        }
    }

    /// <summary>校验并保存自定义路径；传空表示恢复自动探测。</summary>
    public static (bool Ok, string Message, string? Root) SetCustomPath(string gameId, string? path, TruckingConfig config)
    {
        var game = TruckGames.Find(gameId);
        if (game is null)
        {
            return (false, "未知的游戏", null);
        }

        var trimmed = (path ?? string.Empty).Trim().Trim('"');

        if (trimmed.Length == 0)
        {
            var existing = config.Games.FirstOrDefault(g => g.Id == game.Id);
            if (existing is not null)
            {
                existing.Path = string.Empty;
            }

            return (true, "已恢复自动探测", null);
        }

        var root = NormalizeRoot(trimmed);
        if (root is null)
        {
            return (false, "该目录下没有 bin\\win_x64，请选游戏安装根目录（里面应有 bin 文件夹）", null);
        }

        var entry = config.Games.FirstOrDefault(g => g.Id == game.Id);
        if (entry is null)
        {
            entry = new TruckGameConfig { Id = game.Id };
            config.Games.Add(entry);
        }

        entry.Path = root;
        return (true, $"已保存路径 {root}", root);
    }

    /// <summary>
    /// 把用户给的路径收敛成游戏根目录。
    /// 允许直接选 <c>bin</c> 或 <c>bin\win_x64</c>，省得用户被目录层级绕晕。
    /// </summary>
    public static string? NormalizeRoot(string? path)
    {
        var trimmed = (path ?? string.Empty).Trim().Trim('"');
        if (trimmed.Length == 0)
        {
            return null;
        }

        try
        {
            var full = System.IO.Path.GetFullPath(trimmed).TrimEnd('\\');
            if (!Directory.Exists(full))
            {
                return null;
            }

            // 允许直接选 bin 或 bin\\win_x64，往上退到游戏根目录再判断。
            if (string.Equals(System.IO.Path.GetFileName(full), "win_x64", StringComparison.OrdinalIgnoreCase))
            {
                full = System.IO.Path.GetDirectoryName(full) ?? full;
            }

            if (string.Equals(System.IO.Path.GetFileName(full), "bin", StringComparison.OrdinalIgnoreCase))
            {
                full = System.IO.Path.GetDirectoryName(full) ?? full;
            }

            return Directory.Exists(System.IO.Path.Combine(full, "bin", "win_x64")) ? full : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ResolveRoot(TruckGameDefinition game, string? pathOverride, TruckingConfig config)
    {
        if (!string.IsNullOrWhiteSpace(pathOverride) && NormalizeRoot(pathOverride) is { } fromOverride)
        {
            return fromOverride;
        }

        var custom = config.For(game.Id).Path;
        if (!string.IsNullOrWhiteSpace(custom) && NormalizeRoot(custom) is { } fromConfig)
        {
            return fromConfig;
        }

        foreach (var library in SteamLibraries())
        {
            var candidate = System.IO.Path.Combine(library, "steamapps", "common", game.InstallFolder);
            if (Directory.Exists(candidate) && NormalizeRoot(candidate) is { } root)
            {
                return root;
            }
        }

        return null;
    }

    private static string DescribeIoError(Exception ex, TruckGameDefinition game)
    {
        if (ex is UnauthorizedAccessException)
        {
            return $"没有写入权限，请以管理员身份运行 UpDevice，或手动把插件复制到游戏的 {PluginsRelative}";
        }

        if (ex is IOException && IsGameRunning(game))
        {
            return "游戏正在运行，DLL 被占用。请先退出游戏再试一次";
        }

        return $"操作失败：{ex.GetBaseException().Message}";
    }

    private static bool IsGameRunning(TruckGameDefinition game)
    {
        try
        {
            return Process.GetProcessesByName(System.IO.Path.GetFileNameWithoutExtension(game.ProcessName)).Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 枚举所有 Steam 库目录（含库根目录本身）。
    /// 路径来源：注册表里的 Steam 安装位置 + libraryfolders.vdf。
    /// </summary>
    private static IEnumerable<string> SteamLibraries()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var steamRoot in SteamRoots())
        {
            if (!seen.Add(steamRoot))
            {
                continue;
            }

            yield return steamRoot;

            var vdf = System.IO.Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            foreach (var library in ParseLibraryFolders(vdf))
            {
                if (seen.Add(library))
                {
                    yield return library;
                }
            }
        }
    }

    private static IEnumerable<string> SteamRoots()
    {
        foreach (var candidate in new[]
                 {
                     ReadRegistry(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
                     ReadRegistry(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
                     ReadRegistry(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
                 })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
            {
                yield return candidate.TrimEnd('\\');
            }
        }
    }

    private static string? ReadRegistry(RegistryKey root, string path, string name)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            return key?.GetValue(name) as string;
        }
        catch (Exception)
        {
            // 注册表读不了（权限/位数）就当没有 Steam。
            return null;
        }
    }

    /// <summary>
    /// 从 libraryfolders.vdf 里抠出库路径。
    /// 新版是 <c>"path"  "D:\\SteamLibrary"</c>，旧版是 <c>"1"  "D:\\SteamLibrary"</c>，两种都认。
    /// 用正则而不是写 VDF 解析器 —— 这个文件格式固定，正则够了。
    /// </summary>
    private static IEnumerable<string> ParseLibraryFolders(string vdfPath)
    {
        string text;
        try
        {
            if (!File.Exists(vdfPath))
            {
                yield break;
            }

            text = File.ReadAllText(vdfPath);
        }
        catch (Exception)
        {
            yield break;
        }

        var found = new List<string>();
        foreach (Match match in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
        {
            found.Add(match.Groups[1].Value.Replace(@"\\", @"\"));
        }

        if (found.Count == 0)
        {
            foreach (Match match in Regex.Matches(text, "\"\\d+\"\\s+\"([A-Za-z]:\\\\[^\"]+)\""))
            {
                found.Add(match.Groups[1].Value.Replace(@"\\", @"\"));
            }
        }

        foreach (var path in found)
        {
            if (Directory.Exists(path))
            {
                yield return path.TrimEnd('\\');
            }
        }
    }
}
