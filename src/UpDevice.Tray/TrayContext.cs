using System.Diagnostics;
using UpDevice.Shared;
using UpDevice.Shared.Config;

namespace UpDevice.Tray;

/// <summary>托盘图标与菜单。</summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly ConfigStore _store;
    private readonly TrayWebHost _web;
    private readonly NotifyIcon _icon;

    public TrayContext(ConfigStore store, TrayWebHost web)
    {
        _store = store;
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

    /// <summary>停掉上报再退出托盘；主进程留在后台等待下次启动（不强制结束它）。</summary>
    private void ExitAndStopReporting()
    {
        try
        {
            var config = _store.Current;
            config.ReportingEnabled = false;
            _store.Save(config);

            Ipc.SendCommand("shutdown-report");
            Ipc.SendCommand("reload");
        }
        catch (Exception ex)
        {
            Logging.Warn($"停止上报失败: {ex.Message}");
        }

        ExitThread();
    }

    protected override void ExitThreadCore()
    {
        _icon.Visible = false;
        _icon.Dispose();
        Logging.Info("托盘已退出");
        base.ExitThreadCore();
    }
}
