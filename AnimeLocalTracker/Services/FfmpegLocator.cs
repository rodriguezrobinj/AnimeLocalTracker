using System;
using System.IO;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Dónde están ffmpeg/ffprobe: los embebidos (carpeta FFmpeg/ del output) y, si faltan, los del PATH. El mismo criterio
/// que ya usan VideoIntegrityService y AnimeThemesDownloadService, en un solo sitio para lo nuevo.
/// </summary>
internal static class FfmpegLocator
{
    private static readonly Lazy<string> RutaFfmpeg = new(() => Resolver("ffmpeg"));
    private static readonly Lazy<string> RutaFfprobe = new(() => Resolver("ffprobe"));

    public static string Ffmpeg => RutaFfmpeg.Value;
    public static string Ffprobe => RutaFfprobe.Value;

    private static string Resolver(string nombre)
    {
        string embebido = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FFmpeg", nombre + ".exe");
        return File.Exists(embebido) ? embebido : nombre;
    }
}
