using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Services;

public record LogEntry(DateTime Timestamp, string Level, string Source, string Message, string? ExceptionDetails = null)
{
    public override string ToString() =>
        $"[{Timestamp:yyyy-MM-dd HH:mm:ss}] [{Level}] [{Source}] {Message}" +
        (!string.IsNullOrEmpty(ExceptionDetails) ? Environment.NewLine + ExceptionDetails : string.Empty);
}

/// <summary>
/// Logger de la aplicación.
/// - Enqueue es O(1) y nunca bloquea al llamador (el bucle de tracking del reproductor
///   y los ticks de descarga loguean a alta frecuencia desde el hilo de UI).
/// - Un único consumidor en segundo plano escribe a disco en lotes.
/// - Rotación por tamaño para que app.log no crezca sin límite.
/// </summary>
public static class AppLogger
{
    private static readonly string LogDirectory = Environment.GetEnvironmentVariable("ANIMELOCALTRACKER_LOG_DIR") ?? AppDataPaths.LogsDir;
    private static readonly string LogPath = Path.Combine(LogDirectory, "app.log");

    private const int MaxInMemoryLogs = 500;
    private const long MaxLogBytes = 5 * 1024 * 1024; // 5 MB por archivo
    private const int BatchFlushMs = 500;
    private const int MaxBatchSize = 128;

    private static readonly ConcurrentQueue<LogEntry> _recentLogs = new();
    private static readonly Channel<LogEntry> _cola = Channel.CreateUnbounded<LogEntry>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    public static event Action<LogEntry>? LogEmitted;

    public static IReadOnlyCollection<LogEntry> RecentLogs => _recentLogs.ToArray();

    static AppLogger()
    {
        _ = Task.Run(ProcesarColaAsync);

        // Intentar vaciar la cola al cerrar la aplicación
        AppDomain.CurrentDomain.ProcessExit += (s, e) => VaciarColaSincrono();
    }

    public static void Debug(string source, string message) => Log("DEBUG", source, message);
    public static void Info(string source, string message) => Log("INFO", source, message);
    public static void Warn(string source, string message) => Log("WARN", source, message);
    public static void Error(string source, string message, Exception? ex = null) =>
        Log("ERROR", source, message, ex?.ToString());

    private static void Log(string level, string source, string message, string? exceptionDetails = null)
    {
        // SEC-12: nunca volcar rutas completas del perfil del usuario en los logs.
        message = Sanitizar(message);
        exceptionDetails = Sanitizar(exceptionDetails);

        var entry = new LogEntry(DateTime.Now, level, source, message, exceptionDetails);

        _recentLogs.Enqueue(entry);
        while (_recentLogs.Count > MaxInMemoryLogs && _recentLogs.TryDequeue(out _)) { }

        try
        {
            LogEmitted?.Invoke(entry);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AppLogger] Error en suscriptor de LogEmitted: {ex.Message}");
        }

        System.Diagnostics.Debug.WriteLine(entry.ToString());

        // Nunca bloquea: si el canal está lleno (imposible con Unbounded, pero por robustez) se descarta
        _cola.Writer.TryWrite(entry);
    }

    private static async Task ProcesarColaAsync()
    {
        var lote = new List<LogEntry>(MaxBatchSize);
        var lector = _cola.Reader;

        try
        {
            while (await lector.WaitToReadAsync().ConfigureAwait(false))
            {
                lote.Clear();

                // Drenar lo disponible hasta el tamaño de lote
                while (lote.Count < MaxBatchSize && lector.TryRead(out var entry))
                {
                    lote.Add(entry);
                }

                // Si aún queda encolado, escribir inmediatamente; si no, esperar un poco
                // para agrupar más entradas y minimizar I/O
                if (lote.Count < MaxBatchSize)
                {
                    try
                    {
                        using var timeoutCts = new CancellationTokenSource(BatchFlushMs);
                        if (await lector.WaitToReadAsync(timeoutCts.Token).ConfigureAwait(false))
                        {
                            while (lote.Count < MaxBatchSize && lector.TryRead(out var entry))
                            {
                                lote.Add(entry);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Timeout del batch: escribir lo acumulado
                    }
                }

                EscribirLote(lote);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AppLogger] Consumidor de logs terminó inesperadamente: {ex.Message}");
        }
    }

    private static void VaciarColaSincrono()
    {
        try
        {
            var lote = new List<LogEntry>(MaxBatchSize);
            while (_cola.Reader.TryRead(out var entry))
            {
                lote.Add(entry);
                if (lote.Count >= MaxBatchSize)
                {
                    EscribirLote(lote);
                    lote.Clear();
                }
            }
            if (lote.Count > 0) EscribirLote(lote);
        }
        catch
        {
            // En ProcessExit no hay nada más que hacer
        }
    }

    private static void EscribirLote(List<LogEntry> lote)
    {
        if (lote.Count == 0) return;

        try
        {
            if (!Directory.Exists(LogDirectory))
            {
                Directory.CreateDirectory(LogDirectory);
            }

            RotarSiEsNecesario();

            var sb = new System.Text.StringBuilder(lote.Count * 96);
            foreach (var entry in lote)
            {
                sb.Append(entry.ToString()).AppendLine();
            }
            File.AppendAllText(LogPath, sb.ToString());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AppLogger] Error al escribir log en disco: {ex.Message}");
        }
    }

    private static void RotarSiEsNecesario()
    {
        try
        {
            if (!File.Exists(LogPath)) return;

            var info = new FileInfo(LogPath);
            if (info.Length < MaxLogBytes) return;

            string rotado = LogPath + ".1";
            if (File.Exists(rotado))
            {
                File.Delete(rotado);
            }
            File.Move(LogPath, rotado);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AppLogger] No se pudo rotar el log: {ex.Message}");
        }
    }

    /// <summary>
    /// SEC-12: sustituye las rutas del perfil de usuario por marcadores cortos antes
    /// de escribir cualquier entrada de log (higiene de privacidad en disco).
    /// </summary>
    internal static string Sanitizar(string? texto)
    {
        if (string.IsNullOrEmpty(texto)) return texto ?? string.Empty;

        string resultado = texto;
        try
        {
            // Orden importante: %LocalAppData% vive DENTRO del perfil → reemplazarlo primero.
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localApp))
            {
                resultado = resultado.Replace(localApp, "<datos>", StringComparison.OrdinalIgnoreCase);
            }

            string perfil = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(perfil))
            {
                resultado = resultado.Replace(perfil, "<perfil>", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // Si el entorno no expone las rutas, se deja el texto tal cual.
        }

        return resultado;
    }
}
