using System;
using System.Diagnostics;
using System.IO;

namespace AnimeLocalTracker.Services;

public static class AppLogger
{
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnimeLocalTracker", "Logs");
    private static readonly string LogPath = Path.Combine(LogDirectory, "app.log");
    private static readonly object LockObj = new();

    public static void Info(string source, string message) => Log("INFO", source, message);
    public static void Warn(string source, string message) => Log("WARN", source, message);
    public static void Error(string source, string message, Exception? ex = null) =>
        Log("ERROR", source, ex != null ? $"{message}: {ex}" : message);

    private static void Log(string level, string source, string message)
    {
        string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] [{source}] {message}";
        Debug.WriteLine(entry);

        lock (LockObj)
        {
            try
            {
                if (!Directory.Exists(LogDirectory))
                {
                    Directory.CreateDirectory(LogDirectory);
                }
                File.AppendAllText(LogPath, entry + Environment.NewLine);
            }
            catch
            {
                // Silently ignore disk write failures to prevent crash in logging mechanism
            }
        }
    }
}
