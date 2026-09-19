using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Services;

public class VideoIntegrityService : IVideoIntegrityService
{
    // Preferir el ffprobe embebido (carpeta FFmpeg/ del output); si no está, caer al del
    // PATH del sistema con el mismo criterio de visibilidad que NativeMethods.AsegurarFfmpegEnPath.
    private static readonly Lazy<string> RutaFfprobe = new(() =>
    {
        string embebido = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FFmpeg", "ffprobe.exe");
        return File.Exists(embebido) ? embebido : "ffprobe";
    });

    public async Task<ResultadoIntegridad> VerificarArchivoAsync(string rutaArchivo, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rutaArchivo) || !File.Exists(rutaArchivo))
        {
            return ResultadoIntegridad.NoSePudoVerificar;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = RutaFfprobe.Value,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-show_entries");
            psi.ArgumentList.Add("format=duration");
            psi.ArgumentList.Add("-of");
            psi.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
            psi.ArgumentList.Add(rutaArchivo);

            using var proceso = Process.Start(psi);
            if (proceso == null) return ResultadoIntegridad.NoSePudoVerificar;

            Task<string> tareaError = proceso.StandardError.ReadToEndAsync(ct);
            Task<string> tareaSalida = proceso.StandardOutput.ReadToEndAsync(ct);
            await proceso.WaitForExitAsync(ct);
            string salidaError = await tareaError;
            string salida = await tareaSalida;

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

            if (proceso.ExitCode != 0)
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
