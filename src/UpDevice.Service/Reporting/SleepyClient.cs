using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UpDevice.Service.Config;

namespace UpDevice.Service.Reporting;

/// <summary>sleepy /api/device/set 的请求体。</summary>
public sealed class DevicePayload
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("show_name")]
    public string ShowName { get; init; } = string.Empty;

    [JsonPropertyName("using")]
    public bool Using { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;
}

/// <summary>sleepy 上报客户端。</summary>
public sealed class SleepyClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // 中文文案直接以 UTF-8 发送，不转成 \uXXXX 转义。
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public async Task<bool> SendAsync(RemoteConfig remote, DevicePayload payload, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(remote.ServerUrl))
        {
            Logging.Warn("未配置远端 Sleepy 实例地址，跳过上报");
            return false;
        }

        var url = $"{remote.ServerUrl.TrimEnd('/')}/api/device/set";

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            // 用 StringContent 而不是 JsonContent：显式带上 Content-Length，
            // 免得服务端（以及某些反代）对分块传输处理不一致。
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"),
        };

        if (!string.IsNullOrEmpty(remote.Secret))
        {
            request.Headers.TryAddWithoutValidation("Sleepy-Secret", remote.Secret);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", remote.Secret);
        }

        try
        {
            using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                Logging.Warn($"上报被拒绝 (HTTP {(int)response.StatusCode}): {Trim(body)}");
                return false;
            }

            // 服务端在 200 里也可能返回 success = false，要看一眼。
            if (body.Contains("\"success\":false", StringComparison.OrdinalIgnoreCase))
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

    private static string Trim(string text) => text.Length <= 300 ? text : text[..300] + "...";

    public void Dispose() => _http.Dispose();
}
