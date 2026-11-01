using System.Text;

namespace UpDevice.Service;

/// <summary>
/// 极简日志：同时写控制台（若被终端启动）与 logs/updevice-yyyyMMdd.log。
/// 隐藏进程没有控制台时只会落文件。
/// </summary>
public static class Logging
{
    private static readonly object Gate = new();
    private static string _logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
    private static bool _consoleBroken;

    public static void Initialize(string? logDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(logDirectory))
        {
            _logDirectory = logDirectory;
        }

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
                Directory.CreateDirectory(_logDirectory);
                File.AppendAllText(Path.Combine(_logDirectory, $"updevice-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch
            {
                // 记日志失败不再抛异常。
            }
        }
    }
}
