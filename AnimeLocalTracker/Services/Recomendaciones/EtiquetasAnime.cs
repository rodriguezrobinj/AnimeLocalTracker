using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services.Recomendaciones;

/// <summary>
/// Los tags de AniList que se guardan con el anime (<see cref="AnimeItem.Etiquetas"/>), en JSON (<c>[{"n":"Time Manipulation","r":95}]</c>):
/// un nombre con ":" o "|" no rompe nada. Solo los que la comunidad votó al 50 % o más, sin spoilers, como mucho 15.
/// </summary>
public static class EtiquetasAnime
{
    private const int RankMinimo = 50;
    private const int Maximo = 15;

    private sealed record Guardada(string n, int r);

    public static string Serializar(IEnumerable<AniListTag> tags)
        => JsonSerializer.Serialize(tags
            .Where(t => t.Rank >= RankMinimo && !t.IsMediaSpoiler && !t.IsGeneralSpoiler && !string.IsNullOrWhiteSpace(t.Name))
            .OrderByDescending(t => t.Rank)
            .Take(Maximo)
            .Select(t => new Guardada(t.Name, t.Rank)));

    public static IReadOnlyList<(string Nombre, int Rank)> Leer(string? guardado)
    {
        if (string.IsNullOrWhiteSpace(guardado)) return Array.Empty<(string, int)>();
        try
        {
            return JsonSerializer.Deserialize<List<Guardada>>(guardado)?.Select(g => (g.n, g.r)).ToList()
                   ?? (IReadOnlyList<(string, int)>)Array.Empty<(string, int)>();
        }
        catch (JsonException)
        {
            return Array.Empty<(string, int)>();
        }
    }
}
