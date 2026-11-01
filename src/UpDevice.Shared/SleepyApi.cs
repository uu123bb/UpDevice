using System.Text.Json;

namespace UpDevice.Shared;

public enum SleepyApiFlavor
{
    Unknown = 0,

    /// <summary>V4 接口：不带 /api 前缀，设备字段叫 app_name。</summary>
    V4,

    /// <summary>V5（FastAPI 重构版）接口：带 /api 前缀，设备字段叫 status。</summary>
    V5,
}

/// <summary>
/// sleepy 的接口差异集中在这里。
///
/// V4（sleepy 4.x / sleepy_serverless 分支，目前自建实例用的就是这套）：
///   GET  /query            查询状态（无需鉴权）
///   POST /device/set       上报设备（需要鉴权），字段 id / show_name / using / app_name
///   GET  /device/remove    移除设备
///   没有 /meta，也没有 /api 前缀
///
/// V5（sleepy-project 主仓的 FastAPI 重构版）：
///   GET  /api/status/query
///   POST /api/device/set   字段 id / show_name / using / status
/// </summary>
public static class SleepyApi
{
    public const string V4QueryPath = "/query";
    public const string V5QueryPath = "/api/status/query";

    /// <summary>去掉尾部斜杠，统一成干净的基础地址。</summary>
    public static string NormalizeBaseUrl(string serverUrl) => (serverUrl ?? string.Empty).Trim().TrimEnd('/');

    /// <summary>补全协议头：本机地址默认 http，其余默认 https。</summary>
    public static string NormalizeUrl(string url)
    {
        var value = (url ?? string.Empty).Trim().TrimEnd('/');
        if (value.Length == 0)
        {
            return value;
        }

        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        var isLocal = value.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("192.168.", StringComparison.OrdinalIgnoreCase);

        return (isLocal ? "http://" : "https://") + value;
    }

    public static string DeviceSetPath(SleepyApiFlavor flavor) =>
        flavor == SleepyApiFlavor.V5 ? "/api/device/set" : "/device/set";

    public static string DeviceRemovePath(SleepyApiFlavor flavor) =>
        flavor == SleepyApiFlavor.V5 ? "/api/device/remove" : "/device/remove";

    /// <summary>
    /// 探测服务端是哪套接口。先试 V4 的 /query（自建实例都是这套），失败再试 V5。
    /// 注意：不要再探测 /api/meta —— V4 根本没有这个路由。
    /// </summary>
    public static async Task<SleepyProbeResult> ProbeAsync(string serverUrl, CancellationToken token)
    {
        var baseUrl = NormalizeBaseUrl(serverUrl);
        if (baseUrl.Length == 0)
        {
            return SleepyProbeResult.Fail("请先填写远端 Sleepy 实例地址");
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        var v4 = await TryQueryAsync(http, baseUrl + V4QueryPath, token).ConfigureAwait(false);
        if (v4.Ok)
        {
            return new SleepyProbeResult(true, SleepyApiFlavor.V4, $"V4 接口 {V4QueryPath} 可用{v4.Describe()}", v4.DeviceCount);
        }

        var v5 = await TryQueryAsync(http, baseUrl + V5QueryPath, token).ConfigureAwait(false);
        if (v5.Ok)
        {
            return new SleepyProbeResult(true, SleepyApiFlavor.V5, $"V5 接口 {V5QueryPath} 可用{v5.Describe()}", v5.DeviceCount);
        }

        return SleepyProbeResult.Fail($"连接失败：{v4.Error}；{v5.Error}");
    }

    private static async Task<QueryProbe> TryQueryAsync(HttpClient http, string url, CancellationToken token)
    {
        try
        {
            using var response = await http.GetAsync(url, token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return QueryProbe.Failed($"{new Uri(url).AbsolutePath} → HTTP {(int)response.StatusCode}");
            }

            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;

            if (!root.TryGetProperty("success", out var success)
                || success.ValueKind != JsonValueKind.True)
            {
                return QueryProbe.Failed($"{new Uri(url).AbsolutePath} → 返回内容不是 sleepy 的格式");
            }

            int? count = null;
            if (root.TryGetProperty("device", out var device) && device.ValueKind == JsonValueKind.Object)
            {
                count = device.EnumerateObject().Count();
            }

            return new QueryProbe(true, count, null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            // HttpClient 超时在 .NET 里抛的是 TaskCanceledException，消息只有
            // "A task was canceled."，完全看不出是超时 —— 这里翻成人话。
            return QueryProbe.Failed($"{new Uri(url).AbsolutePath} → 请求超时（10 秒）");
        }
        catch (Exception ex)
        {
            return QueryProbe.Failed($"{new Uri(url).AbsolutePath} → {ex.GetBaseException().Message}");
        }
    }

    private readonly record struct QueryProbe(bool Ok, int? DeviceCount, string? Error)
    {
        public static QueryProbe Failed(string error) => new(false, null, error);

        public string Describe() => DeviceCount is { } count ? $"，当前有 {count} 台设备" : string.Empty;
    }
}

public sealed record SleepyProbeResult(bool Ok, SleepyApiFlavor Flavor, string Message, int? DeviceCount)
{
    public static SleepyProbeResult Fail(string message) => new(false, SleepyApiFlavor.Unknown, message, null);
}
