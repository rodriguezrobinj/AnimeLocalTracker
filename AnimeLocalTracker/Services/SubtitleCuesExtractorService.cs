using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Core;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Extrae TODAS las líneas de una pista de subtítulos (incrustada en el video o en un archivo externo) para poder
/// resolver por nuestra cuenta qué mostrar en cada instante — a diferencia del texto único que expone Flyleaf
/// (<see cref="FlyleafLib.MediaPlayer.Subtitles.SubsText"/>), esto permite detectar cuándo dos líneas se solapan.
/// </summary>
public interface ISubtitleCuesExtractorService
{
    /// <summary>
    /// Pista incrustada: <paramref name="streamIndexEmbebido"/> es el índice de flujo dentro de <paramref name="rutaVideo"/>.
    /// Pista externa (archivo .srt/.ass aparte): pásese en <paramref name="rutaExterna"/> y <paramref name="streamIndexEmbebido"/> null.
    /// Lista vacía (nunca null) si falla la extracción o no hay nada que leer — el llamador debe seguir funcionando
    /// con el texto único de Flyleaf como respaldo en ese caso.
    /// </summary>
    Task<IReadOnlyList<SubtitleCue>> ExtraerAsync(string rutaVideo, int? streamIndexEmbebido, string? rutaExterna, CancellationToken ct);
}

/// <summary>
/// Usa el ffmpeg ya embebido en la app (mismo binario que ya usan las miniaturas/huella perceptual) para convertir
/// la pista elegida a SRT plano en un archivo temporal, la parsea y borra el temporal. Solo se guarda en memoria la
/// ÚLTIMA pista pedida (un episodio a la vez reproduciéndose): no hace falta más caché ni límite de tamaño.
/// </summary>
public sealed class SubtitleCuesExtractorService : ISubtitleCuesExtractorService
{
    private static readonly TimeSpan TiempoMaximo = TimeSpan.FromSeconds(20);

    private readonly object _cacheLock = new();
    private string? _claveCache;
    private IReadOnlyList<SubtitleCue> _cache = Array.Empty<SubtitleCue>();

    public async Task<IReadOnlyList<SubtitleCue>> ExtraerAsync(string rutaVideo, int? streamIndexEmbebido, string? rutaExterna, CancellationToken ct)
    {
        string clave = $"{rutaVideo}|{streamIndexEmbebido}|{rutaExterna}";
        lock (_cacheLock)
        {
            if (_claveCache == clave) return _cache;
        }

        var resultado = await ExtraerSinCacheAsync(rutaVideo, streamIndexEmbebido, rutaExterna, ct);

        lock (_cacheLock)
        {
            _claveCache = clave;
            _cache = resultado;
        }
        return resultado;
    }

    private static async Task<IReadOnlyList<SubtitleCue>> ExtraerSinCacheAsync(string rutaVideo, int? streamIndexEmbebido, string? rutaExterna, CancellationToken ct)
    {
        bool esExterna = !string.IsNullOrWhiteSpace(rutaExterna);
        string rutaEntrada = esExterna ? rutaExterna! : rutaVideo;

        if (string.IsNullOrWhiteSpace(rutaEntrada) || !File.Exists(rutaEntrada)) return Array.Empty<SubtitleCue>();
        if (!esExterna && streamIndexEmbebido is null) return Array.Empty<SubtitleCue>();

        string rutaTemporal = Path.Combine(Path.GetTempPath(), $"AnimeTracker_subs_{Guid.NewGuid():N}.srt");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = FfmpegLocator.Ffmpeg,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(rutaEntrada);
            if (!esExterna)
            {
                psi.ArgumentList.Add("-map");
                psi.ArgumentList.Add($"0:{streamIndexEmbebido}");
            }
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("srt");
            psi.ArgumentList.Add(rutaTemporal);

            using var proceso = Process.Start(psi);
            if (proceso == null) return Array.Empty<SubtitleCue>();

            using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limite.CancelAfter(TiempoMaximo);

            try
            {
                // Igual que en AudioDurationService: hay que drenar ambos flujos, si no un pipe sin lector cuelga ffmpeg.
                var salida = proceso.StandardOutput.ReadToEndAsync(limite.Token);
                var error = proceso.StandardError.ReadToEndAsync(limite.Token);
                await proceso.WaitForExitAsync(limite.Token);
                await Task.WhenAll(salida, error);

                if (proceso.ExitCode != 0 || !File.Exists(rutaTemporal)) return Array.Empty<SubtitleCue>();
            }
            catch (OperationCanceledException)
            {
                try { if (!proceso.HasExited) proceso.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                throw;
            }

            string contenido = await File.ReadAllTextAsync(rutaTemporal, ct);
            return SubtitulosSrtParser.Parsear(contenido);
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<SubtitleCue>();
        }
        catch (Exception ex)
        {
            AppLogger.Debug("SubtitleCuesExtractorService", $"No se pudieron extraer los subtítulos de '{rutaEntrada}': {ex.Message}");
            return Array.Empty<SubtitleCue>();
        }
        finally
        {
            try { if (File.Exists(rutaTemporal)) File.Delete(rutaTemporal); } catch { /* best-effort */ }
        }
    }
}
