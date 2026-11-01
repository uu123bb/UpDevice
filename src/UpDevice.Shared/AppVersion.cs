using System.Reflection;

namespace UpDevice.Shared;

/// <summary>
/// 当前程序版本。
///
/// 正式分发时程序跑在 <c>app-1.0.1.1-0</c> 这样的目录里，**目录名就是版本** ——
/// 更新只是换一个目录，不需要额外的版本文件，也不会出现「程序换了但版本没变」。
/// 开发构建不在 app-* 目录里，退回程序集版本。
/// </summary>
public static class AppVersion
{
    public static Version Current =>
        AppPaths.ParseAppDirectory(AppPaths.CurrentDirectoryName) ?? FromAssembly();

    public static string CurrentText => Current.ToString();

    private static Version FromAssembly()
        => typeof(AppVersion).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);

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
