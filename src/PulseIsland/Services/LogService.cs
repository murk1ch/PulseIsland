using System.IO;
using System.Text;

namespace PulseIsland.Services;

public static class LogService
{
    private static readonly object _sync = new();
    private static string LogFilePath => Path.Combine(Core.PortablePaths.LogsDirectory, $"pulseisland_{DateTime.Now:yyyy-MM-dd}.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null)
    {
        var full = ex is null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
        Write("ERROR", full);
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (_sync)
            {
                Directory.CreateDirectory(Core.PortablePaths.LogsDirectory);
                var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(LogFilePath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Never crash application because of logging failure
        }
    }
}
