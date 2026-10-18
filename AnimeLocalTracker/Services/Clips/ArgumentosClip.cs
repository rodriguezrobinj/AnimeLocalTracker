using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace AnimeLocalTracker.Services.Clips;

/// <summary>
/// La línea de ffmpeg de un clip y las reglas del tramo. El MP4 siempre cabe en Discord sin Nitro (10 MB): la tasa de bits se
/// calcula por duración. El GIF lleva su paleta en dos pasadas (palettegen + paletteuse) dentro de un solo filtro.
/// </summary>
internal static class ArgumentosClip
{
    public const double MaxMp4Seg = 30;
    public const double MaxGifSeg = 10;
    public const double MinSeg = 1;

    /// <summary>9,5 MB de presupuesto (no 10: el contenedor también ocupa) en kilobits.</summary>
    private const double PresupuestoKbits = 9.5 * 1024 * 1024 * 8 / 1000;
    private const int AudioKbps = 128;
    private const int TopeVideoKbps = 5000; // 720p no gana nada por encima

    private const string Escala720 = "scale=-2:'min(720,ih)'";
    private const string FiltroGif = "fps=15,scale=480:-1:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];"
                                     + "[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle";

    public static int TasaVideoKbps(double duracionSeg)
        => (int)Math.Min(TopeVideoKbps, Math.Floor(PresupuestoKbits / duracionSeg) - AudioKbps);

    public static IReadOnlyList<string> Construir(SolicitudClip s)
    {
        // -ss y -t delante de -i: limitan la LECTURA del episodio. Con -t de salida, el GIF (palettegen entrega la paleta al acabar
        // su entrada) leía el resto del episodio antes de escribir nada.
        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-ss", Num(s.InicioSeg), "-t", Num(s.DuracionSeg), "-i", s.RutaVideo };
        string subtitulos = FiltroSubtitulos(s);

        if (s.Formato == FormatoClip.Gif)
        {
            args.AddRange(new[] { "-filter_complex", "[0:v]" + subtitulos + FiltroGif, "-an", "-loop", "0" });
        }
        else
        {
            int kbps = TasaVideoKbps(s.DuracionSeg);
            args.AddRange(new[]
            {
                "-map", "0:v:0", "-map", s.IndiceAudio >= 0 ? $"0:{s.IndiceAudio}" : "0:a:0?",
                "-vf", subtitulos + Escala720,
                "-c:v", "libx264", "-preset", "veryfast", "-b:v", $"{kbps}k", "-maxrate", $"{kbps}k", "-bufsize", $"{kbps * 2}k",
                "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", $"{AudioKbps}k", "-sn", "-movflags", "+faststart",
            });
        }

        args.Add(s.RutaSalida);
        return args;
    }

    /// <summary>
    /// Con <c>-ss</c> antes de <c>-i</c> el video empieza en 0: se le devuelve su tiempo real para que el filtro de subtítulos
    /// pinte cada línea en su momento, y se vuelve a 0 después. Termina en coma (va delante del resto de la cadena).
    /// </summary>
    private static string FiltroSubtitulos(SolicitudClip s)
    {
        if (s.Subtitulos is not { } subs) return string.Empty;
        string pista = subs.IndiceRelativo is int n ? $":si={n}" : string.Empty;
        return $"setpts=PTS+{Num(s.InicioSeg)}/TB,subtitles={EscaparRutaFiltro(subs.Archivo)}{pista},setpts=PTS-STARTPTS,";
    }

    /// <summary>
    /// La ruta va dentro de un filtro, que ffmpeg lee en dos niveles: el valor de la opción (donde <c>\ ' :</c> son especiales) y
    /// la cadena de filtros (donde lo son <c>[ ] , ; ' \</c>). Los nombres de fansub ("[SubsPlease] … [AB'C].mkv") tienen de todo.
    /// </summary>
    public static string EscaparRutaFiltro(string ruta)
        => Escapar(Escapar(ruta.Replace('\\', '/'), @"\':"), @"[],;'\");

    private static string Escapar(string texto, string especiales)
    {
        var sb = new StringBuilder(texto.Length + 8);
        foreach (char c in texto)
        {
            if (especiales.Contains(c)) sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Ordena las marcas y aplica los límites del formato; null si el tramo no sirve para ese formato.</summary>
    public static RangoClip? RangoValido(double marcaA, double marcaB, FormatoClip formato)
    {
        double inicio = Math.Min(marcaA, marcaB);
        double duracion = Math.Abs(marcaB - marcaA);
        if (duracion < MinSeg) return null;

        if (formato == FormatoClip.Gif) return duracion <= MaxGifSeg ? new RangoClip(inicio, duracion, false) : null;
        return duracion > MaxMp4Seg ? new RangoClip(inicio, MaxMp4Seg, true) : new RangoClip(inicio, duracion, false);
    }

    /// <summary>
    /// El motor da el índice del flujo dentro del archivo; el filtro <c>subtitles</c> cuenta solo pistas de subtítulos internas.
    /// Una pista externa no lleva índice (el filtro lee su propio archivo).
    /// </summary>
    public static int? IndiceRelativoSubtitulos(IReadOnlyList<PistaSubtitulosMotor> pistas, PistaSubtitulosMotor pista)
        => pista.RutaExterna != null ? null : pistas.Count(p => p.RutaExterna == null && p.Indice < pista.Indice);

    public static string NombreArchivo(string titulo, int episodio, double inicioSeg, FormatoClip formato)
    {
        var invalidos = Path.GetInvalidFileNameChars();
        string limpio = new string(titulo.Select(c => invalidos.Contains(c) ? '_' : c).ToArray()).Trim();
        if (limpio.Length > 80) limpio = limpio[..80].TrimEnd();

        var t = TimeSpan.FromSeconds(Math.Floor(inicioSeg));
        string momento = t.TotalHours >= 1 ? $"{(int)t.TotalHours}-{t:mm\\-ss}" : $"{t:mm\\-ss}";
        return $"{limpio} - Ep {episodio} - {momento}.{(formato == FormatoClip.Gif ? "gif" : "mp4")}";
    }

    private static string Num(double valor) => valor.ToString("0.###", CultureInfo.InvariantCulture);
}
