using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace UpDevice.Shared.Config;

/// <summary>
/// config.json 的读写与热重载。
/// 规则：文件缺失就按默认值创建；损坏则备份成 config.json.invalid-* 后重建，不静默吞掉用户数据。
/// </summary>
public sealed class ConfigStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;
    private readonly string _directory;
    private readonly object _gate = new();

    private volatile AppConfig _current;
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _debounce;
    private DateTime _lastSelfWrite = DateTime.MinValue;

    public event Action<AppConfig>? Reloaded;

    public AppConfig Current => _current;

    public string Path => _path;

    private ConfigStore(string path, AppConfig config)
    {
        _path = path;
        _directory = System.IO.Path.GetDirectoryName(path) ?? AppContext.BaseDirectory;
        _current = config;
    }

    public static ConfigStore Load(string path)
    {
        EnsureExists(path);
        var config = ReadFromDisk(path) ?? AppConfig.CreateDefault();

        UpgradeIfNeeded(config, path);
        config.Normalize();

        var store = new ConfigStore(path, config);
        store.StartWatching();
        return store;
    }

    /// <summary>
    /// 版本升级：旧配置按字段补齐后写回，而不是当成损坏文件备份重建。
    /// 版本比程序还新时只提示，不覆盖 —— 免得降级运行时把用户的新配置冲掉。
    /// </summary>
    private static void UpgradeIfNeeded(AppConfig config, string path)
    {
        var version = config.Version <= 0 ? 1 : config.Version;

        if (version > AppConfig.CurrentVersion)
        {
            Logging.Warn($"config.json 版本 {version} 高于当前程序支持的 {AppConfig.CurrentVersion}，将按原样使用且不回写");
            return;
        }

        if (version == AppConfig.CurrentVersion)
        {
            return;
        }

        if (version < 2)
        {
            // v1 → v2：新增 smtc 节点（老配置没有这个字段，补默认值即可）；
            // 原 smtcMode 只有 off / idle，语义不变。
            config.Smtc ??= new SmtcConfig();
            Logging.Info("config.json 从 v1 升级到 v2（新增 SMTC 独立上报配置）");
        }

        config.Version = AppConfig.CurrentVersion;
        config.Normalize();

        try
        {
            File.WriteAllText(path, Serialize(config), new UTF8Encoding(false));
            Logging.Info($"config.json 已升级并写回（v{AppConfig.CurrentVersion}）");
        }
        catch (Exception ex)
        {
            Logging.Warn($"升级后的配置写回失败，本次仍按新结构运行: {ex.Message}");
        }
    }

    /// <summary>文件不存在时写一份默认配置。两个进程都可能调用，所以用 CreateNew 保证幂等。</summary>
    public static void EnsureExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return;
            }

            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = Serialize(AppConfig.CreateDefault());
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            var bytes = new UTF8Encoding(false).GetBytes(json);
            stream.Write(bytes, 0, bytes.Length);
        }
        catch (IOException)
        {
            // 另一个进程刚好抢先创建，忽略即可。
        }
    }

    private static AppConfig? ReadFromDisk(string path)
    {
        try
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            return JsonSerializer.Deserialize<AppConfig>(text, JsonOptions);
        }
        catch (JsonException ex)
        {
            Logging.Warn($"config.json 解析失败，将备份后重建: {ex.Message}");
            BackupBroken(path);
            return null;
        }
        catch (IOException ex)
        {
            Logging.Warn($"config.json 读取失败，本次使用默认配置: {ex.Message}");
            return null;
        }
    }

    private static void BackupBroken(string path)
    {
        try
        {
            var backup = $"{path}.invalid-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(path, backup, overwrite: true);
            Logging.Warn($"已备份损坏的配置到 {System.IO.Path.GetFileName(backup)}");
        }
        catch (Exception ex)
        {
            Logging.Warn($"备份损坏配置失败: {ex.Message}");
        }
    }

    private void StartWatching()
    {
        try
        {
            if (!Directory.Exists(_directory))
            {
                return;
            }

            _watcher = new FileSystemWatcher(_directory, "config.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, _) => ScheduleReload();
            _watcher.Created += (_, _) => ScheduleReload();
            _watcher.Renamed += (_, _) => ScheduleReload();
        }
        catch (Exception ex)
        {
            Logging.Warn($"配置文件监视启动失败，将不会热重载: {ex.Message}");
        }
    }

    private void ScheduleReload()
    {
        lock (_gate)
        {
            _debounce?.Dispose();
            _debounce = new System.Threading.Timer(_ => Reload(), null, 300, Timeout.Infinite);
        }
    }

    /// <summary>立即重载（供托盘通过窗口消息触发）。</summary>
    public void ReloadNow() => Reload();

    private void Reload()
    {
        try
        {
            // 自己写盘触发的变更不需要再读一遍。
            if ((DateTime.UtcNow - _lastSelfWrite).TotalSeconds < 1)
            {
                return;
            }

            var fresh = ReadFromDisk(_path);
            if (fresh is null)
            {
                EnsureExists(_path);
                fresh = ReadFromDisk(_path);
                if (fresh is null)
                {
                    return;
                }
            }

            fresh.Normalize();
            _current = fresh;
            Logging.Info("config.json 已重新加载");
            Reloaded?.Invoke(fresh);
        }
        catch (Exception ex)
        {
            Logging.Warn($"配置热重载失败: {ex.Message}");
        }
    }

    /// <summary>写回配置（主进程一般不写，留给托盘使用）。</summary>
    public void Save(AppConfig config)
    {
        config.Normalize();
        _lastSelfWrite = DateTime.UtcNow;
        var json = Serialize(config);
        File.WriteAllText(_path, json, new UTF8Encoding(false));
        _current = config;
    }

    private static string Serialize(AppConfig config) => JsonSerializer.Serialize(config, JsonOptions);

    public void Dispose()
    {
        _watcher?.Dispose();
        lock (_gate)
        {
            _debounce?.Dispose();
        }
    }
}
