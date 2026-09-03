using System;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Extrae la lógica de enriquecimiento de metadatos (ARQ-03).
/// Descarga o actualiza información detallada de animes y episodios desde fuentes externas.
/// </summary>
public interface IMediaEnrichmentService
{
    Task<bool> ActualizarMetadatosAnimeAsync(AnimeItem anime);
}

public class MediaEnrichmentService : IMediaEnrichmentService
{
    private readonly IAnimeTrackingService _animeTrackingService;
    private readonly IDatabaseService _databaseService;

    public MediaEnrichmentService(IAnimeTrackingService animeTrackingService, IDatabaseService databaseService)
    {
        _animeTrackingService = animeTrackingService;
        _databaseService = databaseService;
    }

    public async Task<bool> ActualizarMetadatosAnimeAsync(AnimeItem anime)
    {
        if (anime == null || anime.AniListId <= 0) return false;

        var datosFrescos = await _animeTrackingService.ObtenerAnimePorIdAsync(anime.AniListId);
        if (datosFrescos == null) return false;

        int episodiosEmitidos = 0;
        if (datosFrescos.NextAiringEpisode != null && datosFrescos.NextAiringEpisode.Episode > 1)
        {
            episodiosEmitidos = datosFrescos.NextAiringEpisode.Episode - 1;
        }
        else
        {
            episodiosEmitidos = datosFrescos.Episodes ?? anime.TotalEpisodios;
        }

        // TODO: Actualizar todos los campos adicionales (nombres alternativos, estado, géneros, portadas).
        // Delegado desde DetalleViewModel.cs (ARQ-03).
        
        return true;
    }
}
