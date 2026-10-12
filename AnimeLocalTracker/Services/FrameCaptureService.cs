using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace AnimeLocalTracker.Services;

public class FrameCaptureService : IFrameCaptureService
{
    /// <summary>
    /// Captura el fotograma que el motor tiene en pantalla (<see cref="IMotorVideo.CapturarFotograma"/>), sin relanzar
    /// ffmpeg ni volver a decodificar, y en la resolución del video. Si los subtítulos están activos quedan incluidos:
    /// el motor los dibuja dentro del propio video (no son una capa de WPF aparte).
    /// </summary>
    public async Task<string?> CapturarYGuardarAsync(IMotorVideo? motor)
    {
        if (motor == null) return null;

        try
        {
            var bitmap = motor.CapturarFotograma();
            if (bitmap == null) return null;
            bitmap.Freeze();

            Clipboard.SetImage(bitmap);

            string carpeta = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "AnimeLocalTracker");
            string ruta = Path.Combine(carpeta, $"Captura_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            await Task.Run(() =>
            {
                Directory.CreateDirectory(carpeta);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var fs = File.Create(ruta);
                encoder.Save(fs);
            });

            return ruta;
        }
        catch (Exception ex)
        {
            AppLogger.Error("FrameCaptureService", "Error capturando fotograma", ex);
            return null;
        }
    }
}
