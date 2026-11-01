namespace UpDevice.Service.Capture;

/// <summary>前台窗口的处置结果。</summary>
internal enum ForegroundVerdict
{
    /// <summary>正常应用，更新当前前台信息。</summary>
    Accept,

    /// <summary>临时浮层（Alt+Tab、通知中心、输入法…），保留上一次的结果，不更新。</summary>
    Skip,

    /// <summary>
    /// 桌面：没有前台应用，文案显示待机；
    /// 但**不等于离线** —— 可能只是坐在桌面上等任务，是否离线交给空闲时间判断。
    /// </summary>
    Desktop,

    /// <summary>锁屏：用户确实不在，直接离线。</summary>
    Locked,
}

/// <summary>
/// 前台窗口的判定规则。
///
/// 分四档，别混在一起：
///   Locked  —— 锁屏：直接离线
///   Desktop —— 桌面：文案显示待机，但在线状态交给空闲时间（可能在等任务）
///   Skip    —— 临时浮层：保留上一次的应用名（否则状态会抖）
///   Accept  —— 其余：正常应用
///
/// 标题清单参照 sleepy 官方客户端 client/win_device.py 的 SKIPPED_NAMES / NOT_USING_NAMES，
/// 另外补了进程名与窗口类名两维度的判断（有些浮层标题随内容变，靠标题抓不住）。
/// </summary>
internal static class ForegroundFilter
{
    /// <summary>标题命中即忽略（与参考脚本一致，逐字匹配）。</summary>
    private static readonly HashSet<string> IgnoredTitles = new(StringComparer.Ordinal)
    {
        "",
        "系统托盘溢出窗口。",
        "新通知",
        "任务切换",
        "快速设置",
        "通知中心",
        "操作中心",
        "日期和时间信息",
        "网络连接",
        "电池信息",
        "搜索",
        "Flow.Launcher",
        "任务视图",
        "Snipper - Snipaste",

        // 参考脚本里归在 NOT_USING_NAMES，这里一并按「不是有效状态」处理
        "我们喜欢这张图片，因此我们将它与你共享。",
        "启动",
    };

    /// <summary>系统外壳进程：它们的窗口基本都不是用户正在使用的应用。</summary>
    private static readonly HashSet<string> IgnoredProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "ShellExperienceHost.exe",      // 操作中心 / 快速设置 / 音量浮层
        "StartMenuExperienceHost.exe",  // 开始菜单
        "SearchHost.exe",               // 搜索
        "SearchApp.exe",                // 搜索（旧版）
        "TextInputHost.exe",            // 输入法候选窗
        "Widgets.exe",                  // 小组件
        "WidgetService.exe",
    };

    /// <summary>锁屏进程：用户不在用电脑，应报待机。</summary>
    private static readonly HashSet<string> LockScreenProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "LockApp.exe",
    };

    /// <summary>桌面窗口类名：前台是它说明用户回到了桌面。</summary>
    private static readonly HashSet<string> DesktopClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",      // 桌面
        "WorkerW",      // 桌面（壁纸层）
    };

    /// <summary>
    /// 任务栏、Alt+Tab 之类的窗口类名。
    /// 没有把 Windows.UI.Core.CoreWindow 放进来 —— UWP 应用本体也用它，会被误杀。
    /// </summary>
    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd",                            // 任务栏
        "Shell_SecondaryTrayWnd",                   // 副屏任务栏
        "NotifyIconOverflowWindow",                 // 托盘溢出
        "TopLevelWindowForOverflowXamlIsland",      // 托盘溢出（Win11）
        "XamlExplorerHostIslandWindow",             // 任务视图 / Alt+Tab（Win11）
        "MultitaskingViewFrame",                    // 任务视图
        "ForegroundStaging",                        // 前台切换中转窗口
        "TaskListThumbnailWnd",                     // 任务栏缩略图预览
        "IME",                                      // 输入法
    };

    public static ForegroundVerdict Evaluate(ForegroundInfo info, bool sessionLocked)
    {
        // 锁屏：人确实不在
        if (sessionLocked)
        {
            return ForegroundVerdict.Locked;
        }

        if (info.ProcessName.Length > 0 && LockScreenProcesses.Contains(info.ProcessName))
        {
            return ForegroundVerdict.Locked;
        }

        // 桌面：文案待机，但在不在线看空闲时间
        if (info.ClassName.Length > 0 && DesktopClasses.Contains(info.ClassName))
        {
            return ForegroundVerdict.Desktop;
        }

        // 临时浮层：保留上一次
        if (info.IsEmpty)
        {
            return ForegroundVerdict.Skip;
        }

        if (IgnoredTitles.Contains(info.WindowTitle))
        {
            return ForegroundVerdict.Skip;
        }

        if (info.ProcessName.Length > 0 && IgnoredProcesses.Contains(info.ProcessName))
        {
            return ForegroundVerdict.Skip;
        }

        if (info.ClassName.Length > 0 && IgnoredClasses.Contains(info.ClassName))
        {
            return ForegroundVerdict.Skip;
        }

        return ForegroundVerdict.Accept;
    }
}
