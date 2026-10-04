using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Services;

/// <summary>Duración de un archivo de audio local, para mostrarla antes de reproducirlo.</summary>
public interface IAudioDurationService
{
    /// <summary>Duración del archivo, o null si no existe o no se pudo leer. Nunca lanza.</summary>
    Task<TimeSpan?> ObtenerDuracionAsync(string ruta, CancellationToken ct);
}

/// <summary>
/// Lee la duración con ffprobe (ya embebido en la app) sin decodificar el audio: unos 50 ms por archivo. Recuerda el
/// resultado por archivo mientras no cambie (fecha y tamaño), y limita a 2 los ffprobe simultáneos para no lanzar
/// decenas de procesos al abrir una ficha con muchos temas.
/// </summary>
public sealed class AudioDurationService : IAudioDurationService, IDisposable
{
    private static readonly TimeSpan TiempoMaximo = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DuracionMaximaCreible = TimeSpan.FromHours(24);

    private readonly Func<string, CancellationToken, Task<string?>> _ejecutor;
    private readonly ConcurrentDictionary<string, (long Modificado, long Tamano, TimeSpan Duracion)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _limite = new(2, 2);

    public AudioDurationService() : this(EjecutarFfprobeAsync)
    {
    }

    /// <param name="ejecutor">Solo para pruebas: devuelve lo que imprimiría ffprobe para el archivo (null si falla).</param>
    internal AudioDurationService(Func<string, CancellationToken, Task<string?>> ejecutor)
    {
        _ejecutor = ejecutor;
    }

    public async Task<TimeSpan?> ObtenerDuracionAsync(string ruta, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ruta)) return null;

        try
        {
            var info = new FileInfo(ruta);
            if (!info.Exists) return null;

            long modificado = info.LastWriteTimeUtc.Ticks;
            if (_cache.TryGetValue(ruta, out var guardado) && guardado.Modificado == modificado && guardado.Tamano == info.Length)
                return guardado.Duracion;

            await _limite.WaitAsync(ct);
            try
            {
                string? salida = await _ejecutor(ruta, ct);
                var duracion = Parsear(salida);
                if (duracion == null) return null;

                _cache[ruta] = (modificado, info.Length, duracion.Value);
                return duracion;
            }
            finally
            {
                _limite.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            AppLogger.Debug("AudioDurationService", $"No se pudo leer la duración de '{ruta}': {ex.Message}");
            return null;
        }
    }

    /// <summary>Interpreta la salida de <c>ffprobe -show_entries format=duration -of csv=p=0</c> ("92.136000"). Null si no es creíble.</summary>
    internal static TimeSpan? Parsear(string? salida)
    {
        if (string.IsNullOrWhiteSpace(salida)) return null;

        foreach (string linea in salida.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!double.TryParse(linea, NumberStyles.Float, CultureInfo.InvariantCulture, out double segundos)) continue;
            if (!double.IsFinite(segundos) || segundos <= 0) continue;

            var duracion = TimeSpan.FromSeconds(segundos);
            return duracion <= DuracionMaximaCreible ? duracion : null;
        }
        return null;
    }

    private static async Task<string?> EjecutarFfprobeAsync(string ruta, CancellationToken ct)
    {
        string[] argumentos = ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", ruta];
        var resultado = await Core.ProcesoExterno.EjecutarAsync(FfmpegLocator.Ffprobe, argumentos, TiempoMaximo, ct);
        return resultado is { Codigo: 0 } ? resultado.Salida : null;
    }

    public void Dispose() => _limite.Dispose();
}
