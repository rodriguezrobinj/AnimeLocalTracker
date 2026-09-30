using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Cadena de precuelas directas de un anime (la más cercana primero) a partir de las relaciones de
/// AniList ya guardadas y de la biblioteca: sin llamadas de red. La usa el buscador de AnimeAv1 para
/// reconocer las páginas que juntan varias partes ("2nd Season" + "2nd Season Part 2"), y Nyaa para
/// reconocer los releases con numeración continua ("2nd Season - 14" = episodio 1 de la parte 2).
/// </summary>
public static class PrecuelasAnime
{
    /// <summary>Hasta cuántas partes hacia atrás se mira.</summary>
    public const int MaxNiveles = 3;

    public static async Task<IReadOnlyList<PrecuelaAnime>> ObtenerAsync(IDatabaseService db, int aniListId)
    {
        var relaciones = await db.ObtenerRelacionesAnimeAsync();
        var animes = new Dictionary<int, AnimeItem?>();
        int actual = aniListId;
        for (int i = 0; i < MaxNiveles; i++)
        {
            int? precuela = PrecuelaDirecta(relaciones, actual);
            if (precuela is not int id || animes.ContainsKey(id)) break;
            animes[id] = await db.ObtenerAnimePorIdAsync(id);
            actual = id;
        }
        return Construir(relaciones, aniListId, id => animes.GetValueOrDefault(id));
    }

    /// <summary>Lógica pura: sigue las precuelas mientras sean únicas y estén en la biblioteca.</summary>
    public static IReadOnlyList<PrecuelaAnime> Construir(IReadOnlyCollection<RelacionAnime> relaciones, int aniListId, Func<int, AnimeItem?> buscarAnime)
    {
        var cadena = new List<PrecuelaAnime>();
        var visitados = new HashSet<int> { aniListId };
        int actual = aniListId;
        for (int i = 0; i < MaxNiveles; i++)
        {
            if (PrecuelaDirecta(relaciones, actual) is not int id || !visitados.Add(id)) break;
            if (buscarAnime(id) is not AnimeItem anime) break;
            var titulos = new List<string> { anime.Titulo };
            if (!string.IsNullOrWhiteSpace(anime.NombresAlternativos))
                titulos.AddRange(anime.NombresAlternativos.Split([" | ", ";"], StringSplitOptions.RemoveEmptyEntries));
            cadena.Add(new PrecuelaAnime(anime.MalId is > 0 ? anime.MalId : null, anime.TotalEpisodios,
                titulos.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
            actual = id;
        }
        return cadena;
    }

    /// <summary>La precuela del anime si es una sola (con varias no se sabe cuál es la parte anterior).</summary>
    private static int? PrecuelaDirecta(IEnumerable<RelacionAnime> relaciones, int aniListId)
    {
        var precuelas = relaciones
            .Where(r => r.AnimeId == aniListId && r.Tipo.Equals("PREQUEL", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.RelacionadoId)
            .Distinct()
            .ToList();
        return precuelas.Count == 1 ? precuelas[0] : null;
    }
}
