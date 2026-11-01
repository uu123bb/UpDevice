using System.Net;
using System.Text.Json;

namespace UpDevice.Shared;

public sealed record UpdateCheckResult(
    bool Ok,
    string Message,
    string CurrentVersion,
    string? LatestVersion,
    bool HasUpdate,
    string? ReleaseUrl,
    string? PublishedAt,
    string? AssetName,
    long? AssetSize);

/// <summary>
/// 通过 GitHub Releases 检查更新（只查不装 —— 真正替换文件由启动器负责）。
/// </summary>
public static class UpdateChecker
{
    public static async Task<UpdateCheckResult> CheckAsync(string repository, CancellationToken token)
    {
        var current = AppVersion.CurrentText;

        if (string.IsNullOrWhiteSpace(repository))
        {
            return Fail(current, "未配置更新源仓库");
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        // GitHub API 强制要求 User-Agent，否则直接 403。
        http.DefaultRequestHeaders.UserAgent.ParseAdd("UpDevice-Updater");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        var url = $"https://api.github.com/repos/{repository.Trim()}/releases/latest";

        try
        {
            using var response = await http.GetAsync(url, token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var reason = response.StatusCode switch
                {
                    HttpStatusCode.NotFound => "仓库不存在，或还没有发布任何 Release",
                    HttpStatusCode.Forbidden => "GitHub 接口限流（匿名请求每小时 60 次），请稍后再试",
                    _ => $"查询失败 (HTTP {(int)response.StatusCode})",
                };

                return Fail(current, reason);
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
            var releaseUrl = GetString(root, "html_url");
            var publishedAt = GetString(root, "published_at");
            var isPrerelease = root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True;

            var latest = AppVersion.Parse(tag);
            var hasUpdate = latest is not null && latest > AppVersion.Current;

            string? assetName = null;
            long? assetSize = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = GetString(asset, "name");
                    if (name is null || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    assetName = name;
                    if (asset.TryGetProperty("size", out var size) && size.TryGetInt64(out var value))
                    {
                        assetSize = value;
                    }

                    break;
                }
            }

            var message = hasUpdate
                ? $"发现新版本 {tag}{(isPrerelease ? "（预发布）" : string.Empty)}"
                : $"已是最新版本（{current}）";

            return new UpdateCheckResult(true, message, current, tag, hasUpdate, releaseUrl, publishedAt, assetName, assetSize);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fail(current, $"检查失败：{ex.GetBaseException().Message}");
        }
    }

    private static UpdateCheckResult Fail(string current, string message)
        => new(false, message, current, null, false, null, null, null, null);

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
