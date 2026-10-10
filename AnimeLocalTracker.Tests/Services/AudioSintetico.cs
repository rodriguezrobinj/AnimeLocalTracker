using System;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// Huellas sintéticas para probar el análisis de audio sin ffmpeg. El generador es el mismo que usan las pruebas del núcleo
/// Rust (native/animetracker_core/src/audio.rs): con él, un tema "incrustado" en un episodio se encuentra donde se puso.
/// </summary>
internal static class AudioSintetico
{
    internal const int Fps = 10;
    internal const int Columnas = 8;

    /// <summary>Fotogramas × 8 valores en [-0,5, 0,5), por filas.</summary>
    internal static float[] Ruido(int fotogramas, uint semilla)
    {
        var valores = new float[fotogramas * Columnas];
        uint estado = semilla;
        for (int i = 0; i < valores.Length; i++)
        {
            estado = unchecked(estado * 1664525u + 1013904223u);
            valores[i] = (float)(estado / 4294967296.0 - 0.5);
        }
        return valores;
    }

    internal static float[] RuidoSegundos(double segundos, uint semilla) => Ruido((int)(segundos * Fps), semilla);

    /// <summary>Copia <paramref name="tema"/> (desde el fotograma <paramref name="desde"/>) en el episodio, con algo de ruido encima.</summary>
    internal static float[] Incrustar(float[] episodio, float[] tema, int fotograma, int desde = 0)
    {
        var resultado = (float[])episodio.Clone();
        var parte = tema.AsSpan(desde * Columnas);
        var extra = Ruido(parte.Length / Columnas, 99);
        for (int i = 0; i < parte.Length; i++)
        {
            resultado[fotograma * Columnas + i] = (float)(parte[i] + 0.1 * extra[i]);
        }
        return resultado;
    }

    internal static float[] IncrustarEnSegundo(float[] episodio, float[] tema, double segundo, int desde = 0) =>
        Incrustar(episodio, tema, (int)(segundo * Fps), desde);
}
