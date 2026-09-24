using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using FlyleafLib.MediaPlayer;

namespace AnimeLocalTracker.Services;

public class FrameCaptureService : IFrameCaptureService
{
    /// <summary>
    /// Captura el fotograma que Flyleaf tiene actualmente renderizado directo desde su buffer
    /// Direct3D (Player.TakeSnapshotToBitmapSource) — sin relanzar ffmpeg ni re-decodificar, y en
    /// la resolución nativa del video. Si los subtítulos están activos ya quedan incluidos porque
    /// Flyleaf los compone dentro del propio frame renderizado (no son un overlay WPF aparte).
    /// </summary>
    public async Task<string?> CapturarYGuardarAsync(Player? player)
    {
        if (player == null) return null;

        try
        {
            var bitmap = player.TakeSnapshotToBitmapSource(0, 0);
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
