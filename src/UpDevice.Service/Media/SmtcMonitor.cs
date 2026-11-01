using Windows.Media.Control;

namespace UpDevice.Service.Media;

/// <summary>当前播放信息。</summary>
public sealed record NowPlaying(string Source, string Title, string Artist);

/// <summary>
/// 通过 SMTC 获取系统正在播放的媒体。
/// 按需求只认网易云音乐与 QQ 音乐两个来源，其他播放器（浏览器、PotPlayer 等）一律忽略。
/// </summary>
public sealed class SmtcMonitor : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>AUMID 关键字 → 展示名。QQ 音乐的 AUMID 里是 QQMusic.exe，网易云是 cloudmusic.exe。</summary>
    private static readonly (string Keyword, string DisplayName)[] KnownSources =
    {
        ("cloudmusic", "网易云音乐"),
        ("qqmusic", "QQ音乐"),
    };

    private readonly CancellationTokenSource _cts = new();

    private volatile NowPlaying? _current;

    /// <summary>当前正在播放的、且属于受支持来源的媒体；没有则为 null。</summary>
    public NowPlaying? Current => _current;

    public void Start()
    {
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await PollAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // SMTC 在无会话、服务未启动时会抛 COM 异常，属于预期情况。
                Logging.Warn($"SMTC 读取失败: {ex.Message}");
                _current = null;
            }

            try
            {
                await Task.Delay(PollInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PollAsync()
    {
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().ConfigureAwait(false);
        var sessions = manager.GetSessions();

        foreach (var session in sessions)
        {
            var source = ResolveSource(session.SourceAppUserModelId);
            if (source is null)
            {
                continue;
            }

            var playback = session.GetPlaybackInfo();
            if (playback.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                continue;
            }

            var properties = await session.TryGetMediaPropertiesAsync().AsTask().ConfigureAwait(false);
            var title = properties?.Title ?? string.Empty;
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            _current = new NowPlaying(source, title.Trim(), properties?.Artist ?? string.Empty);
            return;
        }

        _current = null;
    }

    private static string? ResolveSource(string? appUserModelId)
    {
        if (string.IsNullOrWhiteSpace(appUserModelId))
        {
            return null;
        }

        foreach (var (keyword, displayName) in KnownSources)
        {
            if (appUserModelId.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return displayName;
            }
        }

        return null;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
