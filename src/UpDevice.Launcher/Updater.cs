using System.Diagnostics;
using System.IO.Compression;
using UpDevice.Shared;

namespace UpDevice.Launcher;

/// <summary>
/// 版本化目录的启动器：
///   启动 → 找根目录下版本最高的 app-* 目录，跑里面的托盘
///   更新 → 把新版本解压成新的 app-&lt;版本&gt;-&lt;序号&gt; 目录，切过去，顺手清理旧目录
///
/// 因为新旧版本各在各的目录里，替换时不存在「覆盖运行中的文件」问题，
/// 也不需要把旧目录改名再塞新的进去。
/// </summary>
internal static class Updater
{
    public const string TrayExeName = "UpDevice.Tray.exe";
    public const string ServiceExeName = "UpDevice.Service.exe";

    /// <summary>启动器本身在根目录，Root 就是它所在目录。</summary>
    public static string Root => AppPaths.Root;

    public static (string Path, Version Version)? FindLatestApp()
    {
        var candidates = AppPaths.EnumerateAppDirectories(Root);
        if (candidates.Count == 0)
        {
            return null;
        }

        var best = candidates[0];
        return (best.Path, best.Version);
    }

    /// <summary>解压新版本到独立目录，切换过去。返回新版本目录。</summary>
    public static async Task<string> ApplyAsync(string zipPath, string? versionText, CancellationToken token = default)
    {
        if (!File.Exists(zipPath))
        {
            throw new FileNotFoundException("更新包不存在", zipPath);
        }

        if (AppVersion.Parse(versionText) is not { } version)
        {
            throw new InvalidOperationException("更新包缺少可用的版本号");
        }

        var directoryName = NextAppDirectoryName(version);
        var staging = Path.Combine(Root, directoryName + ".new");

        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, true);
        }

        // 先解压再停程序：解压失败就不用打断正在运行的服务。
        ZipFile.ExtractToDirectory(zipPath, staging, overwriteFiles: true);
        FlattenIfNested(staging);

        if (!File.Exists(Path.Combine(staging, TrayExeName)))
        {
            Directory.Delete(staging, true);
            throw new InvalidOperationException($"更新包结构不符合预期：找不到 {TrayExeName}");
        }

        await StopAppsAsync(token).ConfigureAwait(false);

        var target = Path.Combine(Root, directoryName);
        MoveDirectoryWithRetry(staging, target);

        CleanupOldApps(target);
        return target;
    }

    /// <summary>
    /// CI 产出的压缩包是「启动器 + app-&lt;版本&gt;-0/」的完整布局，
    /// 而这里要的只是一个程序目录的内容，所以把里面带托盘的那个目录提出来，
    /// 其余（启动器本身、README）丢掉 —— 启动器不参与自动更新。
    /// </summary>
    private static void FlattenIfNested(string staging)
    {
        if (File.Exists(Path.Combine(staging, TrayExeName)))
        {
            return;
        }

        var candidates = Directory.GetDirectories(staging)
            .Where(directory => File.Exists(Path.Combine(directory, TrayExeName)))
            .ToList();

        if (candidates.Count != 1)
        {
            return;
        }

        var flattened = staging + "-flat";
        Directory.Move(candidates[0], flattened);
        Directory.Delete(staging, true);
        Directory.Move(flattened, staging);
    }

    private static string NextAppDirectoryName(Version version)
    {
        for (var revision = 0; revision < 100; revision++)
        {
            var name = $"{AppPaths.AppDirectoryPrefix}{version}-{revision}";
            if (!Directory.Exists(Path.Combine(Root, name))
                && !Directory.Exists(Path.Combine(Root, name + ".new")))
            {
                return name;
            }
        }

        return $"{AppPaths.AppDirectoryPrefix}{version}-{DateTime.Now:HHmmss}";
    }

    /// <summary>删掉除当前版本外的旧目录。删不掉不致命 —— 下次更新还会再试。</summary>
    private static void CleanupOldApps(string keep)
    {
        foreach (var candidate in AppPaths.EnumerateAppDirectories(Root))
        {
            if (string.Equals(candidate.Path, keep, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                Directory.Delete(candidate.Path, true);
                Logging.Info($"已清理旧版本 {Path.GetFileName(candidate.Path)}");
            }
            catch (Exception ex)
            {
                Logging.Warn($"旧版本 {Path.GetFileName(candidate.Path)} 暂时删不掉，下次再试：{ex.Message}");
            }
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
                    // Kill 只是发信号，必须等它真的退出，否则文件句柄还锁着。
                    process.WaitForExit(5000);
                }
                catch
                {
                    // 已经退出就忽略。
                }
            }
        }

        await WaitForExitAsync(ServiceExeName, TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        await WaitForExitAsync(TrayExeName, TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
    }

    private static async Task WaitForExitAsync(string exeName, TimeSpan timeout, CancellationToken token)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exeName)).Length == 0)
            {
                return;
            }

            await Task.Delay(200, token).ConfigureAwait(false);
        }
    }

    private static void MoveDirectoryWithRetry(string source, string destination, int attempts = 8)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < attempts)
            {
                Logging.Warn($"目录移动被占用，重试 {attempt}/{attempts - 1}");
                Thread.Sleep(500);
            }
        }
    }

    /// <summary>启动版本最高的那套程序（主进程由托盘自己拉起）。</summary>
    public static bool StartApps()
    {
        if (FindLatestApp() is not { } app)
        {
            Logging.Warn($"根目录下没有找到 {AppPaths.AppDirectoryPrefix}* 程序目录：{Root}");
            return false;
        }

        var trayPath = Path.Combine(app.Path, TrayExeName);
        if (!File.Exists(trayPath))
        {
            Logging.Warn($"未找到 {trayPath}");
            return false;
        }

        if (SiblingProcess.IsRunning(@"Local\UpDevice.Tray"))
        {
            return true;
        }

        try
        {
            Process.Start(new ProcessStartInfo(trayPath)
            {
                UseShellExecute = false,
                WorkingDirectory = app.Path,
            });
            Logging.Info($"已启动托盘 {Path.GetFileName(app.Path)}");
            return true;
        }
        catch (Exception ex)
        {
            Logging.Warn($"启动托盘失败: {ex.GetBaseException().Message}");
            return false;
        }
    }
}
