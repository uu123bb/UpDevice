using UpDevice.Shared.Trucking;

namespace UpDevice.Shared.Config;

/// <summary>
/// 公路与车辆（ATS / ETS2 遥测）上报设置。v4 新增。
///
/// 这里的开关只管「要不要把遥测信息合并进上报文案」，
/// 插件装没装、游戏在不在跑都由托盘/主进程实时判断，不落进配置。
/// </summary>
public sealed class TruckingConfig
{
    /// <summary>默认上报格式，与设置页示例一致。</summary>
    public const string DefaultFormat = "[%game%] %driver_status%·%speed%·%fuel_distance%";

    /// <summary>格式串长度上限，防止手改配置塞进几 MB 的字符串。</summary>
    public const int MaxFormatLength = 500;

    /// <summary>全局开关：关闭后即使游戏在前台也不做遥测上报。</summary>
    public bool Enabled { get; set; }

    /// <summary>上报模板，支持 %变量% 与 %% 转义。</summary>
    public string Format { get; set; } = DefaultFormat;

    /// <summary>使用英制单位（mph / mi），默认公制（kph / km）。</summary>
    public bool Imperial { get; set; }

    /// <summary>各游戏的自定义路径（只记录用户手动指定过的）。</summary>
    public List<TruckGameConfig> Games { get; set; } = new();

    public void Normalize()
    {
        Format = string.IsNullOrWhiteSpace(Format) ? DefaultFormat : Format.Trim();
        if (Format.Length > MaxFormatLength)
        {
            Format = Format[..MaxFormatLength];
        }

        Games ??= new List<TruckGameConfig>();

        // 未知 id 与重复 id 直接丢掉：配置是用户可手改的，不能假设干净。
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cleaned = new List<TruckGameConfig>(Games.Count);
        foreach (var game in Games)
        {
            if (game is null || !TruckGames.IsKnown(game.Id) || !seen.Add(game.Id))
            {
                continue;
            }

            game.Id = game.Id.Trim().ToLowerInvariant();
            game.Path = (game.Path ?? string.Empty).Trim();
            cleaned.Add(game);
        }

        Games = cleaned;
    }

    /// <summary>取某个游戏的自定义路径配置，没有就返回一个空壳（不写回集合）。</summary>
    public TruckGameConfig For(string gameId)
    {
        return Games.FirstOrDefault(g => string.Equals(g.Id, gameId, StringComparison.OrdinalIgnoreCase))
            ?? new TruckGameConfig { Id = gameId };
    }
}

/// <summary>单个游戏的路径覆盖。空 <see cref="Path"/> 表示用自动探测到的 Steam 路径。</summary>
public sealed class TruckGameConfig
{
    /// <summary>游戏 id，见 <see cref="TruckGames"/>。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>用户指定的游戏根目录（含 bin/ 的那一层）。空表示自动探测。</summary>
    public string Path { get; set; } = string.Empty;
}
