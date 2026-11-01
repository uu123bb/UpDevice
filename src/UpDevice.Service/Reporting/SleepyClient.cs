using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using UpDevice.Shared;
using UpDevice.Shared.Config;

namespace UpDevice.Service.Reporting;

/// <summary>设备上报请求体。</summary>
public sealed class DevicePayload
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("show_name")]
    public string ShowName { get; init; } = string.Empty;

    [JsonPropertyName("using")]
    public bool Using { get; init; }

    /// <summary>V4 字段名（sleepy 4.x / sleepy_serverless）。</summary>
    [JsonPropertyName("app_name")]
    public string AppName { get; init; } = string.Empty;

    /// <summary>V5 字段名（FastAPI 重构版）。两套一起发，服务端会忽略不认识的那个。</summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;
}

/// <summary>sleepy 上报客户端，自动适配 V4 / V5 两套接口。</summary>
public sealed class SleepyClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // 中文文案直接以 UTF-8 发送，不转成 \uXXXX 转义。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private SleepyApiFlavor _flavor = SleepyApiFlavor.Unknown;
    private string _flavorBaseUrl = string.Empty;
    private bool _warnedProbeFailure;

    public async Task<bool> SendAsync(RemoteConfig remote, DevicePayload payload, CancellationToken token)
    {
        var baseUrl = SleepyApi.NormalizeBaseUrl(remote.ServerUrl);
        if (baseUrl.Length == 0)
        {
            Logging.Warn("未配置远端 Sleepy 实例地址，跳过上报");
            return false;
        }

        var flavor = await ResolveFlavorAsync(baseUrl, token).ConfigureAwait(false);
        if (flavor == SleepyApiFlavor.Unknown)
        {
            return false;
        }

        var result = await PostAsync(baseUrl, flavor, remote.Secret, payload, token).ConfigureAwait(false);
        if (result is not null)
        {
            return result.Value;
        }

        // 探测结果可能过时（服务端换了版本），换一套接口再试一次。
        var fallback = flavor == SleepyApiFlavor.V4 ? SleepyApiFlavor.V5 : SleepyApiFlavor.V4;
        Logging.Warn($"{flavor} 接口返回 404，改用 {fallback} 重试");

        var retry = await PostAsync(baseUrl, fallback, remote.Secret, payload, token).ConfigureAwait(false);
        if (retry is { } ok)
        {
            if (ok)
            {
                _flavor = fallback;
                _flavorBaseUrl = baseUrl;
                Logging.Info($"接口已切换为 {fallback}");
            }

            return ok;
        }

        return false;
    }

    /// <summary>返回 null 表示 404（接口不存在，值得换一套重试）。</summary>
    private async Task<bool?> PostAsync(
        string baseUrl,
        SleepyApiFlavor flavor,
        string secret,
        DevicePayload payload,
        CancellationToken token)
    {
        var url = baseUrl + SleepyApi.DeviceSetPath(flavor);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            // 用 StringContent 而不是 JsonContent：显式带上 Content-Length，
            // 免得服务端（以及某些反代）对分块传输处理不一致。
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"),
        };

        if (!string.IsNullOrEmpty(secret))
        {
            request.Headers.TryAddWithoutValidation("Sleepy-Secret", secret);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        }

        try
        {
            using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                Logging.Warn($"上报被拒绝 (HTTP {(int)response.StatusCode}): {Trim(body)}");
                return false;
            }

            if (!IsSuccess(body))
            {
                Logging.Warn($"上报未成功: {Trim(body)}");
                return false;
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // HttpRequestException 的外层消息没有信息量，取内层的真实原因。
            Logging.Warn($"上报请求失败: {ex.GetBaseException().Message}");
            return false;
        }
    }

    private async Task<SleepyApiFlavor> ResolveFlavorAsync(string baseUrl, CancellationToken token)
    {
        if (_flavor != SleepyApiFlavor.Unknown && _flavorBaseUrl == baseUrl)
        {
            return _flavor;
        }

        var probe = await SleepyApi.ProbeAsync(baseUrl, token).ConfigureAwait(false);
        if (!probe.Ok)
        {
            if (!_warnedProbeFailure)
            {
                _warnedProbeFailure = true;
                Logging.Warn(probe.Message);
            }

            return SleepyApiFlavor.Unknown;
        }

        _flavor = probe.Flavor;
        _flavorBaseUrl = baseUrl;
        _warnedProbeFailure = false;
        Logging.Info($"远端接口识别为 {probe.Flavor}：{probe.Message}");
        return _flavor;
    }

    private static bool IsSuccess(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return !document.RootElement.TryGetProperty("success", out var success)
                || success.ValueKind != JsonValueKind.False;
        }
        catch
        {
            // 不是 JSON 就当成功（部分部署会直接返回空体）。
            return true;
        }
    }

    private static string Trim(string text) => text.Length <= 300 ? text : text[..300] + "...";

    public void Dispose() => _http.Dispose();
}
