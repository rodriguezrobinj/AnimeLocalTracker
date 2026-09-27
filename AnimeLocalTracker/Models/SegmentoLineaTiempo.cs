using System;
using System.Collections.Generic;
using System.Linq;

namespace AnimeLocalTracker.Models;

public enum TipoSegmentoLineaTiempo
{
    Opening,
    Ending,
    Resumen
}

/// <summary>Un tramo marcado en la barra de progreso del reproductor (segundos desde el inicio del episodio).</summary>
public sealed record SegmentoLineaTiempo(TipoSegmentoLineaTiempo Tipo, double Inicio, double Fin)
{
    public double Duracion => Fin - Inicio;

    /// <summary>
    /// Convierte los tramos de saltos (AniSkip / audio de referencia) en marcadores de la barra:
    /// - descarta los inválidos (fin ≤ inicio, negativos) y recorta al largo del episodio si se conoce;
    /// - los "mixed" (opening/ending mezclado con la escena) solo se dibujan si no hay un opening/ending normal del mismo tipo,
    ///   para no apilar dos marcas casi iguales;
    /// - se ordenan por inicio.
    /// </summary>
    public static List<SegmentoLineaTiempo> Crear(IEnumerable<AniSkipResult>? tramos, double duracionEpisodio)
    {
        var resultado = new List<SegmentoLineaTiempo>();
        if (tramos == null) return resultado;

        var validos = tramos
            .Where(t => t != null && t.Interval != null && t.Interval.EndTime > t.Interval.StartTime && t.Interval.EndTime > 0)
            .ToList();

        bool hayOpeningNormal = validos.Any(t => t.SkipType == "op");
        bool hayEndingNormal = validos.Any(t => t.SkipType == "ed");

        foreach (var t in validos)
        {
            TipoSegmentoLineaTiempo? tipo = t.SkipType switch
            {
                "op" => TipoSegmentoLineaTiempo.Opening,
                "mixed-op" when !hayOpeningNormal => TipoSegmentoLineaTiempo.Opening,
                "ed" => TipoSegmentoLineaTiempo.Ending,
                "mixed-ed" when !hayEndingNormal => TipoSegmentoLineaTiempo.Ending,
                "recap" => TipoSegmentoLineaTiempo.Resumen,
                _ => null
            };
            if (tipo == null) continue;

            double inicio = Math.Max(0, t.Interval.StartTime);
            double fin = t.Interval.EndTime;
            if (duracionEpisodio > 0)
            {
                if (inicio >= duracionEpisodio) continue;
                fin = Math.Min(fin, duracionEpisodio);
            }
            if (fin <= inicio) continue;

            resultado.Add(new SegmentoLineaTiempo(tipo.Value, inicio, fin));
        }

        return resultado.OrderBy(s => s.Inicio).ToList();
    }
}
