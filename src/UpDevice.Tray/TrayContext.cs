using System.Diagnostics;
using UpDevice.Shared;
using UpDevice.Shared.Config;

namespace UpDevice.Tray;

/// <summary>托盘图标与菜单。</summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly TrayWebHost _web;
    private readonly NotifyIcon _icon;

    public TrayContext(TrayWebHost web)
    {
        _web = web;

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("打开 Web 配置页面", null, (_, _) => OpenConfigPage()));

        var autostartItem = new ToolStripMenuItem("开机自动启动托盘")
        {
            CheckOnClick = true,
            Checked = Autostart.IsEnabled(),
        };
        autostartItem.Click += (_, _) =>
        {
            if (!Autostart.SetEnabled(autostartItem.Checked))
            {
                // 写入失败就把勾选状态弹回去，别让界面说谎。
                autostartItem.Checked = !autostartItem.Checked;
                Logging.Warn("开机自启设置失败");
            }
        };
        menu.Items.Add(autostartItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出托盘", null, (_, _) => ExitThread()));
        menu.Items.Add(new ToolStripMenuItem("退出托盘并停止上报", null, (_, _) => ExitAndStopReporting()));

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "UpDevice",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => OpenConfigPage();

        Logging.Info("托盘已启动");
    }

    private void OpenConfigPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_web.Url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logging.Warn($"打开配置页面失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 「退出托盘并停止上报」：让主进程先发一条停止上报文案，再连同主进程一起结束。
    /// 顺序不能反 —— 先杀进程就没机会把状态推给服务端了。
    /// </summary>
    private void ExitAndStopReporting()
    {
        try
        {
            if (Ipc.SendCommand("stop-and-exit"))
            {
                WaitForServiceExit(TimeSpan.FromSeconds(6));
            }
        }
        catch (Exception ex)
        {
            Logging.Warn($"停止上报失败: {ex.Message}");
        }

        ExitThread();
    }

    private static void WaitForServiceExit(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!Ipc.IsServiceRunning)
            {
                return;
            }

            Thread.Sleep(150);
        }

        // 兜底：指令没让它退出就强杀，避免留一个失联进程。
        Logging.Warn("主进程未在超时内退出，强制结束");
        foreach (var process in Process.GetProcessesByName("UpDevice.Service"))
        {
            try
            {
                process.Kill();
            }
            catch (Exception ex)
            {
                Logging.Warn($"结束主进程失败: {ex.Message}");
            }
        }
    }

    protected override void ExitThreadCore()
    {
        _icon.Visible = false;
        _icon.Dispose();
        Logging.Info("托盘已退出");
        base.ExitThreadCore();
    }
}
