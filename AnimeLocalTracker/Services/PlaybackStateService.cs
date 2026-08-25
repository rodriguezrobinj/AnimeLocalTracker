using System;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

public class PlaybackStateService : IPlaybackStateService
{
    // === Reglas de negocio de persistencia de reproducción ===
    private const double MinimoSegundosParaReanudar = 5;
    private const double UmbralPorcentajeVisto = 0.95;
    private const double MinimoSegundosParaPersistir = 3;

    private readonly IDatabaseService _databaseService;
    private readonly IAnimeTrackingService _animeTrackingService;
    private readonly IAuthService _authService;

    public PlaybackStateService(
        IDatabaseService databaseService,
        IAnimeTrackingService animeTrackingService,
        IAuthService authService)
    {
        _databaseService = databaseService;
        _animeTrackingService = animeTrackingService;
        _authService = authService;
    }

    public async Task<(double Posicion, double Duracion)?> ObtenerPosicionParaReanudarAsync(int animeId, int episodio)
    {
        var registros = await _databaseService.ObtenerRegistrosPorAnimeAsync(animeId);
        var reg = registros?.FirstOrDefault(r => r.NumeroEpisodio == episodio);

        if (reg == null || reg.ProgresoSegundos <= MinimoSegundosParaReanudar)
            return null;

        // Si ya terminó (>= 95%) o la duración no está registrada con progreso avanzado, no reanudar
        if (reg.TotalSegundos > 0 && reg.ProgresoSegundos >= reg.TotalSegundos * UmbralPorcentajeVisto)
            return null;

        return (reg.ProgresoSegundos, reg.TotalSegundos);
    }

    public async Task<ResultadoGuardadoProgreso> GuardarProgresoAsync(DatosProgresoReproduccion datos)
    {
        if (datos.AnimeId <= 0 || datos.NumeroEpisodio <= 0)
            return new ResultadoGuardadoProgreso(0, datos.DuracionSegundos);

        double curSec = datos.PosicionSegundos;
        double durSec = datos.DuracionSegundos;

        double progresoAGuardar = datos.ForzarProgresoCero || (durSec > 0 && curSec >= durSec * UmbralPorcentajeVisto)
            ? 0
            : curSec;
        if (progresoAGuardar < MinimoSegundosParaPersistir) progresoAGuardar = 0;

        var registros = await _databaseService.ObtenerRegistrosPorAnimeAsync(datos.AnimeId);
        var registro = registros?.FirstOrDefault(r => r.NumeroEpisodio == datos.NumeroEpisodio);

        if (registro != null)
        {
            registro.ProgresoSegundos = progresoAGuardar;
            if (durSec > 0) registro.TotalSegundos = durSec;
            registro.UltimaReproduccion = DateTime.UtcNow;
            if (string.IsNullOrWhiteSpace(registro.RutaArchivo) && !string.IsNullOrWhiteSpace(datos.RutaVideo))
            {
                registro.RutaArchivo = datos.RutaVideo;
            }
            // Si el usuario reanuda un capítulo "visto" pero lo deja a medias, 
            // le quitamos la marca de "visto" para que se vea la barra de progreso.
            if (progresoAGuardar > 0 && (durSec <= 0 || progresoAGuardar < durSec * UmbralPorcentajeVisto))
            {
                registro.VistoLocal = false;
            }
            await _databaseService.GuardarRegistroEpisodioAsync(registro);
        }
        else if (progresoAGuardar > 0)
        {
            registro = new RegistroEpisodio
            {
                AniListId = datos.AnimeId,
                NumeroEpisodio = datos.NumeroEpisodio,
                RutaArchivo = datos.RutaVideo,
                ProgresoSegundos = progresoAGuardar,
                TotalSegundos = durSec,
                VistoLocal = datos.FueMarcadoComoVisto,
                UltimaReproduccion = DateTime.UtcNow
            };
            await _databaseService.GuardarRegistroEpisodioAsync(registro);
        }

        return new ResultadoGuardadoProgreso(progresoAGuardar, durSec);
    }

    public async Task<bool> MarcarComoVistoYSincronizarAsync(int animeId, int episodio, string rutaVideo, double duracionSegundos)
    {
        try
        {
            // 1. Guardar localmente
            var registros = await _databaseService.ObtenerRegistrosPorAnimeAsync(animeId);
            var registro = registros?.FirstOrDefault(r => r.NumeroEpisodio == episodio);

            if (registro != null)
            {
                registro.VistoLocal = true;
                registro.ProgresoSegundos = 0; // Al marcarse como visto, el progreso se limpia a 0
                registro.UltimaReproduccion = DateTime.UtcNow;
            }
            else
            {
                registro = new RegistroEpisodio
                {
                    AniListId = animeId,
                    NumeroEpisodio = episodio,
                    RutaArchivo = rutaVideo,
                    VistoLocal = true,
                    ProgresoSegundos = 0,
                    UltimaReproduccion = DateTime.UtcNow
                };
            }
            await _databaseService.GuardarRegistroEpisodioAsync(registro);

            // 2. Guardar en AniList
            var token = _authService.ObtenerTokenGuardado();
            if (!string.IsNullOrEmpty(token))
            {
                await _animeTrackingService.ActualizarProgresoAsync(animeId, episodio, token);
            }

            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("PlaybackStateService", $"Error al marcar como visto el episodio {episodio} del anime {animeId}", ex);
            return false;
        }
    }
}
