using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace AnimeLocalTracker.Services;

public record LogEntry(DateTime Timestamp, string Level, string Source, string Message, string? ExceptionDetails = null)
{
    public override string ToString() =>
        $"[{Timestamp:yyyy-MM-dd HH:mm:ss}] [{Level}] [{Source}] {Message}" +
        (!string.IsNullOrEmpty(ExceptionDetails) ? Environment.NewLine + ExceptionDetails : string.Empty);
}

public static class AppLogger
{
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnimeLocalTracker", "Logs");
    private static readonly string LogPath = Path.Combine(LogDirectory, "app.log");
    private static readonly object LockObj = new();
    
    private const int MaxInMemoryLogs = 500;
    private static readonly ConcurrentQueue<LogEntry> _recentLogs = new();

    public static event Action<LogEntry>? LogEmitted;

    public static IReadOnlyCollection<LogEntry> RecentLogs => _recentLogs.ToArray();

    public static void Debug(string source, string message) => Log("DEBUG", source, message);
    public static void Info(string source, string message) => Log("INFO", source, message);
    public static void Warn(string source, string message) => Log("WARN", source, message);
    public static void Error(string source, string message, Exception? ex = null) =>
        Log("ERROR", source, message, ex?.ToString());

    private static void Log(string level, string source, string message, string? exceptionDetails = null)
    {
        var entry = new LogEntry(DateTime.Now, level, source, message, exceptionDetails);
        
        _recentLogs.Enqueue(entry);
        while (_recentLogs.Count > MaxInMemoryLogs && _recentLogs.TryDequeue(out _)) { }

        try
        {
            LogEmitted?.Invoke(entry);
        }
        catch { }

        string formattedLine = entry.ToString();
        System.Diagnostics.Debug.WriteLine(formattedLine);

        lock (LockObj)
        {
            try
            {
                if (!Directory.Exists(LogDirectory))
                {
                    Directory.CreateDirectory(LogDirectory);
                }
                File.AppendAllText(LogPath, formattedLine + Environment.NewLine);
            }
            catch
            {
                // Silently ignore disk write failures to prevent crash in logging mechanism
            }
        }
    }
}
