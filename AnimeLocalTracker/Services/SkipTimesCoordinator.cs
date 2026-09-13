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
        // FUENTE DE PRUEBA: Plugin de Detección por Audio
        if (!string.IsNullOrWhiteSpace(rutaVideoLocal) && _pythonBridge != null)
        {
            try
            {
                if (await _pythonBridge.IsAvailableAsync())
                {
                    string dir = Path.GetDirectoryName(rutaVideoLocal)!;
                    var otroVideo = Directory.GetFiles(dir, "*.*").FirstOrDefault(f => f != rutaVideoLocal && (f.EndsWith(".mkv") || f.EndsWith(".mp4")));
                    
                    if (otroVideo != null)
                    {
                        var pluginPath = Path.Combine(AppDataPaths.PluginsFolder, "audio_skip_plugin.py");
                        var payload = new
                        {
                            plugin_path = pluginPath,
                            func_name = "detect_opening",
                            args = new { video_paths = new[] { rutaVideoLocal, otroVideo } }
                        };
                        
                        // PluginDaemonResponse<AudioSkipResult>
                        var pluginRes = await _pythonBridge.ExecuteCommandAsync<object, AnimeLocalTracker.Services.PluginDaemonResponse<AudioSkipResult>>("run-plugin", payload, ct);
                        
                        if (pluginRes != null && pluginRes.Success && pluginRes.Result != null && pluginRes.Result.Found)
                        {
                            var r = pluginRes.Result;
                            var locales = new List<AniSkipResult>();
                            locales.Add(CrearSkip("op", r.IntroEstimatedStart, r.IntroEstimatedEnd));
                            
                            AppLogger.Info("SkipTimesCoordinator", $"PLUGIN AUDIO: Opening detectado [{r.IntroEstimatedStart} - {r.IntroEstimatedEnd}] (Conf: {r.Confidence})");
                            return locales;
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AppLogger.Debug("SkipTimesCoordinator", $"Error en plugin de audio local: {ex.Message}");
            }
        }

        // FALLBACK: Si no hay otro video local o el plugin falla, usamos la nube (AniSkip)
        if (_aniSkipService != null && animeId > 0 && episodio > 0)
        {
            var malId = await _aniSkipService.ObtenerMalIdDesdeAniListAsync(animeId, ct);
            if (malId.HasValue)
            {
                return await _aniSkipService.ObtenerSkipTimesAsync(malId.Value, episodio, duracionSegundos, ct);
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

    public class AudioSkipResult
    {
        public bool Found { get; set; }
        public double IntroEstimatedStart { get; set; }
        public double IntroEstimatedEnd { get; set; }
        public double Confidence { get; set; }
        public string? Source { get; set; }
    }

}
