using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AnimeLocalTracker.Core;

/// <summary>Un ajuste predefinido del ecualizador (ganancias en dB de cada banda).</summary>
public sealed record PresetEcualizador(string Clave, IReadOnlyList<double> Ganancias);

/// <summary>Un filtro de audio de FFmpeg tal como lo recibe Flyleaf (Id = nombre de la instancia, para cambiarlo en vivo).</summary>
public sealed record FiltroAudio(string Id, string Nombre, string Argumentos);

/// <summary>
/// Ecualizador gráfico de 10 bandas (una por octava, de 31 Hz a 16 kHz) sobre los filtros de audio de FFmpeg que ya usa el reproductor
/// para el Modo Noche. Cada banda es un filtro "equalizer" con su propio Id: al mover un deslizador solo se le manda la ganancia nueva
/// (sin reconstruir la cadena ni cortar el audio). Un filtro "volume" previo baja el volumen lo que suba la banda más alta, para que al
/// realzar no sature ni distorsione.
/// </summary>
public static class EcualizadorAudio
{
    public const double GananciaMaxima = 12;

    /// <summary>Frecuencias centrales (Hz) de las 10 bandas.</summary>
    public static readonly IReadOnlyList<int> Frecuencias = new[] { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };

    public const string PresetPlano = "Plano";
    public const string PresetPersonalizado = "Personalizado";

    /// <summary>Ajustes predefinidos pensados para anime: diálogo claro, graves de las escenas de acción, brillo de los openings…</summary>
    public static readonly IReadOnlyList<PresetEcualizador> Presets = new[]
    {
        new PresetEcualizador(PresetPlano,        new double[] {  0,  0,  0, 0,  0, 0, 0, 0, 0, 0 }),
        new PresetEcualizador("VocesClaras",      new double[] { -3, -2, -1, 0,  2, 4, 4, 3, 1, 0 }),
        new PresetEcualizador("GravesPotentes",   new double[] {  6,  5,  4, 2,  0, 0, 0, 0, 0, 0 }),
        new PresetEcualizador("AgudosBrillantes", new double[] {  0,  0,  0, 0,  0, 1, 2, 4, 5, 5 }),
        new PresetEcualizador("Cine",             new double[] {  5,  4,  2, 0, -1, 0, 1, 3, 4, 4 }),
        new PresetEcualizador("Musica",           new double[] {  3,  2,  1, 0, -1, -1, 0, 2, 3, 3 }),
    };

    public static string IdBanda(int indice) => $"eq_b{indice}";
    public const string IdPreamplificador = "eq_pre";
    public const string IdModoNoche = "modonoche";

    /// <summary>Ganancias válidas: 10 valores entre -12 y +12 dB (lo que falte o sobre en un archivo de ajustes viejo se corrige).</summary>
    public static double[] Normalizar(IEnumerable<double>? ganancias)
    {
        var lista = (ganancias ?? Array.Empty<double>()).Take(Frecuencias.Count).ToList();
        while (lista.Count < Frecuencias.Count) lista.Add(0);
        return lista.Select(g => double.IsFinite(g) ? Math.Round(Math.Clamp(g, -GananciaMaxima, GananciaMaxima), 1) : 0).ToArray();
    }

    /// <summary>El preset cuyas ganancias coinciden exactamente, o Personalizado.</summary>
    public static string PresetDe(IReadOnlyList<double> ganancias)
    {
        var normalizadas = Normalizar(ganancias);
        return Presets.FirstOrDefault(p => p.Ganancias.SequenceEqual(normalizadas))?.Clave ?? PresetPersonalizado;
    }

    /// <summary>Reducción previa (dB) para que la banda más realzada no sature: tanto como suba la más alta.</summary>
    public static double Preamplificacion(IReadOnlyList<double> ganancias) => -Math.Max(0, ganancias.DefaultIfEmpty(0).Max());

    public static string TextoGanancia(double db) => db.ToString("0.0", CultureInfo.InvariantCulture);

    public static string TextoVolumen(double db) => $"{TextoGanancia(db)}dB";

    /// <summary>
    /// Cadena de filtros de audio del reproductor: ecualizador (si está activo) y, detrás, el compresor del Modo Noche (si está activo),
    /// que así también iguala lo que el ecualizador realce.
    /// </summary>
    public static List<FiltroAudio> ConstruirFiltros(bool ecualizadorActivo, IReadOnlyList<double> ganancias, bool modoNoche, string argumentosModoNoche)
    {
        var filtros = new List<FiltroAudio>();
        if (ecualizadorActivo)
        {
            var g = Normalizar(ganancias);
            filtros.Add(new FiltroAudio(IdPreamplificador, "volume", $"volume={TextoVolumen(Preamplificacion(g))}"));
            for (int i = 0; i < Frecuencias.Count; i++)
                filtros.Add(new FiltroAudio(IdBanda(i), "equalizer", $"f={Frecuencias[i]}:t=o:w=1:g={TextoGanancia(g[i])}"));
        }
        if (modoNoche) filtros.Add(new FiltroAudio(IdModoNoche, "acompressor", argumentosModoNoche));
        return filtros;
    }

    public static string EtiquetaFrecuencia(int hz) => hz >= 1000 ? $"{hz / 1000}k" : hz.ToString(CultureInfo.InvariantCulture);
}
