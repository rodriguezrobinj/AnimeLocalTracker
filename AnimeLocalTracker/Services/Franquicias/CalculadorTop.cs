using System;
using System.Collections.Generic;
using System.Linq;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services.Franquicias;

/// <summary>Una franquicia (o un anime suelto) con el tiempo total que le has dedicado.
/// <paramref name="AniListIdRepresentante"/> es el mismo título que da nombre a la franquicia
/// (ver comentario en Calcular) — sirve para resolver su portada, p. ej. en la tarjeta Wrapped.</summary>
public sealed record TopFranquiciaItem(int FranquiciaId, string Titulo, double Segundos, int Episodios, int Titulos, int AniListIdRepresentante);

/// <summary>
/// Top de lo más visto POR TIEMPO, sumando toda la franquicia (temporadas, películas, especiales, spin-offs).
/// Antes se ordenaba por número de episodios: One Piece, con más de mil, ganaba siempre aunque no fuera lo que
/// más horas te llevó. Cálculo puro (sin red ni base de datos).
/// </summary>
public static class CalculadorTop
{
    /// <summary>Un título solo puede dar nombre a su franquicia si suma al menos esta fracción del tiempo del más visto.</summary>
    private const double UmbralDeRepresentante = 0.2;

    public static List<TopFranquiciaItem> Calcular(
        IReadOnlyList<AnimeItem> animes,
        IReadOnlyCollection<RegistroEpisodio> vistos,
        IReadOnlyDictionary<int, int> franquicias,
        int cantidad = 5)
    {
        var animesPorId = new Dictionary<int, AnimeItem>();
        foreach (var anime in animes) animesPorId.TryAdd(anime.AniListId, anime);

        // Promedio global para los episodios sin duración guardada (mismo criterio que el total de horas).
        var medidor = EstimadorDuracion.CrearMedidor(vistos);

        var porAnime = vistos
            .GroupBy(r => r.AniListId)
            .Select(g => (
                AnimeId: g.Key,
                Segundos: g.Sum(medidor),
                Episodios: g.Select(r => r.NumeroEpisodio).Distinct().Count()))
            .ToList();

        return porAnime
            .GroupBy(x => franquicias.TryGetValue(x.AnimeId, out var franquicia) ? franquicia : x.AnimeId)
            .Select(grupo =>
            {
                // El nombre de la franquicia es el de su título más ANTIGUO entre los que pesan de verdad (al
                // menos un 20 % del tiempo del título más visto): normalmente la primera temporada, con el nombre
                // limpio ("Danmachi" y no "Danmachi V: Houjo…"). Una película suelta o un especial que veas poco
                // no da nombre a la franquicia. Sin año conocido (o empate), el id de AniList menor: casi cronológico.
                double maximoDelGrupo = grupo.Max(x => x.Segundos);
                var representante = grupo
                    .Where(x => x.Segundos >= maximoDelGrupo * UmbralDeRepresentante)
                    .OrderBy(x => animesPorId.TryGetValue(x.AnimeId, out var a) && a.AnioLanzamiento > 0 ? a.AnioLanzamiento : int.MaxValue)
                    .ThenBy(x => x.AnimeId)
                    .First();

                string titulo = animesPorId.TryGetValue(representante.AnimeId, out var animeRep) && !string.IsNullOrWhiteSpace(animeRep.Titulo)
                    ? animeRep.Titulo
                    : $"Anime {representante.AnimeId}";

                return new TopFranquiciaItem(
                    grupo.Key,
                    titulo,
                    grupo.Sum(x => x.Segundos),
                    grupo.Sum(x => x.Episodios),
                    grupo.Count(),
                    representante.AnimeId);
            })
            .OrderByDescending(f => f.Segundos)
            .ThenBy(f => f.Titulo, StringComparer.OrdinalIgnoreCase)
            .Take(cantidad)
            .ToList();
    }
}
