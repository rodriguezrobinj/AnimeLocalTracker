using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Extraído de ReproductorViewModel: captura un frame del video actual a un archivo de imagen
/// vía el ffmpeg embebido de la app. Sin estado compartido con el reproductor — recibe todo lo
/// que necesita por parámetro, por lo que no requiere registro en el contenedor DI.
/// </summary>
public static class FrameCaptureService
{
    /// <summary>MensajeUsuario ya viene formateado listo para mostrar en el diálogo de error; null si no debe mostrarse ninguno (falla silenciosa, como antes).</summary>
    public sealed record Resultado(bool Exito, string? MensajeUsuario);

    /// <summary>
    /// CAP-01: timeout de 30 s — un ffmpeg colgado (ruta de red, codec raro, AV) no puede
    /// dejar procesos huérfanos acumulándose en segundo plano.
    /// </summary>
    public static async Task<Resultado> CapturarFrameAsync(
        string rutaVideo,
        double posicionSegundos,
        string rutaDestino,
        CancellationToken ct = default)
    {
        try
        {
            // Buscar el ffmpeg embebido de la app (ya está en el PATH del proceso)
            string ffmpeg = "ffmpeg";
            string embebido = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FFmpeg", "ffmpeg.exe");
            if (File.Exists(embebido)) ffmpeg = embebido;

            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-nostdin");
            // Sec: acota la asignación de memoria por bloque en ffmpeg (2 GB)
            psi.ArgumentList.Add("-max_alloc"); psi.ArgumentList.Add("2147483648");
            psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-ss"); psi.ArgumentList.Add(posicionSegundos.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(rutaVideo);
            psi.ArgumentList.Add("-frames:v"); psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-q:v"); psi.ArgumentList.Add("2");
            psi.ArgumentList.Add(rutaDestino);

            using var proceso = Process.Start(psi)!;
            // No se propaga ct aquí a propósito: el timeout/cancelación se maneja abajo matando
            // el proceso (lo que a su vez completa este ReadToEndAsync al cerrarse stderr).
            string stderr = await proceso.StandardError.ReadToEndAsync(CancellationToken.None);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await proceso.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { proceso.Kill(entireProcessTree: true); } catch { }
                AppLogger.Warn("FrameCaptureService", "Captura de frame cancelada por timeout (30 s)");
                return new Resultado(false, "La captura del frame tardó demasiado (30 s). Inténtalo de nuevo.");
            }

            if (proceso.ExitCode == 0 && File.Exists(rutaDestino))
            {
                return new Resultado(true, null);
            }

            // CAP-02: exponer un extracto del stderr real para que el usuario pueda diagnosticar
            string detalle = string.IsNullOrWhiteSpace(stderr) ? "" : $"\n\n{stderr.Trim()}";
            if (detalle.Length > 320) detalle = detalle[..320] + "…";
            AppLogger.Debug("FrameCaptureService", $"ffmpeg falló al capturar frame: {stderr}");
            return new Resultado(false, $"No se pudo capturar el frame.{detalle}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Debug("FrameCaptureService", $"Error capturando frame: {ex.Message}");
            return new Resultado(false, null);
        }
    }
}
