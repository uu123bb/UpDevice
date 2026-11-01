using System.Diagnostics;
using System.Text;
using UpDevice.Shared;

namespace UpDevice.Tray;

/// <summary>
/// 通过 schtasks 管理「登录时启动主进程」的计划任务。
/// 不用 TaskScheduler COM / nuget：只需要建与删，命令行最省事。
/// </summary>
internal static class ScheduledTask
{
    public const string TaskName = "UpDevice.Service";

    public static bool IsEnabled()
    {
        var result = Run("/Query", "/TN", TaskName);
        return result.ExitCode == 0;
    }

    public static (bool Ok, string Message) SetEnabled(bool enabled)
    {
        return enabled ? Create() : Delete();
    }

    private static (bool Ok, string Message) Create()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "UpDevice.Service.exe");
        if (!File.Exists(exe))
        {
            return (false, $"未找到主程序，请把 UpDevice.Service.exe 放在同一目录：{exe}");
        }

        // ONLOGON + HIGHEST：登录即启动，且以最高权限运行。
        var result = Run("/Create", "/TN", TaskName, "/TR", $"\"{exe}\"", "/SC", "ONLOGON", "/RL", "HIGHEST", "/F");
        return result.ExitCode == 0
            ? (true, "已启用任务计划")
            : (false, $"创建计划任务失败（退出码 {result.ExitCode}）：{result.Output}");
    }

    private static (bool Ok, string Message) Delete()
    {
        var result = Run("/Delete", "/TN", TaskName, "/F");
        return result.ExitCode == 0
            ? (true, "已禁用任务计划")
            : (false, $"删除计划任务失败（退出码 {result.ExitCode}）：{result.Output}");
    }

    private static (int ExitCode, string Output) Run(params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return (-1, "无法启动 schtasks.exe");
            }

            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(15000);

            return (process.HasExited ? process.ExitCode : -1, output.Trim());
        }
        catch (Exception ex)
        {
            Logging.Warn($"调用 schtasks 失败: {ex.Message}");
            return (-1, ex.Message);
        }
    }
}
