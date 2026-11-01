using UpDevice.Service.Capture;
using UpDevice.Service.Media;
using UpDevice.Service.Native;
using UpDevice.Service.Reporting;
using UpDevice.Shared;
using UpDevice.Shared.Config;

namespace UpDevice.Service;

internal static class Program
{
    /// <summary>隐藏窗口的标题，托盘进程靠它 FindWindow 后发指令。</summary>
    internal const string WindowCaption = "UpDevice.Service.Hidden";

    [STAThread]
    private static int Main(string[] args)
    {
        var dryRun = args.Any(a => a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase));
        var once = args.Any(a => a.Equals("--once", StringComparison.OrdinalIgnoreCase));

        Logging.Initialize();
        Logging.Info("==== UpDevice.Service 启动 ====");

        // 单实例：计划任务与手动启动可能撞车。
        using var singleInstance = new Mutex(true, @"Local\UpDevice.Service", out var isFirstInstance);
        if (!isFirstInstance)
        {
            Logging.Warn("已有 UpDevice.Service 在运行，本次退出");
            return 0;
        }

        var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        using var store = ConfigStore.Load(configPath);
        Logging.Info($"配置文件: {store.Path}");

        var activity = new ActivityMonitor();

        if (once)
        {
            return RunOnce(store, activity, dryRun);
        }

        using var foreground = new ForegroundCapture();
        using var battery = new BatteryMonitor();
        using var smtc = new SmtcMonitor();
        using var controller = new ControllerMonitor(activity, () => store.Current.Report);
        using var client = new SleepyClient();
        using var reporter = new Reporter(store, foreground, activity, battery, smtc, client, dryRun);

        foreground.Start();
        battery.Start();
        smtc.Start();
        controller.Start();
        reporter.Start();

        using var window = new HiddenMessageWindow();
        window.SessionEnding += reporter.ReportShutdown;
        window.CommandReceived += command => HandleCommand(command, store, reporter, window);

        Logging.Info(dryRun ? "已进入运行状态（DRY-RUN，不真正发送请求）" : "已进入运行状态");

        // 主进程被单独拉起时，把托盘也带起来（托盘已在运行则跳过）。
        SiblingProcess.EnsureRunning("UpDevice.Tray.exe", @"Local\UpDevice.Tray");

        Application.Run(new ApplicationContext());

        Logging.Info("==== UpDevice.Service 退出 ====");
        return 0;
    }

    /// <summary>冒烟测试：采一轮、打印结果就退出，方便确认各采集器是否正常。</summary>
    private static int RunOnce(ConfigStore store, ActivityMonitor activity, bool dryRun)
    {
        using var foreground = new ForegroundCapture();
        using var battery = new BatteryMonitor();
        using var smtc = new SmtcMonitor();
        using var client = new SleepyClient();
        using var reporter = new Reporter(store, foreground, activity, battery, smtc, client, dryRun);

        foreground.Start();
        battery.Start();
        smtc.Start();

        // 给采集器一点时间出数据。
        Thread.Sleep(2500);

        var config = store.Current;
        var (usingFlag, status) = reporter.Compose(config);
        var current = foreground.Current;

        Logging.Info("---- 采集结果 ----");
        Logging.Info($"前台进程: {current.ProcessName}");
        Logging.Info($"窗口标题: {current.WindowTitle}");
        Logging.Info($"窗口类名: {current.ClassName}");
        Logging.Info($"空闲时长: {activity.IdleSeconds(config.Report.ControllerEnabled):F0}s (阈值 {config.Report.IdleTimeoutSeconds}s)");
        Logging.Info($"电池: {(battery.IsAvailable ? battery.Format(config.Texts) : "不可用")}");
        Logging.Info($"SMTC: {(smtc.Current is { } playing ? $"{playing.Source} - {playing.Title}" : "无")}");
        Logging.Info($"待上报: using={usingFlag} status=\"{status}\"");
        return 0;
    }

    private static void HandleCommand(string command, ConfigStore store, Reporter reporter, HiddenMessageWindow window)
    {
        switch (command.Trim().ToLowerInvariant())
        {
            case "reload":
                Logging.Info("收到 reload 指令");
                store.ReloadNow();
                break;

            case "shutdown-report":
                Logging.Info("收到 shutdown-report 指令");
                reporter.ReportShutdown();
                break;

            case "stop-and-exit":
                // 托盘「退出并停止上报」：先上报停止文案，再把自己结束掉。
                // 放到后台线程做，避免阻塞窗口消息线程（托盘那边 SendMessage 会同步等待）。
                Logging.Info("收到 stop-and-exit 指令");
                _ = Task.Run(() =>
                {
                    try
                    {
                        reporter.ReportOffline("[设备已停止上报]");
                    }
                    catch (Exception ex)
                    {
                        Logging.Warn($"停止上报文案发送失败: {ex.Message}");
                    }
                    finally
                    {
                        window.RequestExit();
                    }
                });
                break;

            case "exit":
                Logging.Info("收到 exit 指令");
                Application.ExitThread();
                break;

            default:
                Logging.Warn($"未知指令: {command}");
                break;
        }
    }
}
