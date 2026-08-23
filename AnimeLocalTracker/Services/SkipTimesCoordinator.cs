using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

public class SkipTimesCoordinator : ISkipTimesCoordinator
{
    // Ventana del fallback genérico de intro cuando AniSkip no tiene datos
    public const double VentanaIntroGenericaInicio = 30;
    public const double VentanaIntroGenericaFin = 180;

    private readonly IAniSkipService? _aniSkipService;

    // Memoización del mapeo AniListId -> MAL ID durante la vida del reproductor
    private readonly ConcurrentDictionary<int, int?> _malIdCache = new();

    public SkipTimesCoordinator(IAniSkipService? aniSkipService)
    {
        _aniSkipService = aniSkipService;
    }

    public async Task<IReadOnlyList<AniSkipResult>> CargarSkipTimesAsync(int animeId, int episodio, double duracionSegundos, CancellationToken ct = default)
    {
        if (_aniSkipService == null || animeId <= 0 || episodio <= 0) return [];

        try
        {
            var malId = _malIdCache.GetOrAdd(animeId, id => null);
            if (!malId.HasValue || malId.Value <= 0)
            {
                malId = await _aniSkipService.ObtenerMalIdDesdeAniListAsync(animeId, ct);
                if (malId.HasValue && malId.Value > 0)
                {
                    _malIdCache[animeId] = malId;
                }
            }

            if (malId.HasValue && malId.Value > 0)
            {
                var results = await _aniSkipService.ObtenerSkipTimesAsync(malId.Value, episodio, duracionSegundos, ct);
                if (results != null && results.Count > 0)
                {
                    AppLogger.Info("SkipTimesCoordinator", $"AniSkip: {results.Count} segmentos cargados para MAL ID {malId.Value}, Ep {episodio}");
                    return results;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("SkipTimesCoordinator", $"Error cargando skip times de AniSkip: {ex.Message}");
        }

        return [];
    }

    public AniSkipResult? ObtenerSkipActivo(double currentSeconds, IReadOnlyList<AniSkipResult> skipTimes, double margenFinalSegundos = 0)
    {
        if (skipTimes == null || skipTimes.Count == 0) return null;

        return skipTimes.FirstOrDefault(s =>
            currentSeconds >= s.Interval.StartTime &&
            currentSeconds < s.Interval.EndTime - margenFinalSegundos);
    }

    public bool EstaEnVentanaIntroGenerica(double currentSeconds)
    {
        return currentSeconds >= VentanaIntroGenericaInicio && currentSeconds <= VentanaIntroGenericaFin;
    }
}
