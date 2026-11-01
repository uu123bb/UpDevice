using System.Security.Cryptography;

namespace UpDevice.Shared;

/// <summary>
/// 更新包的下载与校验。托盘（有界面、能显示进度）负责下载，
/// 启动器只负责「等进程退出 → 替换目录」，两边共用这里的路径约定与校验逻辑。
/// </summary>
public static class UpdateInstaller
{
    public static string WorkingDirectory => Path.Combine(Path.GetTempPath(), "UpDevice-update");

    /// <summary>下载并校验更新包，返回 zip 的完整路径。</summary>
    public static async Task<string> DownloadAsync(UpdateCheckResult info, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(info.AssetUrl))
        {
            throw new InvalidOperationException("Release 里没有找到 zip 产物");
        }

        var directory = Path.Combine(WorkingDirectory, info.LatestVersion ?? "latest");
        Directory.CreateDirectory(directory);

        var zipPath = Path.Combine(directory, info.AssetName ?? "update.zip");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("UpDevice-Updater");

        using var response = await http.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using (var target = File.Create(zipPath))
        {
            await response.Content.CopyToAsync(target, token).ConfigureAwait(false);
        }

        await VerifyDigestAsync(zipPath, info.AssetDigest, token).ConfigureAwait(false);
        return zipPath;
    }

    public static async Task VerifyDigestAsync(string filePath, string? digest, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(digest))
        {
            Logging.Warn("Release 未提供 digest，跳过校验");
            return;
        }

        var expected = digest.Contains(':') ? digest[(digest.IndexOf(':') + 1)..] : digest;

        await using var stream = File.OpenRead(filePath);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));

        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("更新包校验失败（SHA256 不匹配），已中止");
        }
    }
}
