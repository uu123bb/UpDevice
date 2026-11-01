using System.Net.Http;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using UpDevice.Shared;
using UpDevice.Shared.Config;

namespace UpDevice.Tray;

/// <summary>
/// 仅监听 127.0.0.1 的本地 Web 服务，提供配置页与配套接口。
/// 只绑回环，不对局域网暴露（配置里有 secret）。
/// </summary>
internal sealed class TrayWebHost
{
    private const int PreferredPort = 38945;
    private const int PortSearchRange = 10;

    private readonly ConfigStore _store;

    private WebApplication? _application;

    public TrayWebHost(ConfigStore store)
    {
        _store = store;
    }

    public string Url { get; private set; } = $"http://127.0.0.1:{PreferredPort}/";

    public void Start()
    {
        Exception? last = null;

        for (var offset = 0; offset < PortSearchRange; offset++)
        {
            var port = PreferredPort + offset;
            try
            {
                var application = Build(port);
                application.StartAsync().GetAwaiter().GetResult();
                _application = application;
                Url = $"http://127.0.0.1:{port}/";
                Logging.Info($"配置页面已启动: {Url}");
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                Logging.Warn($"端口 {port} 不可用: {ex.GetBaseException().Message}");
            }
        }

        throw new InvalidOperationException("没有可用端口，配置页面无法启动", last);
    }

    private WebApplication Build(int port)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

        var application = builder.Build();
        MapEndpoints(application);
        return application;
    }

    private void MapEndpoints(WebApplication application)
    {
        application.MapGet("/", async context =>
        {
            var page = Path.Combine(AppContext.BaseDirectory, "Web", "index.html");
            if (!File.Exists(page))
            {
                context.Response.StatusCode = 500;
                await context.Response.WriteAsync("未找到 Web/index.html");
                return;
            }

            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.SendFileAsync(page);
        });

        application.MapGet("/api/state", () => Results.Json(new
        {
            reportingEnabled = _store.Current.ReportingEnabled,
            scheduledTaskEnabled = ScheduledTask.IsEnabled(),
            autostartEnabled = Autostart.IsEnabled(),
            serviceRunning = Ipc.IsServiceRunning,
            url = Url,
        }));

        application.MapGet("/api/config", () => Results.Json(_store.Current));

        application.MapPost("/api/config", async (HttpRequest request) =>
        {
            var (parsed, incoming) = await TryReadAsync<AppConfig>(request);
            if (!parsed || incoming is null)
            {
                return Results.BadRequest(new { ok = false, message = "配置内容无法解析" });
            }

            incoming.Normalize();
            _store.Save(incoming);

            var notified = Ipc.SendCommand("reload");
            return Results.Json(new
            {
                ok = true,
                message = notified ? "已保存，主进程已刷新" : "已保存，但主进程未运行",
            });
        });

        application.MapPost("/api/reporting", async (HttpRequest request) =>
        {
            var (parsed, body) = await TryReadAsync<ToggleRequest>(request);
            if (!parsed || body is null)
            {
                return Results.BadRequest(new { ok = false, message = "请求内容无法解析" });
            }

            var config = Clone(_store.Current);
            config.ReportingEnabled = body.Enabled;
            _store.Save(config);
            Ipc.SendCommand("reload");

            return Results.Json(new
            {
                ok = true,
                message = config.ReportingEnabled ? "已开始上报" : "已停止上报",
                reportingEnabled = config.ReportingEnabled,
            });
        });

        application.MapPost("/api/task", async (HttpRequest request) =>
        {
            var (parsed, body) = await TryReadAsync<ToggleRequest>(request);
            if (!parsed || body is null)
            {
                return Results.BadRequest(new { ok = false, message = "请求内容无法解析" });
            }

            var (ok, message) = ScheduledTask.SetEnabled(body.Enabled);
            return Results.Json(new { ok, message, scheduledTaskEnabled = ScheduledTask.IsEnabled() });
        });

        application.MapPost("/api/test", async (HttpRequest request) =>
        {
            var (_, body) = await TryReadAsync<TestRequest>(request);
            var rawUrl = (body?.ServerUrl ?? string.Empty).Trim();
            var secret = body?.Secret ?? string.Empty;

            if (rawUrl.Length == 0)
            {
                return Results.Json(new { ok = false, message = "请先填写远端 Sleepy 实例地址" });
            }

            var serverUrl = NormalizeUrl(rawUrl);
            var result = await ProbeAsync(serverUrl, secret);
            return Results.Json(new { ok = result.Ok, message = result.Message, serverUrl });
        });
    }

    /// <summary>容错读取请求体：解析失败返回 false，由调用方给出 400，而不是抛 500。</summary>
    private static async Task<(bool Parsed, T? Value)> TryReadAsync<T>(HttpRequest request)
    {
        try
        {
            var value = await request.ReadFromJsonAsync<T>();
            return (true, value);
        }
        catch (Exception)
        {
            return (false, default);
        }
    }

    /// <summary>探测 sleepy 的可用接口，顺带把版本号带回来。</summary>
    private static async Task<(bool Ok, string Message)> ProbeAsync(string serverUrl, string secret)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        var probes = new[] { "/api/meta", "/api/status/query" };
        var errors = new List<string>();

        foreach (var path in probes)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, serverUrl.TrimEnd('/') + path);
                if (secret.Length > 0)
                {
                    request.Headers.TryAddWithoutValidation("Sleepy-Secret", secret);
                }

                using var response = await http.SendAsync(request);
                var text = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    errors.Add($"{path} → HTTP {(int)response.StatusCode}");
                    if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        return (false, "密钥不正确（服务端返回 401），请检查 SECRET");
                    }

                    continue;
                }

                var version = ExtractVersion(text);
                return (true, version is null
                    ? $"连接成功，接口 {path} 可用"
                    : $"连接成功，接口 {path} 可用，服务端版本 {version}");
            }
            catch (Exception ex)
            {
                errors.Add($"{path} → {ex.GetBaseException().Message}");
            }
        }

        return (false, "连接失败：" + string.Join("；", errors));
    }

    private static string? ExtractVersion(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.TryGetProperty("version_str", out var versionString))
            {
                return versionString.GetString();
            }

            if (root.TryGetProperty("meta", out var meta)
                && meta.ValueKind == JsonValueKind.Object
                && meta.TryGetProperty("version", out var metaVersion))
            {
                return metaVersion.GetString();
            }
        }
        catch
        {
            // 不是 JSON 就算了，能连上比解析版本重要。
        }

        return null;
    }

    /// <summary>补全协议头：本机地址默认 http，其余默认 https。</summary>
    private static string NormalizeUrl(string url)
    {
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        var isLocal = url.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("127.0.0.1", StringComparison.OrdinalIgnoreCase);

        return (isLocal ? "http://" : "https://") + url;
    }

    /// <summary>保存前先克隆一份，避免直接改到正在被主进程读取的实例。</summary>
    private static AppConfig Clone(AppConfig source)
    {
        var json = JsonSerializer.Serialize(source);
        return JsonSerializer.Deserialize<AppConfig>(json) ?? AppConfig.CreateDefault();
    }

    public async ValueTask DisposeAsync()
    {
        if (_application is not null)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _application.StopAsync(timeout.Token);
            }
            catch
            {
                // 退出阶段忽略。
            }

            await _application.DisposeAsync();
            _application = null;
        }
    }

    private sealed record ToggleRequest(bool Enabled);

    private sealed record TestRequest(string? ServerUrl, string? Secret);
}
