using System.Diagnostics;

namespace UpDevice.Shared;

/// <summary>
/// 两个程序互相拉起对方：启动时发现对方没在跑就顺手拉起来。
/// 只在启动时做一次，不做持续守护 —— 否则「退出托盘」会被立刻拽回来。
/// 用命名 Mutex 判断是否已在运行，避免互相拉起形成死循环。
/// </summary>
public static class SiblingProcess
{
    public static bool IsRunning(string mutexName)
    {
        if (string.IsNullOrWhiteSpace(mutexName))
        {
            return false;
        }

        try
        {
            using var mutex = Mutex.OpenExisting(mutexName);
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // 打不开句柄说明对象确实存在，只是权限不够。
            return true;
        }
    }

    /// <summary>
    /// 确保同目录下的 <paramref name="exeName"/> 正在运行；已在运行或文件不存在时直接返回。
    /// </summary>
    public static bool EnsureRunning(string exeName, string mutexName)
    {
        if (IsRunning(mutexName))
        {
            return true;
        }

        var path = Path.Combine(AppContext.BaseDirectory, exeName);
        if (!File.Exists(path))
        {
            Logging.Warn($"未找到 {exeName}，跳过自动拉起");
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            });
            Logging.Info($"已拉起 {exeName}");
            return true;
        }
        catch (Exception ex)
        {
            // 常见于托盘需要 UAC 而主进程未提权：ERROR_ELEVATION_REQUIRED。
            Logging.Warn($"拉起 {exeName} 失败: {ex.GetBaseException().Message}");
            return false;
        }
    }
}
