using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
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
            currentVersion = AppVersion.CurrentText,
        }));

        application.MapGet("/api/config", () => Results.Json(_store.Current));

        application.MapPost("/api/config", async (HttpRequest request) =>
        {
            var (parsed, incoming) = await TryReadAsync<AppConfig>(request);
            if (!parsed || incoming is null)
            {
                return Results.BadRequest(new { ok = false, message = "配置内容无法解析" });
            }

            var previous = _store.Current;
            incoming.Normalize();

            if (incoming.Report.SmtcMode == SmtcModes.Separate)
            {
                if (string.IsNullOrWhiteSpace(incoming.Smtc.DeviceId) || string.IsNullOrWhiteSpace(incoming.Smtc.ShowName))
                {
                    return Results.BadRequest(new { ok = false, message = "单独上报需要填写 SMTC 设备名称与设备 ID" });
                }

                if (string.Equals(incoming.Smtc.DeviceId, incoming.Remote.DeviceId, StringComparison.OrdinalIgnoreCase))
                {
                    return Results.BadRequest(new { ok = false, message = "SMTC 设备 ID 不能和设备的设备 ID 相同" });
                }
            }

            ScheduleSmtcRemoval(previous, incoming);
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

            var serverUrl = SleepyApi.NormalizeUrl(rawUrl);
            var probe = await SleepyApi.ProbeAsync(serverUrl, CancellationToken.None);
            if (!probe.Ok)
            {
                return Results.Json(new { ok = false, message = probe.Message, serverUrl });
            }

            var message = $"连接成功：{probe.Message}";

            if (secret.Length > 0)
            {
                var (secretOk, secretMessage) = await VerifySecretAsync(serverUrl, probe.Flavor, secret);
                return Results.Json(new { ok = secretOk, message = $"{message}；{secretMessage}", serverUrl });
            }

            return Results.Json(new { ok = true, message, serverUrl });
        });

        // 只检查不安装：下载与替换文件由启动器负责。
        application.MapPost("/api/update/check", async () =>
        {
            var result = await UpdateChecker.CheckAsync(_store.Current.Update.Repository, CancellationToken.None);
            return Results.Json(result);
        });

        // 下载交给托盘（只有它能显示进度），替换目录交给启动器（只有它在 UpDevice/ 外面）。
        application.MapPost("/api/update/install", async () =>
        {
            var check = await UpdateChecker.CheckAsync(_store.Current.Update.Repository, CancellationToken.None);
            if (!check.Ok)
            {
                return Results.Json(new { ok = false, message = check.Message });
            }

            if (!check.HasUpdate)
            {
                return Results.Json(new { ok = false, message = "已经是最新版本" });
            }

            var launcher = AppPaths.LauncherPath;
            if (!File.Exists(launcher))
            {
                return Results.Json(new { ok = false, message = "未找到 UpDevice.Launcher.exe，无法自动安装更新" });
            }

            string zipPath;
            try
            {
                zipPath = await UpdateInstaller.DownloadAsync(check, CancellationToken.None);
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, message = $"下载失败：{ex.GetBaseException().Message}" });
            }

            try
            {
                var startInfo = new ProcessStartInfo(launcher)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(launcher)!,
                };
                startInfo.ArgumentList.Add("--apply-update");
                startInfo.ArgumentList.Add(zipPath);
                startInfo.ArgumentList.Add("--version");
                startInfo.ArgumentList.Add((check.LatestVersion ?? string.Empty).TrimStart('v', 'V'));

                Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, message = $"启动更新程序失败：{ex.GetBaseException().Message}" });
            }

            // 让主进程上报离线后退出；本进程也退，把 UpDevice/ 让出来给启动器替换。
            Ipc.SendCommand("stop-and-exit");
            _ = Task.Run(async () =>
            {
                await Task.Delay(1000);
                Environment.Exit(0);
            });

            return Results.Json(new { ok = true, message = "开始更新，程序将退出并在完成后自动重启" });
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

    /// <summary>
    /// 校验 secret，且不产生副作用：故意发一个空请求体。
    /// 服务端会先验密钥（错误 → 401），再校验参数（缺字段 → 400），
    /// 所以拿到 400 就说明密钥是对的，同时什么也没写进去。
    /// </summary>
    private static async Task<(bool Ok, string Message)> VerifySecretAsync(
        string serverUrl,
        SleepyApiFlavor flavor,
        string secret)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var request = new HttpRequestMessage(HttpMethod.Post, serverUrl + SleepyApi.DeviceSetPath(flavor))
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("Sleepy-Secret", secret);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);

            using var response = await http.SendAsync(request);
            return response.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized => (false, "密钥不正确（服务端返回 401）"),
                System.Net.HttpStatusCode.BadRequest => (true, "密钥校验通过"),
                System.Net.HttpStatusCode.OK => (true, "密钥校验通过"),
                _ => (true, $"密钥校验返回 HTTP {(int)response.StatusCode}，请在首次上报时留意日志"),
            };
        }
        catch (Exception ex)
        {
            return (false, $"密钥校验失败：{ex.GetBaseException().Message}");
        }
    }

    /// <summary>
    /// 关闭「单独上报」或改了 SMTC 设备 ID 时，把旧设备标记为待删除。
    /// 真正删除由主进程执行 —— 托盘不一定连得上服务端，而主进程本来就在跑上报循环；
    /// 标记只有删除成功才会被清掉，所以主进程没运行时下次启动也会补删。
    /// </summary>
    private static void ScheduleSmtcRemoval(AppConfig previous, AppConfig incoming)
    {
        var previousId = previous.Smtc.DeviceId;

        if (previous.Report.SmtcMode != SmtcModes.Separate || string.IsNullOrWhiteSpace(previousId))
        {
            return;
        }

        if (string.Equals(previousId, previous.Remote.DeviceId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var stillSeparate = incoming.Report.SmtcMode == SmtcModes.Separate;
        var sameId = string.Equals(previousId, incoming.Smtc.DeviceId, StringComparison.OrdinalIgnoreCase);
        if (stillSeparate && sameId)
        {
            return;
        }

        incoming.Smtc.PendingRemoveId = previousId;
        Logging.Info($"已标记待删除 SMTC 设备 {previousId}");
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
