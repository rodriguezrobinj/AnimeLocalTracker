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
}
