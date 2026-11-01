using System.Diagnostics;
using System.IO.Compression;
using UpDevice.Shared;

namespace UpDevice.Launcher;

/// <summary>
/// 启动器只做两件事：把托盘拉起来；需要更新时把 UpDevice/ 目录换掉。
///
/// 之所以由它来换目录：托盘自己跑在 UpDevice/ 里面，没法覆盖自己。
/// 启动器在外面，等进程退出后整目录替换，再重新拉起托盘即可。
/// </summary>
internal static class Updater
{
    public const string AppDirectoryName = "UpDevice";
    public const string TrayExeName = "UpDevice.Tray.exe";
    public const string ServiceExeName = "UpDevice.Service.exe";

    public static string AppDirectory => Path.Combine(AppContext.BaseDirectory, AppDirectoryName);

    public static string TrayPath => Path.Combine(AppDirectory, TrayExeName);

    /// <summary>
    /// 用给定的压缩包替换程序目录。调用前应已完成下载与校验（由托盘负责）。
    /// </summary>
    public static async Task ApplyAsync(string zipPath, string? version, CancellationToken token = default)
    {
        if (!File.Exists(zipPath))
        {
            throw new FileNotFoundException("更新包不存在", zipPath);
        }

        // 先解压再停程序：解压失败就不用打断正在运行的服务。
        var staging = AppDirectory + ".new";
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, true);
        }

        ZipFile.ExtractToDirectory(zipPath, staging, overwriteFiles: true);

        // 兼容压缩包里多套一层 UpDevice/
        var nested = Path.Combine(staging, AppDirectoryName);
        if (File.Exists(Path.Combine(nested, TrayExeName)))
        {
            var flattened = staging + "-flat";
            Directory.Move(nested, flattened);
            Directory.Delete(staging, true);
            Directory.Move(flattened, staging);
        }

        if (!File.Exists(Path.Combine(staging, TrayExeName)))
        {
            Directory.Delete(staging, true);
            throw new InvalidOperationException($"更新包结构不符合预期：找不到 {TrayExeName}");
        }

        // 版本文件由启动器写入，保证「当前版本」跟刚装上的包一致。
        if (!string.IsNullOrWhiteSpace(version))
        {
            await File.WriteAllTextAsync(Path.Combine(staging, AppVersion.FileName), version.Trim(), token)
                .ConfigureAwait(false);
        }

        await StopAppsAsync(token).ConfigureAwait(false);

        var backup = AppDirectory + ".old";
        if (Directory.Exists(backup))
        {
            Directory.Delete(backup, true);
        }

        try
        {
            if (Directory.Exists(AppDirectory))
            {
                MoveDirectoryWithRetry(AppDirectory, backup);
            }

            MoveDirectoryWithRetry(staging, AppDirectory);
        }
        catch
        {
            // 替换失败：把旧目录放回去，清掉暂存，避免留下半套文件。
            if (!Directory.Exists(AppDirectory) && Directory.Exists(backup))
            {
                MoveDirectoryWithRetry(backup, AppDirectory);
            }

            if (Directory.Exists(staging))
            {
                try
                {
                    Directory.Delete(staging, true);
                }
                catch
                {
                    // 删不掉就留着，下次更新会覆盖。
                }
            }

            throw;
        }

        try
        {
            Directory.Delete(backup, true);
        }
        catch
        {
            // 备份删不掉不影响使用，下次更新会再试。
        }
    }

    /// <summary>
    /// 进程刚被杀掉时文件句柄还没释放，直接 Move 会报「文件正被另一进程使用」。
    /// 重试几次是最省事的处理方式。
    /// </summary>
    private static void MoveDirectoryWithRetry(string source, string destination, int attempts = 8)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       && attempt < attempts)
            {
                Logging.Warn($"目录移动被占用，重试 {attempt}/{attempts - 1}");
                Thread.Sleep(500);
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

        // 主进程收到指令后会先上报离线再退出，给它几秒。
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

        // 再确认一遍，避免有残留进程占着目录。
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
