using System.Reflection;

namespace UpDevice.Shared;

/// <summary>当前程序版本，来自程序集版本（由 Directory.Build.props 或 CI 的 -p:Version 注入）。</summary>
public static class AppVersion
{
    public static Version Current =>
        typeof(AppVersion).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);

    public static string CurrentText => Current.ToString();

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
