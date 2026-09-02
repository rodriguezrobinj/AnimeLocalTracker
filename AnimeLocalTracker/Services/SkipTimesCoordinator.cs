using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services.Python;

namespace AnimeLocalTracker.Services;

public class SkipTimesCoordinator : ISkipTimesCoordinator
{
    private readonly IAniSkipService? _aniSkipService;
    private readonly IPythonBridgeService? _pythonBridge;

    public SkipTimesCoordinator(IAniSkipService? aniSkipService, IPythonBridgeService? pythonBridge = null)
    {
        _aniSkipService = aniSkipService;
        _pythonBridge = pythonBridge;
    }

    public async Task<IReadOnlyList<AniSkipResult>> CargarSkipTimesAsync(int animeId, int episodio, double duracionSegundos, string? rutaVideoLocal = null, CancellationToken ct = default)
    {
        // Fuente 1: AniSkip API (comunitaria, requiere MAL ID)
        if (_aniSkipService != null && animeId > 0 && episodio > 0)
        {
            try
            {
                // ARC-05: el mapeo AniListId→MAL ID se memoiza dentro de IAniSkipService
                // (caché única compartida por toda la app, con tope de 2000 entradas).
                var malId = await _aniSkipService.ObtenerMalIdDesdeAniListAsync(animeId, ct);
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
        }

        // Fuente 2: Detección local por escenas (Python/ffmpeg) si tenemos el video local
        if (!string.IsNullOrWhiteSpace(rutaVideoLocal) && _pythonBridge != null)
        {
            try
            {
                if (await _pythonBridge.IsAvailableAsync())
                {
                    var scenes = await _pythonBridge.ExecuteCommandAsync<object, SceneDetectResult>(
                        "detect-scenes",
                        new { video_path = rutaVideoLocal, max_seconds = 300 },
                        ct
                    );

                    if (scenes != null && scenes.Success && scenes.Confidence > 0)
                    {
                        var locales = new List<AniSkipResult>();
                        if (scenes.IntroStart.HasValue && scenes.IntroEnd.HasValue)
                        {
                            locales.Add(CrearSkip("op", scenes.IntroStart.Value, scenes.IntroEnd.Value));
                        }
                        if (scenes.EndingStart.HasValue && scenes.EndingEnd.HasValue)
                        {
                            locales.Add(CrearSkip("ed", scenes.EndingStart.Value, scenes.EndingEnd.Value));
                        }

                        if (locales.Count > 0)
                        {
                            AppLogger.Info("SkipTimesCoordinator", $"Detección local: {locales.Count} segmentos para '{Path.GetFileName(rutaVideoLocal)}'");
                            return locales;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Cancelación esperada al navegar o cambiar de episodio
            }
            catch (Exception ex)
            {
                AppLogger.Debug("SkipTimesCoordinator", $"Error en detección local de escenas: {ex.Message}");
            }
        }

        return [];
    }

    private static AniSkipResult CrearSkip(string tipo, double inicio, double fin)
    {
        return new AniSkipResult
        {
            SkipType = tipo,
            Interval = new AniSkipInterval { StartTime = inicio, EndTime = fin }
        };
    }

    public AniSkipResult? ObtenerSkipActivo(double currentSeconds, IReadOnlyList<AniSkipResult> skipTimes, double margenFinalSegundos = 0)
    {
        if (skipTimes == null || skipTimes.Count == 0) return null;

        return skipTimes.FirstOrDefault(s =>
            currentSeconds >= s.Interval.StartTime &&
            currentSeconds < s.Interval.EndTime - margenFinalSegundos);
    }

    private class SceneDetectResult
    {
        public bool Success { get; set; }
        public double Confidence { get; set; }
        public double? IntroStart { get; set; }
        public double? IntroEnd { get; set; }
        public double? EndingStart { get; set; }
        public double? EndingEnd { get; set; }
    }
}
