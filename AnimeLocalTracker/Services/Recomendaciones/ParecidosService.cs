using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Services.Recomendaciones;

/// <summary>"Parecidos en tu biblioteca" de la ficha: animes pendientes que se parecen al que se está viendo.</summary>
public interface IParecidosService
{
    /// <summary>Hasta 5, de más a menos parecido. Lista vacía si no hay o si algo falla (nunca lanza).</summary>
    Task<IReadOnlyList<Parecido>> BuscarAsync(int aniListId, CancellationToken ct);
}

/// <summary>
/// Lee la biblioteca y calcula en segundo plano (<see cref="SimilitudAnimes"/>). Sin caché: con unos cientos de animes son
/// milisegundos, y así siempre refleja lo último que se vio o se descargó.
/// </summary>
public sealed class ParecidosService(IDatabaseService database) : IParecidosService
{
    public async Task<IReadOnlyList<Parecido>> BuscarAsync(int aniListId, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var animes = await database.ObtenerTodosLosAnimesAsync().ConfigureAwait(false) ?? new();
            var baseAnime = animes.FirstOrDefault(a => a.AniListId == aniListId);
            if (baseAnime == null) return Array.Empty<Parecido>();

            var vistos = await database.ObtenerEpisodiosVistosPorAnimeAsync().ConfigureAwait(false) ?? new();
            var registros = await database.ObtenerTodosLosRegistrosAsync().ConfigureAwait(false) ?? new();
            ct.ThrowIfCancellationRequested();

            return await Task.Run(() =>
            {
                // Episodios que se pueden ver ya: con archivo en disco y sin ver.
                var enDisco = registros.Where(r => !string.IsNullOrEmpty(r.RutaArchivo) && !r.VistoLocal)
                    .GroupBy(r => r.AniListId).ToDictionary(g => g.Key, g => g.Count());
                return SimilitudAnimes.Calcular(baseAnime, animes, vistos, enDisco);
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<Parecido>();
        }
        catch (Exception ex)
        {
            AppLogger.Warn("Parecidos", $"No se pudieron calcular los parecidos de {aniListId}: {ex.Message}");
            return Array.Empty<Parecido>();
        }
    }
}
