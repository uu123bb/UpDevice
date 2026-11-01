using System.Diagnostics;
using UpDevice.Shared;
using UpDevice.Shared.Config;

namespace UpDevice.Launcher;

internal sealed class LauncherForm : Form
{
    private readonly bool _autoStart;
    private readonly ConfigStore _store;
    private readonly CancellationTokenSource _cts = new();

    private readonly Label _versionValue = new() { AutoSize = true, Location = new Point(96, 54) };
    private readonly Label _statusValue = new()
    {
        AutoSize = false,
        Location = new Point(96, 80),
        Size = new Size(370, 20),
        AutoEllipsis = true,
        Text = "就绪",
    };

    private readonly Button _startButton = new() { Text = "启动", Location = new Point(16, 112), Size = new Size(104, 30) };
    private readonly Button _updateButton = new() { Text = "检查更新", Location = new Point(128, 112), Size = new Size(104, 30) };
    private readonly Button _configButton = new() { Text = "打开配置页", Location = new Point(240, 112), Size = new Size(112, 30) };
    private readonly Button _exitButton = new() { Text = "退出", Location = new Point(360, 112), Size = new Size(104, 30) };

    private readonly TextBox _log = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Location = new Point(16, 154),
        Size = new Size(448, 170),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        BackColor = SystemColors.Window,
    };

    public LauncherForm(bool autoStart)
    {
        _autoStart = autoStart;

        Text = "UpDevice 启动器";
        ClientSize = new Size(480, 340);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);

        _store = ConfigStore.Load(ConfigPath.Resolve());

        var title = new Label
        {
            Text = "UpDevice",
            AutoSize = true,
            Location = new Point(16, 14),
            Font = new Font(Font.FontFamily, 14F, FontStyle.Bold),
        };

        var versionCaption = new Label { Text = "当前版本", AutoSize = true, Location = new Point(16, 54), ForeColor = SystemColors.GrayText };
        var statusCaption = new Label { Text = "状态", AutoSize = true, Location = new Point(16, 80), ForeColor = SystemColors.GrayText };

        _versionValue.Text = AppVersion.CurrentText;
        _configButton.Enabled = Directory.Exists(Updater.AppDirectory);

        Controls.AddRange(new Control[]
        {
            title, versionCaption, _versionValue, statusCaption, _statusValue,
            _startButton, _updateButton, _configButton, _exitButton, _log,
        });

        _startButton.Click += (_, _) => StartApps();
        _updateButton.Click += async (_, _) => await CheckUpdatesAsync(silent: false);
        _configButton.Click += (_, _) => OpenConfigPage();
        _exitButton.Click += (_, _) => Close();

        AppendLog($"启动器版本 {AppVersion.CurrentText}");
        AppendLog($"程序目录 {Updater.AppDirectory}");
        AppendLog($"配置文件 {_store.Path}");
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);

        if (_autoStart)
        {
            StartApps();
        }

        if (_store.Current.Update.Enabled)
        {
            await CheckUpdatesAsync(silent: true);
        }
    }

    private void StartApps()
    {
        if (!Directory.Exists(Updater.AppDirectory))
        {
            SetStatus("缺少 UpDevice 目录，无法启动");
            MessageBox.Show($"未找到程序目录：{Updater.AppDirectory}", "UpDevice", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (Updater.StartApps())
        {
            SetStatus("已启动，可在系统托盘查看");
            AppendLog("已拉起托盘（主进程由托盘互拉）");
        }
        else
        {
            SetStatus("启动失败，详见日志");
        }
    }

    private void OpenConfigPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo("http://127.0.0.1:38945/") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus($"打开配置页失败：{ex.Message}");
        }
    }

    private async Task CheckUpdatesAsync(bool silent)
    {
        SetStatus("正在检查更新…");
        SetBusy(true);

        try
        {
            var result = await UpdateChecker.CheckAsync(_store.Current.Update.Repository, _cts.Token);
            AppendLog($"检查更新：{result.Message}");

            if (!result.Ok)
            {
                SetStatus(result.Message);
                if (!silent)
                {
                    MessageBox.Show(result.Message, "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }

                return;
            }

            SetStatus(result.Message);

            if (!result.HasUpdate)
            {
                if (!silent)
                {
                    MessageBox.Show(result.Message, "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }

                return;
            }

            var answer = MessageBox.Show(
                $"发现新版本 {result.LatestVersion}（当前 {result.CurrentVersion}）。\r\n\r\n更新会结束正在运行的程序并替换程序目录，配置不受影响。是否立即更新？",
                "UpDevice 更新",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (answer != DialogResult.Yes)
            {
                return;
            }

            await InstallAsync(result);
        }
        catch (OperationCanceledException)
        {
            // 关闭窗口时的正常取消。
        }
        catch (Exception ex)
        {
            AppendLog($"检查更新失败：{ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task InstallAsync(UpdateCheckResult info)
    {
        var progress = new Progress<string>(SetStatus);
        SetBusy(true);

        try
        {
            await Updater.InstallAsync(info, progress, _cts.Token);
            SetStatus("更新完成，正在重启…");
            AppendLog($"已更新到 {info.LatestVersion}");
            Updater.StartApps();
        }
        catch (OperationCanceledException)
        {
            // 忽略。
        }
        catch (Exception ex)
        {
            SetStatus("更新失败");
            AppendLog($"更新失败：{ex.Message}");
            MessageBox.Show($"更新失败：{ex.Message}", "UpDevice", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _startButton.Enabled = !busy;
        _updateButton.Enabled = !busy;
    }

    private void SetStatus(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => SetStatus(message)));
            return;
        }

        _statusValue.Text = message;
    }

    private void AppendLog(string message)
    {
        Logging.Info(message);

        if (_log.InvokeRequired)
        {
            _log.BeginInvoke(new Action(() => AppendLog(message)));
            return;
        }

        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _cts.Cancel();
        _cts.Dispose();
        _store.Dispose();
        base.OnFormClosed(e);
    }
}
