using System.Text;

namespace UpDevice.Shared;

/// <summary>
/// 极简日志：同时写控制台（若被终端启动）与 <c>data/Logs/updevice-yyyyMMdd.log</c>。
/// 隐藏进程没有控制台时只会落文件。日志跟着 data 走，不随程序目录更新而丢失。
/// </summary>
public static class Logging
{
    private static readonly object Gate = new();
    private static string? _logDirectory;
    private static bool _consoleBroken;

    private static string LogDirectory => _logDirectory ??= AppPaths.LogsDirectory;

    public static void Initialize(string? logDirectory = null)
    {
        // 延迟到用时再取，确保 AppPaths 已经能判断出当前是哪种布局。
        _logDirectory = string.IsNullOrWhiteSpace(logDirectory) ? AppPaths.LogsDirectory : logDirectory;

        try
        {
            Directory.CreateDirectory(_logDirectory);
        }
        catch
        {
            // 目录建不出来就只写控制台。
        }

        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
            _consoleBroken = true;
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";
        lock (Gate)
        {
            if (!_consoleBroken)
            {
                try
                {
                    Console.WriteLine(line);
                }
                catch
                {
                    _consoleBroken = true;
                }
            }

            try
            {
                var directory = LogDirectory;
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, $"updevice-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch
            {
                // 记日志失败不再抛异常。
            }
        }
    }
}
