namespace UpDevice.Shared.Trucking;

/// <summary>
/// 一个受支持游戏的固定信息。id 是配置里的键，其余字段只用于探测与文案。
/// </summary>
/// <param name="Id">配置里的键，如 <c>ats</c> / <c>ets2</c>。</param>
/// <param name="DisplayName">对外显示的名字，同时作为 %game% 的输出。</param>
/// <param name="SteamAppId">Steam AppID，用于读 appmanifest / 判断是否装过。</param>
/// <param name="InstallFolder">steamapps/common 下的目录名。</param>
/// <param name="ProcessName">游戏进程名（小写，带 .exe），用于判断是否在前台。</param>
/// <param name="TelemetryGameId">SDK 初始化参数里的 game_id。</param>
public sealed record TruckGameDefinition(
    string Id,
    string DisplayName,
    string SteamAppId,
    string InstallFolder,
    string ProcessName,
    string TelemetryGameId);

/// <summary>ATS / ETS2 的静态定义与查找。</summary>
public static class TruckGames
{
    public static readonly TruckGameDefinition Ats = new(
        "ats",
        "American Truck Simulator",
        "270880",
        "American Truck Simulator",
        "ats.exe",
        "amtrucks");

    public static readonly TruckGameDefinition Ets2 = new(
        "ets2",
        "Euro Truck Simulator 2",
        "227300",
        "Euro Truck Simulator 2",
        "eurotrucks2.exe",
        "eurotrucks2");

    /// <summary>设置页按这个顺序列出：先 ATS 后 ETS2。</summary>
    public static readonly IReadOnlyList<TruckGameDefinition> All = new[] { Ats, Ets2 };

    public static bool IsKnown(string? id)
    {
        return !string.IsNullOrWhiteSpace(id)
            && All.Any(g => string.Equals(g.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public static TruckGameDefinition? Find(string? id)
    {
        return All.FirstOrDefault(g => string.Equals(g.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>按前台进程名找游戏（大小写与路径都不敏感）。</summary>
    public static TruckGameDefinition? FromProcess(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return null;
        }

        var name = Path.GetFileName(processName.Trim());
        return All.FirstOrDefault(g => string.Equals(g.ProcessName, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>按插件上报的 game_id 找游戏。</summary>
    public static TruckGameDefinition? FromTelemetryGameId(string? telemetryGameId)
    {
        if (string.IsNullOrWhiteSpace(telemetryGameId))
        {
            return null;
        }

        return All.FirstOrDefault(g => string.Equals(g.TelemetryGameId, telemetryGameId.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
