using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Services;

public class VideoIntegrityService : IVideoIntegrityService
{
    // El ffprobe embebido (carpeta FFmpeg/ del output); el del sistema solo en desarrollo: ver FfmpegLocator.
    private static readonly Lazy<string> RutaFfprobe = new(() => FfmpegLocator.Ffprobe);

    public async Task<ResultadoIntegridad> VerificarArchivoAsync(string rutaArchivo, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rutaArchivo) || !File.Exists(rutaArchivo))
        {
            return ResultadoIntegridad.NoSePudoVerificar;
        }

        try
        {
            string[] argumentos = ["-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", rutaArchivo];
            var resultado = await Core.ProcesoExterno.EjecutarAsync(RutaFfprobe.Value, argumentos, null, ct);
            if (resultado == null) return ResultadoIntegridad.NoSePudoVerificar;

            string salidaError = resultado.Error;
            string salida = resultado.Salida;

            // El código de salida es la señal real de "no se pudo leer el contenedor" (probado:
            // un mp4 con moov atom faltante da exit=1). El contenido de stderr NO sirve para
            // decidir esto — con "-v error" ffprobe igual imprime advertencias cosméticas en
            // archivos sanos (ej. "Referenced QT chapter track not found" en remuxes con
            // metadata de capítulos incompleta) que en un principio se trataban como corrupción
            // y marcaban episodios perfectamente reproducibles como rotos.
            if (!string.IsNullOrWhiteSpace(salidaError))
            {
                AppLogger.Debug("VideoIntegrityService", $"ffprobe stderr (no decisivo) para '{rutaArchivo}': {salidaError.Trim()}");
            }

            if (resultado.Codigo != 0)
            {
                return ResultadoIntegridad.Corrupto;
            }

            if (!double.TryParse(salida.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double duracion) || duracion <= 0)
            {
                return ResultadoIntegridad.Corrupto;
            }

            return ResultadoIntegridad.Ok;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Debug("VideoIntegrityService", $"No se pudo invocar ffprobe para '{rutaArchivo}': {ex.Message}");
            return ResultadoIntegridad.NoSePudoVerificar;
        }
    }
}
