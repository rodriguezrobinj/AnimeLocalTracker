using System;
using System.Collections.Generic;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Horas realmente vistas. Los episodios marcados como vistos a mano o importados en bloque (p. ej.
/// desde AniList) no guardan su duración, y ignorarlos dejaba las horas ridículamente bajas: con
/// 1500 episodios vistos salían ~7 h. Para esos se estima la duración con el promedio de tus
/// episodios que sí la tienen (o 24 min si no hay ninguno). Lo usan Estadísticas y Logros, para
/// que ambos muestren la misma cifra.
/// </summary>
public static class EstimadorDuracion
{
    public const double DuracionPorDefectoSegundos = 24 * 60;

    /// <summary>Segundos totales de los episodios vistos (reales + estimados).</summary>
    public static double SegundosVistos(IReadOnlyCollection<RegistroEpisodio> vistos)
    {
        var medidor = CrearMedidor(vistos);
        double total = 0;
        foreach (var registro in vistos) total += medidor(registro);
        return total;
    }

    /// <summary>
    /// Devuelve una función que da los segundos de UN episodio visto: su duración real si se conoce, o el
    /// promedio global de <paramref name="vistos"/> (o 24 min si no hay ninguna) si no. Usar el promedio de
    /// TODA la biblioteca —no el de cada grupo— mantiene coherentes los totales por franquicia con el total general.
    /// </summary>
    public static Func<RegistroEpisodio, double> CrearMedidor(IReadOnlyCollection<RegistroEpisodio> vistos)
    {
        double sumaDuraciones = 0;
        int conDuracion = 0;

        foreach (var registro in vistos)
        {
            // El promedio solo usa duraciones completas de episodio, no progresos parciales.
            if (registro.TotalSegundos > 0)
            {
                sumaDuraciones += registro.TotalSegundos;
                conDuracion++;
            }
        }

        double promedio = conDuracion > 0 ? sumaDuraciones / conDuracion : DuracionPorDefectoSegundos;

        return registro =>
        {
            double segundos = Math.Max(registro.TotalSegundos, registro.ProgresoSegundos);
            return segundos > 0 ? segundos : promedio;
        };
    }
}
