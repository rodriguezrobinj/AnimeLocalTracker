using System;
using System.Collections.Generic;

namespace AnimeLocalTracker.Core;

/// <summary>
/// Decisiones puras (sin Flyleaf ni WPF) del dibujo de subtítulos ASS/SSA con su estilo original.
/// Ver docs/investigacion-subtitulos-ass.md.
/// </summary>
public static class SubtitulosAss
{
    /// <summary>Solo las pistas ASS/SSA de texto incrustadas se dibujan con su estilo, y solo si el usuario no pidió el suyo.</summary>
    public static bool DebeDibujarse(bool esAss, bool esImagen, bool esExterna, bool usarMiEstilo)
        => esAss && !esImagen && !esExterna && !usarMiEstilo;

    /// <summary>
    /// El filtro de ffmpeg numera la pista entre las de subtítulos (0 = la primera), no con el índice del contenedor que da
    /// Flyleaf. Devuelve -1 si la pista no está en la lista.
    /// </summary>
    public static int IndiceEntreSubtitulos(IEnumerable<int> indicesDePistasDeSubtitulos, int indiceEnContenedor)
    {
        int anteriores = 0;
        bool esta = false;
        foreach (int indice in indicesDePistasDeSubtitulos)
        {
            if (indice == indiceEnContenedor) esta = true;
            else if (indice < indiceEnContenedor) anteriores++;
        }
        return esta ? anteriores : -1;
    }

    /// <summary>Tamaño al que se dibuja: el del video, reducido sin deformar si no cabe en la pantalla. (0, 0) si no se conoce.</summary>
    public static (int Ancho, int Alto) TamanoDibujo(int anchoVideo, int altoVideo, int anchoPantalla, int altoPantalla)
    {
        if (anchoVideo <= 0 || altoVideo <= 0 || anchoPantalla <= 0 || altoPantalla <= 0) return (0, 0);

        double escala = Math.Min(1.0, Math.Min((double)anchoPantalla / anchoVideo, (double)altoPantalla / altoVideo));
        return (Par(anchoVideo * escala), Par(altoVideo * escala));

        static int Par(double valor) => Math.Max(2, (int)Math.Round(valor) & ~1);
    }

    /// <summary>
    /// El filtro de ffmpeg pinta los colores con el rango que declara el guion ("YCbCr Matrix"), no con el del fotograma: sobre
    /// RGB eso los deja en 16–235 (blanco apagado) salvo que el guion diga "None" o "PC.*". True = hay que estirarlos a 0–255.
    /// Misma regla que <c>ass_get_color_range</c> de vf_subtitles.c: ausente o desconocida cuenta como TV.
    /// </summary>
    public static bool RangoComprimido(string? cabeceraDelGuion)
    {
        if (string.IsNullOrEmpty(cabeceraDelGuion)) return true;

        const string Clave = "YCbCr Matrix:";
        foreach (string linea in cabeceraDelGuion.Split('\n'))
        {
            string limpia = linea.Trim();
            if (!limpia.StartsWith(Clave, StringComparison.OrdinalIgnoreCase)) continue;

            string valor = limpia[Clave.Length..].Trim();
            return !(valor.Equals("None", StringComparison.OrdinalIgnoreCase) || valor.StartsWith("PC.", StringComparison.OrdinalIgnoreCase));
        }
        return true;
    }
}
