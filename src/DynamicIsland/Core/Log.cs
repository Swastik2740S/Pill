using System.Diagnostics;
using System.IO;

namespace DynamicIsland.Core;

/// <summary>
/// Tiny file logger: %LOCALAPPDATA%\DynamicIsland\log.txt, capped at 1 MB.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly string LogDirectory = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DynamicIsland");
    private static readonly string LogFile = System.IO.Path.Combine(LogDirectory, "log.txt");
    private const long MaxBytes = 1024 * 1024;

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message, Exception? ex = null) =>
        Write("WARN", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}{Environment.NewLine}{ex}");

    private static void Write(string level, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        Debug.WriteLine(line);
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                var info = new FileInfo(LogFile);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Delete(LogFile);
                }
                File.AppendAllText(LogFile, line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
