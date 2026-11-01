using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using UpDevice.Shared;

namespace UpDevice.Launcher;

/// <summary>
/// 整包更新：下载 → 校验 → 解压到临时目录 → 结束进程 → 替换 UpDevice/ → 重启。
/// 之所以能整目录替换，是因为程序都装在 UpDevice/ 子目录里，而这个更新器本体在外面。
/// </summary>
internal static class Updater
{
    public const string AppDirectoryName = "UpDevice";
    public const string TrayExeName = "UpDevice.Tray.exe";
    public const string ServiceExeName = "UpDevice.Service.exe";

    public static string AppDirectory => Path.Combine(AppContext.BaseDirectory, AppDirectoryName);

    public static string TrayPath => Path.Combine(AppDirectory, TrayExeName);

    /// <summary>下载并安装更新。返回 true 表示已替换完成（调用方负责重启程序）。</summary>
    public static async Task InstallAsync(
        UpdateCheckResult info,
        IProgress<string> progress,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(info.AssetUrl))
        {
            throw new InvalidOperationException("Release 里没有找到 zip 产物");
        }

        var working = Path.Combine(Path.GetTempPath(), "UpDevice-update", info.LatestVersion ?? "latest");
        Directory.CreateDirectory(working);
        var zipPath = Path.Combine(working, info.AssetName ?? "update.zip");

        progress.Report($"正在下载 {info.AssetName}…");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("UpDevice-Updater");
            using var response = await http.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var target = File.Create(zipPath);
            await response.Content.CopyToAsync(target, token).ConfigureAwait(false);
        }

        progress.Report("正在校验文件…");
        await VerifyDigestAsync(zipPath, info.AssetDigest, token).ConfigureAwait(false);

        progress.Report("正在解压…");
        var staging = AppDirectory + ".new";
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, true);
        }

        ZipFile.ExtractToDirectory(zipPath, staging, overwriteFiles: true);

        // 压缩包根目录就是 UpDevice 目录的内容；如果被打包成带一层 UpDevice/，这里兜一下。
        var nested = Path.Combine(staging, AppDirectoryName);
        var source = File.Exists(Path.Combine(nested, TrayExeName)) ? nested : staging;

        if (!File.Exists(Path.Combine(source, TrayExeName)))
        {
            Directory.Delete(staging, true);
            throw new InvalidOperationException("更新包结构不符合预期：找不到 UpDevice.Tray.exe");
        }

        if (source != staging)
        {
            // 把内层内容提上来，省得替换后目录套目录。
            var flattened = staging + "-flat";
            Directory.Move(source, flattened);
            Directory.Delete(staging, true);
            Directory.Move(flattened, staging);
        }

        progress.Report("正在结束程序…");
        await StopAppsAsync(token).ConfigureAwait(false);

        progress.Report("正在替换程序文件…");
        var backup = AppDirectory + ".old";
        if (Directory.Exists(backup))
        {
            Directory.Delete(backup, true);
        }

        if (Directory.Exists(AppDirectory))
        {
            Directory.Move(AppDirectory, backup);
        }

        Directory.Move(staging, AppDirectory);

        try
        {
            Directory.Delete(backup, true);
        }
        catch
        {
            // 备份删不掉不影响使用，下次更新会再试。
        }
    }

    private static async Task VerifyDigestAsync(string filePath, string? digest, CancellationToken token)
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

    /// <summary>让主进程先上报离线再退出，然后确保两个进程都不在了。</summary>
    private static async Task StopAppsAsync(CancellationToken token)
    {
        if (Ipc.IsServiceRunning)
        {
            Ipc.SendCommand("stop-and-exit");
        }

        await WaitForExitAsync(ServiceExeName, TimeSpan.FromSeconds(6), token).ConfigureAwait(false);
        await WaitForExitAsync(TrayExeName, TimeSpan.FromSeconds(3), token).ConfigureAwait(false);

        foreach (var name in new[] { ServiceExeName, TrayExeName })
        {
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name)))
            {
                try
                {
                    process.Kill();
                    Logging.Warn($"强制结束 {name} (PID {process.Id})");
                }
                catch
                {
                    // 已经退出就忽略。
                }
            }
        }
    }

    private static async Task WaitForExitAsync(string exeName, TimeSpan timeout, CancellationToken token)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exeName));
            if (running.Length == 0)
            {
                return;
            }

            await Task.Delay(200, token).ConfigureAwait(false);
        }
    }

    /// <summary>启动托盘（主进程由托盘自己拉起）。</summary>
    public static bool StartApps()
    {
        if (!File.Exists(TrayPath))
        {
            Logging.Warn($"未找到 {TrayPath}");
            return false;
        }

        if (SiblingProcess.IsRunning(@"Local\UpDevice.Tray"))
        {
            return true;
        }

        try
        {
            Process.Start(new ProcessStartInfo(TrayPath)
            {
                UseShellExecute = false,
                WorkingDirectory = AppDirectory,
            });
            Logging.Info("已启动托盘");
            return true;
        }
        catch (Exception ex)
        {
            Logging.Warn($"启动托盘失败: {ex.GetBaseException().Message}");
            return false;
        }
    }
}
