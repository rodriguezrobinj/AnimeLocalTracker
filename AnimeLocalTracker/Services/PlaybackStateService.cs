using System;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

public class PlaybackStateService : IPlaybackStateService
{
    // === Reglas de negocio de persistencia de reproducción ===
    private const double MinimoSegundosParaReanudar = 5;
    private const double MinimoSegundosParaPersistir = 3;

    private readonly IDatabaseService _databaseService;
    private readonly IAnimeTrackingService _animeTrackingService;
    private readonly IAuthService _authService;
    private readonly ISettingsService? _settingsService;

    public PlaybackStateService(
        IDatabaseService databaseService,
        IAnimeTrackingService animeTrackingService,
        IAuthService authService,
        ISettingsService? settingsService = null)
    {
        _databaseService = databaseService;
        _animeTrackingService = animeTrackingService;
        _authService = authService;
        _settingsService = settingsService;
    }

    /// <summary>
    /// Umbral configurable de "marcado visto" (AppSettings.UmbralMarcadoVisto, 1-100); si no
    /// hay configuración disponible se usa el valor por defecto 90% — el mismo fallback que
    /// ReproductorViewModel.UmbralMarcadoVistoActual y que AppSettings.UmbralMarcadoVisto.
    /// Deben coincidir: si este servicio usara un umbral más estricto que el del reproductor,
    /// un guardado de progreso entre ambos umbrales podría revertir un episodio recién marcado
    /// como visto (mitigado además por el chequeo de FueMarcadoComoVisto en GuardarProgresoAsync).
    /// </summary>
    private double UmbralVisto
    {
        get
        {
            int porcentaje = _settingsService?.ObtenerConfiguracion()?.UmbralMarcadoVisto ?? 90;
            return Math.Clamp(porcentaje, 1, 100) / 100.0;
        }
    }

    public async Task<(double Posicion, double Duracion)?> ObtenerPosicionParaReanudarAsync(int animeId, int episodio, string? rutaVideoActual = null)
    {
        var registros = await _databaseService.ObtenerRegistrosPorAnimeAsync(animeId);
        var reg = registros?.FirstOrDefault(r => r.NumeroEpisodio == episodio);

        if (reg == null || reg.ProgresoSegundos <= MinimoSegundosParaReanudar)
            return null;

        // FUN-006: si el archivo del episodio cambió (fue reemplazado por otro), la posición
        // guardada corresponde al archivo viejo y no debe reanudarse a ciegas.
        if (!string.IsNullOrWhiteSpace(reg.RutaArchivo)
            && !string.IsNullOrWhiteSpace(rutaVideoActual)
            && !reg.RutaArchivo.Equals(rutaVideoActual, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Si ya terminó (>= 95%) o la duración no está registrada con progreso avanzado, no reanudar
        if (reg.TotalSegundos > 0 && reg.ProgresoSegundos >= reg.TotalSegundos * UmbralVisto)
            return null;

        return (reg.ProgresoSegundos, reg.TotalSegundos);
    }

    public async Task<ResultadoGuardadoProgreso> GuardarProgresoAsync(DatosProgresoReproduccion datos)
    {
        if (datos.AnimeId <= 0 || datos.NumeroEpisodio <= 0)
            return new ResultadoGuardadoProgreso(0, datos.DuracionSegundos);

        double curSec = datos.PosicionSegundos;
        double durSec = datos.DuracionSegundos;

        double progresoAGuardar = datos.ForzarProgresoCero || (durSec > 0 && curSec >= durSec * UmbralVisto)
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
            // Si el usuario reanuda un capítulo "visto" pero lo deja a medias, le quitamos
            // la marca de "visto" para que se vea la barra de progreso — PERO nunca si el
            // reproductor ya determinó en ESTA sesión que se completó (datos.FueMarcadoComoVisto).
            // Sin este chequeo, el guardado periódico (cada 3s) o el guardado al cambiar de
            // episodio podían revertir a "no visto" un episodio recién marcado: su propio
            // umbral (95% por defecto) es más estricto que el del reproductor (90% por
            // defecto), así que en la ventana 90-95% este guardado deshacía el marcado que
            // RealizarAutoTrackingAsync acababa de confirmar — el síntoma exacto al ver
            // capítulos seguidos sin volver a la ficha del anime.
            if (!datos.FueMarcadoComoVisto && progresoAGuardar > 0 && (durSec <= 0 || progresoAGuardar < durSec * UmbralVisto))
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

    public async Task<bool> MarcarComoVistoYSincronizarAsync(int animeId, int episodio, string rutaVideo, double duracionSegundos, bool registrarReproduccion = true)
    {
        // FUN-004: un episodio sin número (0 = archivo sin dígitos) nunca debe marcarse ni
        // sincronizarse: empujar progress=0 a AniList reseteaba el progreso real del usuario.
        if (animeId <= 0 || episodio <= 0)
        {
            AppLogger.Warn("PlaybackStateService", $"Marcado como visto ignorado para episodio inválido (anime {animeId}, ep {episodio}).");
            return false;
        }

        try
        {
            // 1. Guardar localmente
            var registros = await _databaseService.ObtenerRegistrosPorAnimeAsync(animeId);
            var registro = registros?.FirstOrDefault(r => r.NumeroEpisodio == episodio);

            // El "historial" refleja VISIONADO REAL: solo la reproducción (auto-track al
            // terminar de ver) registra la fecha; el marcado manual no debe parecer visto hoy.
            if (registro != null)
            {
                registro.VistoLocal = true;
                registro.ProgresoSegundos = 0; // Al marcarse como visto, el progreso se limpia a 0
                registro.UltimaReproduccion = registrarReproduccion ? DateTime.UtcNow : registro.UltimaReproduccion;
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
                    UltimaReproduccion = registrarReproduccion ? DateTime.UtcNow : null
                };
            }
            await _databaseService.GuardarRegistroEpisodioAsync(registro);

            // 2. Guardar en AniList
            var token = _authService.ObtenerTokenGuardado();
            if (!string.IsNullOrEmpty(token))
            {
                // Solo el visionado REAL fija fechas. La entrada de AniList se lee ANTES de subir el progreso:
                // después ya no se sabría si el usuario venía de cero (inicio) o de un re-visionado.
                AniListMediaList? remotoPrevio = null;
                if (registrarReproduccion)
                {
                    try { remotoPrevio = await _animeTrackingService.ObtenerSeguimientoUsuarioAsync(animeId, token); }
                    catch (Exception ex) { AppLogger.Debug("PlaybackStateService", $"No se pudo leer el seguimiento previo de {animeId}: {ex.Message}"); }
                }

                bool progresoSubido = await _animeTrackingService.ActualizarProgresoAsync(animeId, episodio, token);

                if (progresoSubido && registrarReproduccion)
                {
                    await SeguimientoFechas.AplicarAsync(_databaseService, _animeTrackingService, animeId, remotoPrevio, episodio, token);
                }
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
