using System.Collections.Generic;
using System.Linq;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Core;

/// <summary>
/// ARQ-01 (paso 1 del split de DetalleViewModel): lógica pura de filtrado y
/// ordenamiento de episodios, extraída del god-object para ser testeable sin UI.
/// </summary>
public static class EpisodiosOrganizador
{
    /// <summary>
    /// Aplica el filtro textual de episodios ("Todos", "Descargados", "Vistos",
    /// "No Vistos", "Favoritos") y el orden (ascendente/descendente por número).
    /// </summary>
    public static List<EpisodioItem> FiltrarYOrdenar(
        IEnumerable<EpisodioItem> episodios, string filtro, bool ordenAscendente)
    {
        var query = episodios.AsEnumerable();

        switch (filtro)
        {
            case "Descargados":
                query = query.Where(e => e.Descargado);
                break;
            case "Vistos":
                query = query.Where(e => e.Visto);
                break;
            case "No Vistos":
                query = query.Where(e => !e.Visto);
                break;
            case "Favoritos":
                query = query.Where(e => e.Favorito);
                break;
        }

        query = ordenAscendente
            ? query.OrderBy(e => e.NumeroEpisodio)
            : query.OrderByDescending(e => e.NumeroEpisodio);

        return query.ToList();
    }

    /// <summary>
    /// Un archivo local puede ir hasta esto por encima del total oficial y aun así ganarse una fila: cubre un
    /// preestreno/filtración de 1-2 días antes de la emisión oficial, o un par de especiales numerados justo después del
    /// último episodio regular. Un archivo mal nombrado con un número absurdo (p. ej. "Episodio 3000.mp4" en una carpeta
    /// con 12 episodios reales) queda muy por encima de este margen y no infla la lista.
    /// </summary>
    internal const int MargenEpisodiosAdelantados = 5;

    /// <summary>
    /// Hasta qué número de episodio dibuja la lista de la Ficha. Manda el total oficial de AniList cuando se conoce, con
    /// un pequeño margen (<see cref="MargenEpisodiosAdelantados"/>) para no ocultar un episodio real que ya tienes
    /// aunque AniList todavía no lo cuente como emitido — un archivo muchísimo más alto que ese margen (el caso del
    /// nombre mal puesto o un número absurdo) no cuenta. Solo sin dato oficial (anime recién añadido, sin sincronizar
    /// aún) se confía del todo en lo que hay en disco. El progreso visto localmente siempre puede extender la lista (si
    /// ya viste más episodios de los que AniList tiene registrados, esas filas no deben desaparecer).
    /// </summary>
    public static int CalcularMaxEpisodio(int totalEpisodiosOficial, IReadOnlyCollection<EpisodioItem> encontrados, int episodiosVistos)
    {
        int max;
        if (totalEpisodiosOficial > 0)
        {
            // Se descartan los archivos por encima del margen ANTES de calcular el máximo local: con un preestreno real (13)
            // y un archivo absurdo (4000) a la vez, el 4000 no debe "colarse" solo por ir acompañado de un número plausible.
            int limite = totalEpisodiosOficial + MargenEpisodiosAdelantados;
            int maxLocalPlausible = encontrados.Where(e => e.NumeroEpisodio <= limite).Select(e => e.NumeroEpisodio).DefaultIfEmpty(0).Max();
            max = System.Math.Max(totalEpisodiosOficial, maxLocalPlausible);
        }
        else
        {
            max = encontrados.Count > 0 ? encontrados.Max(e => e.NumeroEpisodio) : 0;
        }

        return episodiosVistos > max ? episodiosVistos : max;
    }
}
