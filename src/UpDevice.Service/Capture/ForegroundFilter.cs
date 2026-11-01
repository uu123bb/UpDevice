namespace UpDevice.Service.Capture;

/// <summary>
/// 前台窗口的屏蔽规则。
///
/// Windows 有一批「临时浮层」会抢走前台窗口：开始菜单、Alt+Tab、通知中心、输入法候选框、
/// 任务栏预览等。它们不是用户真正在用的应用，上报出去会变成一串莫名其妙的窗口名。
/// 命中这里的窗口**不更新**当前前台信息，保留上一次有效结果。
///
/// 标题清单参照 sleepy 官方客户端 client/win_device.py 的 SKIPPED_NAMES / NOT_USING_NAMES，
/// 另外补了进程名与窗口类名两维度的判断（有些浮层的标题是随内容变的，抓不住）。
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
        "LockApp.exe",                  // 锁屏
        "Widgets.exe",                  // 小组件
        "WidgetService.exe",
    };

    /// <summary>
    /// 桌面、任务栏、Alt+Tab 之类的窗口类名。
    /// 没有把 Windows.UI.Core.CoreWindow 放进来 —— UWP 应用本体也用它，会被误杀。
    /// </summary>
    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",                                  // 桌面
        "WorkerW",                                  // 桌面（壁纸层）
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

    public static bool ShouldIgnore(ForegroundInfo info)
    {
        if (info.IsEmpty)
        {
            return true;
        }

        if (IgnoredTitles.Contains(info.WindowTitle))
        {
            return true;
        }

        if (info.ProcessName.Length > 0 && IgnoredProcesses.Contains(info.ProcessName))
        {
            return true;
        }

        return info.ClassName.Length > 0 && IgnoredClasses.Contains(info.ClassName);
    }
}
