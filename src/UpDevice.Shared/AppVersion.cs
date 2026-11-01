using System.Reflection;

namespace UpDevice.Shared;

/// <summary>
/// 当前程序版本。
///
/// 优先读程序目录里的 <c>version.txt</c>（CI 打包时写入）：启动器只替换 UpDevice/ 目录，
/// 自己的程序集版本不会跟着变，靠文件才能反映「当前装的是哪个版本」。
/// 没有文件时回退到程序集版本（本地开发的构建没有这个文件）。
/// </summary>
public static class AppVersion
{
    public const string FileName = "version.txt";

    public static Version Current => FromVersionFile() ?? FromAssembly();

    public static string CurrentText => Current.ToString();

    private static Version FromAssembly()
        => typeof(AppVersion).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);

    private static Version? FromVersionFile()
    {
        var baseDirectory = AppContext.BaseDirectory;

        // 托盘/主进程在 UpDevice/ 里，version.txt 与自己同级；
        // 启动器在外层，version.txt 在 UpDevice/ 子目录里。
        var candidates = new[]
        {
            Path.Combine(baseDirectory, FileName),
            Path.Combine(baseDirectory, "UpDevice", FileName),
        };

        foreach (var candidate in candidates)
        {
            try
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }

                if (Version.TryParse(File.ReadAllText(candidate).Trim(), out var version))
                {
                    return version;
                }
            }
            catch
            {
                // 读不到就继续找下一个。
            }
        }

        return null;
    }

    /// <summary>把 tag 之类的字符串解析成可比较的版本号（去掉开头的 v）。</summary>
    public static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var value = text.Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }

        return Version.TryParse(value, out var version) ? version : null;
    }
}
