using System.Diagnostics;
using System.Text;
using UpDevice.Shared;

namespace UpDevice.Tray;

/// <summary>
/// 通过 schtasks 管理「登录时启动托盘」的计划任务（主进程由托盘带起）。
/// 不用 TaskScheduler COM / nuget：只需要建与删，命令行最省事。
///
/// 为什么指向托盘而不是主进程：托盘才是这套程序的入口（图标、配置页都在它身上），
/// 主进程是它拉起来的后台进程。让任务直接起主进程会出现「只有后台进程、没有托盘」的状态，
/// 用户看不到图标，也没法用菜单退出。
/// </summary>
internal static class ScheduledTask
{
    /// <summary>任务名。</summary>
    public const string TaskName = "UpDevice.Tray";

    /// <summary>老版本的任务名：直接启动主进程。升级时会被自动替换。</summary>
    private const string LegacyTaskName = "UpDevice.Service";

    public static bool IsEnabled()
    {
        return Exists(TaskName) || Exists(LegacyTaskName);
    }

    public static (bool Ok, string Message) SetEnabled(bool enabled)
    {
        return enabled ? Create() : Delete();
    }

    /// <summary>
    /// 升级迁移：老版本的计划任务指向主进程，现在改由托盘拉起。
    /// 托盘启动时调一次 —— 老任务存在就删掉，原本是启用状态就按新目标重建，
    /// 免得两个任务并存、开机时既起托盘又起主进程。
    /// </summary>
    public static void MigrateLegacy()
    {
        if (!Exists(LegacyTaskName))
        {
            return;
        }

        Run("/Delete", "/TN", LegacyTaskName, "/F");
        var (ok, message) = Create();
        Logging.Info(ok
            ? $"计划任务已从主进程迁移到托盘：{message}"
            : $"老计划任务已删除，但重建失败（需要在设置页重新启用）：{message}");
    }

    private static bool Exists(string name)
    {
        return Run("/Query", "/TN", name).ExitCode == 0;
    }

    private static (bool Ok, string Message) Create()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "UpDevice.Tray.exe");
        if (!File.Exists(exe))
        {
            return (false, $"未找到托盘程序，请把 UpDevice.Tray.exe 放在同一目录：{exe}");
        }

        // ONLOGON + HIGHEST：登录即启动，且以最高权限运行（托盘建/删计划任务本身也要提权）。
        var result = Run("/Create", "/TN", TaskName, "/TR", $"\"{exe}\"", "/SC", "ONLOGON", "/RL", "HIGHEST", "/F");
        return result.ExitCode == 0
            ? (true, "已启用任务计划（登录时启动托盘）")
            : (false, $"创建计划任务失败（退出码 {result.ExitCode}）：{result.Output}");
    }

    private static (bool Ok, string Message) Delete()
    {
        // 两个名字都尝试删：升级前留下的老任务不能继续在开机时起作用。
        var failed = new List<string>();
        var removed = 0;

        foreach (var name in new[] { TaskName, LegacyTaskName })
        {
            if (!Exists(name))
            {
                continue;
            }

            var result = Run("/Delete", "/TN", name, "/F");
            if (result.ExitCode == 0)
            {
                removed++;
            }
            else
            {
                failed.Add($"{name}（退出码 {result.ExitCode}）");
            }
        }

        if (failed.Count > 0)
        {
            return (false, $"删除计划任务失败：{string.Join("、", failed)}");
        }

        return (true, removed > 0 ? "已禁用任务计划" : "任务计划本来就未启用");
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
