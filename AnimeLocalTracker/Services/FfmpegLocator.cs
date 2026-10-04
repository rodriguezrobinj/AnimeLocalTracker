using System;
using System.IO;
using AnimeLocalTracker.Services.Python;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Dónde están ffmpeg/ffprobe: los embebidos (carpeta FFmpeg/ del output). El mismo criterio que ya usan
/// VideoIntegrityService y AnimeThemesDownloadService, en un solo sitio para lo nuevo.
/// Solo en desarrollo, si faltan, se usan los del sistema. En un build publicado nunca: pedirle a Windows "ffmpeg" a secas
/// lo busca también en la carpeta actual y en el PATH, y la app acabaría ejecutando el primero que encontrara. Si el
/// embebido falta (un antivirus lo quitó), es mejor que la función falle y se vea en el registro.
/// </summary>
internal static class FfmpegLocator
{
    private static readonly Lazy<string> RutaFfmpeg = new(() => Resolver("ffmpeg", AppDomain.CurrentDomain.BaseDirectory, LocalizadorHerramientasPython.CompilacionDeDesarrollo));
    private static readonly Lazy<string> RutaFfprobe = new(() => Resolver("ffprobe", AppDomain.CurrentDomain.BaseDirectory, LocalizadorHerramientasPython.CompilacionDeDesarrollo));

    public static string Ffmpeg => RutaFfmpeg.Value;
    public static string Ffprobe => RutaFfprobe.Value;

    internal static string Resolver(string nombre, string baseDir, bool desarrollo)
    {
        string embebido = Path.Combine(baseDir, "FFmpeg", nombre + ".exe");
        return File.Exists(embebido) || !desarrollo ? embebido : nombre;
    }
}
