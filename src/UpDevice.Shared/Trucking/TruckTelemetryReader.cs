using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace UpDevice.Shared.Trucking;

/// <summary>
/// 插件写入共享内存的一帧快照（已从裸内存解析成可用单位）。
/// </summary>
public sealed record TruckTelemetrySnapshot
{
    /// <summary>插件上报的 game_id，如 <c>eurotrucks2</c> / <c>amtrucks</c>。</summary>
    public string GameId { get; init; } = string.Empty;

    /// <summary>由 game_id 反查到的展示名；认不出来时为空。</summary>
    public string GameName { get; init; } = string.Empty;

    /// <summary>游戏处于暂停（菜单、地图等）。</summary>
    public bool Paused { get; init; }

    /// <summary>正在跑任务（有货物）。</summary>
    public bool JobActive { get; init; }

    /// <summary>挂车已连接。</summary>
    public bool TrailerAttached { get; init; }

    /// <summary>游戏内时间（当天分钟数）。</summary>
    public int GameTimeMinutes { get; init; }

    /// <summary>车速，米/秒。</summary>
    public double SpeedMps { get; init; }

    /// <summary>导航限速，米/秒。</summary>
    public double SpeedLimitMps { get; init; }

    /// <summary>当前油量，升。</summary>
    public double FuelLiters { get; init; }

    /// <summary>油箱容量，升。</summary>
    public double FuelCapacityLiters { get; init; }

    /// <summary>续驶里程，公里。</summary>
    public double FuelRangeKm { get; init; }

    /// <summary>车损，0..1（取发动机/变速箱/驾驶室/底盘/轮胎的最大值）。</summary>
    public double WearTruck { get; init; }

    /// <summary>挂损，0..1。</summary>
    public double WearTrailer { get; init; }

    /// <summary>货损，0..1。</summary>
    public double CargoDamage { get; init; }

    /// <summary>导航剩余里程，米。</summary>
    public double NavDistanceMeters { get; init; }

    /// <summary>导航剩余时间，秒。</summary>
    public double NavTimeSeconds { get; init; }

    /// <summary>距下次休息的游戏内分钟数。</summary>
    public int RestStopMinutes { get; init; }

    /// <summary>货物重量，千克。</summary>
    public int CargoMassKg { get; init; }

    /// <summary>任务截止时间（游戏内分钟数），0 表示游戏没提供。</summary>
    public int JobDeadlineMinutes { get; init; }

    public string CargoName { get; init; } = string.Empty;

    public string DestinationCity { get; init; } = string.Empty;

    public string DestinationCompany { get; init; } = string.Empty;

    /// <summary>距插件最后一次写入的毫秒数，用来判断数据是否还新鲜。</summary>
    public int AgeMs { get; init; }

    public bool HasJobCargo => CargoName.Length > 0;

    public bool HasDestination => DestinationCity.Length > 0 || DestinationCompany.Length > 0;
}

/// <summary>
/// 读取遥测插件写入的命名共享内存。
///
/// 布局是**手写固定偏移**而不是 StructLayout —— 这样两边（C++ 插件 / C# 读取端）
/// 的字段位置可以逐字节对齐核对，也不需要在 Shared 项目里打开 unsafe。
/// 改动任何一个字段都必须同时改 <c>plugin.cpp</c> 里的结构体与两边的版本号。
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class TruckTelemetryReader : IDisposable
{
    /// <summary>共享内存对象名，插件侧用同一个名字（含 Local\ 前缀）。</summary>
    public const string MapName = @"Local\UpDeviceTruckTelemetry";

    /// <summary>不带前缀的名字，用于兜底尝试。</summary>
    private const string BareMapName = "UpDeviceTruckTelemetry";

    /// <summary>布局魔数：内存里依次是 'U' 'P' 'D' 'T'。</summary>
    internal const uint Magic = 0x54445055;

    /// <summary>布局版本，与 plugin.cpp 的 UPD_LAYOUT_VERSION 必须一致。</summary>
    internal const uint LayoutVersion = 1;

    /// <summary>整块共享内存大小（字节），与插件侧 sizeof 一致。</summary>
    internal const int SharedSize = 320;

    /// <summary>超过这个时间没更新就认为插件已经停止（游戏退出/崩溃）。</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(3);

    // ---- 固定偏移 ----
    private const int OffsetMagic = 0;
    private const int OffsetStructSize = 4;
    private const int OffsetVersion = 8;
    private const int OffsetSequence = 12;
    private const int OffsetGame = 16;
    private const int OffsetFlags = 20;
    private const int OffsetGameTime = 24;
    private const int OffsetSpeed = 28;
    private const int OffsetSpeedLimit = 32;
    private const int OffsetFuelAmount = 36;
    private const int OffsetFuelCapacity = 40;
    private const int OffsetFuelRange = 44;
    private const int OffsetWearTruck = 48;
    private const int OffsetWearTrailer = 52;
    private const int OffsetCargoDamage = 56;
    private const int OffsetNavDistance = 60;
    private const int OffsetNavTime = 64;
    private const int OffsetRestStop = 68;
    private const int OffsetCargoMass = 72;
    private const int OffsetJobDeadline = 76;
    private const int OffsetCargoName = 80;
    private const int OffsetDestinationCity = 144;
    private const int OffsetDestinationCompany = 208;
    private const int OffsetUpdatedMs = 272;

    private const int NameLength = 64;
    private const int FlagPaused = 0x01;
    private const int FlagJobActive = 0x02;
    private const int FlagTrailerAttached = 0x04;

    private readonly byte[] _buffer = new byte[SharedSize];
    private readonly byte[] _sequenceBuffer = new byte[4];
    private readonly object _gate = new();

    private MemoryMappedFile? _map;
    private MemoryMappedViewAccessor? _view;
    private DateTime _nextOpenAttempt = DateTime.MinValue;

    /// <summary>
    /// 读一帧。游戏没跑、插件没装、数据过期都返回 false（不抛异常）。
    /// </summary>
    public bool TryRead(out TruckTelemetrySnapshot? snapshot)
    {
        snapshot = null;
        if (!EnsureOpen())
        {
            return false;
        }

        lock (_gate)
        {
            try
            {
                // seqlock：写入方把编号改成奇数表示「正在写」，写完再改回偶数。
                var before = ReadSequence();
                if (before == 0 || (before & 1) == 1)
                {
                    return false;
                }

                _view!.ReadArray(0, _buffer, 0, SharedSize);

                var after = BinaryPrimitives.ReadUInt32LittleEndian(_buffer.AsSpan(OffsetSequence));
                if (before != after)
                {
                    return false;
                }
            }
            catch (Exception)
            {
                Close();
                return false;
            }
        }

        snapshot = Parse(_buffer);
        return snapshot is not null;
    }

    private uint ReadSequence()
    {
        _view!.ReadArray(OffsetSequence, _sequenceBuffer, 0, 4);
        return BinaryPrimitives.ReadUInt32LittleEndian(_sequenceBuffer);
    }

    private static TruckTelemetrySnapshot? Parse(byte[] buffer)
    {
        var span = buffer.AsSpan();

        if (BinaryPrimitives.ReadUInt32LittleEndian(span[OffsetMagic..]) != Magic)
        {
            return null;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(span[OffsetVersion..]) != LayoutVersion)
        {
            return null;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(span[OffsetStructSize..]) != SharedSize)
        {
            return null;
        }

        var updatedMs = BinaryPrimitives.ReadUInt32LittleEndian(span[OffsetUpdatedMs..]);
        var age = unchecked((uint)Environment.TickCount - updatedMs);
        if (age > StaleAfter.TotalMilliseconds)
        {
            return null;
        }

        var flags = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[OffsetFlags..]);
        var gameId = ReadGameId(BinaryPrimitives.ReadUInt32LittleEndian(span[OffsetGame..]));

        return new TruckTelemetrySnapshot
        {
            GameId = gameId,
            GameName = TruckGames.FromTelemetryGameId(gameId)?.DisplayName ?? string.Empty,
            Paused = (flags & FlagPaused) != 0,
            JobActive = (flags & FlagJobActive) != 0,
            TrailerAttached = (flags & FlagTrailerAttached) != 0,
            GameTimeMinutes = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[OffsetGameTime..]),
            SpeedMps = BinaryPrimitives.ReadSingleLittleEndian(span[OffsetSpeed..]),
            SpeedLimitMps = BinaryPrimitives.ReadSingleLittleEndian(span[OffsetSpeedLimit..]),
            FuelLiters = BinaryPrimitives.ReadSingleLittleEndian(span[OffsetFuelAmount..]),
            FuelCapacityLiters = BinaryPrimitives.ReadSingleLittleEndian(span[OffsetFuelCapacity..]),
            FuelRangeKm = BinaryPrimitives.ReadSingleLittleEndian(span[OffsetFuelRange..]),
            WearTruck = BinaryPrimitives.ReadSingleLittleEndian(span[OffsetWearTruck..]),
            WearTrailer = BinaryPrimitives.ReadSingleLittleEndian(span[OffsetWearTrailer..]),
            CargoDamage = BinaryPrimitives.ReadSingleLittleEndian(span[OffsetCargoDamage..]),
            NavDistanceMeters = BinaryPrimitives.ReadSingleLittleEndian(span[OffsetNavDistance..]),
            NavTimeSeconds = BinaryPrimitives.ReadSingleLittleEndian(span[OffsetNavTime..]),
            RestStopMinutes = (int)Math.Round(BinaryPrimitives.ReadSingleLittleEndian(span[OffsetRestStop..])),
            CargoMassKg = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[OffsetCargoMass..]),
            JobDeadlineMinutes = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[OffsetJobDeadline..]),
            CargoName = ReadString(span, OffsetCargoName),
            DestinationCity = ReadString(span, OffsetDestinationCity),
            DestinationCompany = ReadString(span, OffsetDestinationCompany),
            AgeMs = (int)age,
        };
    }

    /// <summary>插件写的是枚举（1=ETS2，2=ATS），这里映射回官方 game_id，避免两边各写一份字符串。</summary>
    private static string ReadGameId(uint value)
    {
        return value switch
        {
            1 => TruckGames.Ets2.TelemetryGameId,
            2 => TruckGames.Ats.TelemetryGameId,
            _ => string.Empty,
        };
    }

    private static string ReadString(ReadOnlySpan<byte> span, int offset)
    {
        var slice = span.Slice(offset, NameLength);
        var end = slice.IndexOf((byte)0);
        if (end < 0)
        {
            end = NameLength;
        }

        return end == 0 ? string.Empty : Encoding.UTF8.GetString(slice[..end]).Trim();
    }

    private bool EnsureOpen()
    {
        if (_view is not null)
        {
            return true;
        }

        if (DateTime.UtcNow < _nextOpenAttempt)
        {
            return false;
        }

        // 游戏没跑的时候每轮都去开一次会白烧 CPU，失败后隔一会儿再试。
        _nextOpenAttempt = DateTime.UtcNow.AddSeconds(2);

        lock (_gate)
        {
            try
            {
                _map = TryOpenMap(MapName) ?? TryOpenMap(BareMapName);
                if (_map is null)
                {
                    return false;
                }

                _view = _map.CreateViewAccessor(0, SharedSize, MemoryMappedFileAccess.Read);
                return true;
            }
            catch (Exception ex)
            {
                Logging.Warn($"打开遥测共享内存失败: {ex.GetBaseException().Message}");
                Close();
                return false;
            }
        }
    }

    private static MemoryMappedFile? TryOpenMap(string name)
    {
        try
        {
            return MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Close()
    {
        try
        {
            _view?.Dispose();
        }
        catch
        {
            // 关闭阶段忽略。
        }

        try
        {
            _map?.Dispose();
        }
        catch
        {
            // 关闭阶段忽略。
        }

        _view = null;
        _map = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            Close();
        }
    }
}
