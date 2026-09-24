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
    private readonly IAnimeThemesDownloadService? _themesDownload;

    /// <summary>
    /// Localiza audio_skip_plugin.py sin depender de AppDataPaths.PluginsFolder (esa carpeta es para
    /// plugins que el USUARIO instala a mano; este es un plugin propio de la app, siempre debe estar
    /// disponible). En desarrollo se lee directo de tools/python/ (única fuente de verdad, subiendo
    /// directorios desde el ejecutable); en un build empaquetado esa carpeta del repo no existe, así
    /// que se usa la copia sincronizada en AnimeLocalTracker/PythonPlugins/ (incluida por
    /// &lt;None Include="PythonPlugins\**"/&gt; del .csproj — a diferencia de Tools\, esta SÍ va al
    /// control de versiones). Mantener ambas copias iguales al tocar el algoritmo.
    /// </summary>
    private static readonly Lazy<string?> RutaPluginAudioSkip = new(() =>
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;

        var searchDir = new DirectoryInfo(baseDir);
        for (int i = 0; i < 6 && searchDir != null; i++)
        {
            string candidato = Path.Combine(searchDir.FullName, "tools", "python", "audio_skip_plugin.py");
            if (File.Exists(candidato)) return candidato;
            searchDir = searchDir.Parent;
        }

        string empaquetado = Path.Combine(baseDir, "PythonPlugins", "audio_skip_plugin.py");
        return File.Exists(empaquetado) ? empaquetado : null;
    });

    public SkipTimesCoordinator(IAniSkipService? aniSkipService, IPythonBridgeService? pythonBridge = null, IAnimeThemesDownloadService? themesDownload = null)
    {
        _aniSkipService = aniSkipService;
        _pythonBridge = pythonBridge;
        _themesDownload = themesDownload;
    }

    public async Task<IReadOnlyList<AniSkipResult>> CargarSkipTimesAsync(int animeId, int episodio, double duracionSegundos, string? rutaVideoLocal = null, CancellationToken ct = default)
    {
        // FUENTE PRINCIPAL: audio de referencia oficial (OP/ED descargados de AnimeThemes.moe desde
        // la Ficha). Es la más confiable cuando está disponible: funciona con un solo episodio local
        // (no necesita un segundo capítulo para comparar) y cubre OP y ED, no solo OP.
        if (!string.IsNullOrWhiteSpace(rutaVideoLocal) && _pythonBridge != null && _themesDownload != null && RutaPluginAudioSkip.Value != null)
        {
            var resultadoReferencia = await IntentarDeteccionPorReferenciaAsync(animeId, episodio, rutaVideoLocal, ct);
            if (resultadoReferencia.Count > 0)
            {
                return resultadoReferencia;
            }
        }

        // FUENTE DE PRUEBA: Plugin de Detección por Audio (comparación entre 2 episodios locales)
        if (!string.IsNullOrWhiteSpace(rutaVideoLocal) && _pythonBridge != null && RutaPluginAudioSkip.Value != null)
        {
            try
            {
                if (await _pythonBridge.IsAvailableAsync())
                {
                    string dir = Path.GetDirectoryName(rutaVideoLocal)!;
                    var otroVideo = Directory.GetFiles(dir, "*.*").FirstOrDefault(f => f != rutaVideoLocal && (f.EndsWith(".mkv") || f.EndsWith(".mp4")));

                    if (otroVideo != null)
                    {
                        var pluginPath = RutaPluginAudioSkip.Value;
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

    /// <summary>
    /// Usa los OP/ED que el usuario ya descargó desde la Ficha (ver AnimeThemesDownloadService) como
    /// referencia para ubicar el segmento exacto dentro de ESTE episodio. A diferencia del plugin de
    /// comparación entre 2 episodios, esto funciona con uno solo y también detecta ED.
    /// </summary>
    private async Task<List<AniSkipResult>> IntentarDeteccionPorReferenciaAsync(int animeId, int episodio, string rutaVideoLocal, CancellationToken ct)
    {
        var resultados = new List<AniSkipResult>();
        try
        {
            if (!await _pythonBridge!.IsAvailableAsync()) return resultados;

            var descargas = _themesDownload!.ListarDescargasLocales(animeId);
            if (descargas.Count == 0) return resultados;

            var opCandidato = descargas.FirstOrDefault(t => string.Equals(t.Tipo, "OP", StringComparison.OrdinalIgnoreCase) && t.AplicaAlEpisodio(episodio));
            if (opCandidato != null)
            {
                var op = await EjecutarDeteccionReferenciaAsync(rutaVideoLocal, opCandidato.RutaArchivo, buscarDesdeElFinal: false, ct);
                if (op != null)
                {
                    resultados.Add(CrearSkip("op", op.EstimatedStart, op.EstimatedEnd));
                    AppLogger.Info("SkipTimesCoordinator", $"REFERENCIA ANIMETHEMES: Opening detectado [{op.EstimatedStart:F1} - {op.EstimatedEnd:F1}] (Conf: {op.Confidence:F2})");
                }
            }

            var edCandidato = descargas.FirstOrDefault(t => string.Equals(t.Tipo, "ED", StringComparison.OrdinalIgnoreCase) && t.AplicaAlEpisodio(episodio));
            if (edCandidato != null)
            {
                var ed = await EjecutarDeteccionReferenciaAsync(rutaVideoLocal, edCandidato.RutaArchivo, buscarDesdeElFinal: true, ct);
                if (ed != null)
                {
                    resultados.Add(CrearSkip("ed", ed.EstimatedStart, ed.EstimatedEnd));
                    AppLogger.Info("SkipTimesCoordinator", $"REFERENCIA ANIMETHEMES: Ending detectado [{ed.EstimatedStart:F1} - {ed.EstimatedEnd:F1}] (Conf: {ed.Confidence:F2})");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLogger.Debug("SkipTimesCoordinator", $"Error en detección por audio de referencia: {ex.Message}");
        }
        return resultados;
    }

    private async Task<AudioReferenceSkipResult?> EjecutarDeteccionReferenciaAsync(string rutaEpisodio, string rutaReferencia, bool buscarDesdeElFinal, CancellationToken ct)
    {
        var payload = new
        {
            plugin_path = RutaPluginAudioSkip.Value,
            func_name = "detect_from_reference",
            args = new
            {
                episode_path = rutaEpisodio,
                reference_path = rutaReferencia,
                search_duration = 300.0,
                search_from_end = buscarDesdeElFinal
            }
        };

        var pluginRes = await _pythonBridge!.ExecuteCommandAsync<object, PluginDaemonResponse<AudioReferenceSkipResult>>("run-plugin", payload, ct);
        return pluginRes is { Success: true, Result.Found: true } ? pluginRes.Result : null;
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

    /// <summary>Respuesta de audio_skip_plugin.detect_from_reference (JSON: estimated_start/estimated_end).</summary>
    public class AudioReferenceSkipResult
    {
        public bool Found { get; set; }
        public double EstimatedStart { get; set; }
        public double EstimatedEnd { get; set; }
        public double Confidence { get; set; }
        public string? Source { get; set; }
    }
}
