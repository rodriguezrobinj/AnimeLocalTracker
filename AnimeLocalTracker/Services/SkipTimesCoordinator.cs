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
    /// <summary>
    /// Confianza mínima para aceptar una detección por audio. Con el tema correcto sale entre 0,9 y 1,0; probar un tema que no
    /// corresponde al episodio deja "encontrados" espurios de 0,4 a 0,5 (medido con episodios reales).
    /// </summary>
    internal const double ConfianzaMinima = 0.7;

    /// <summary>Un análisis incompleto (sin red, anime sin datos…) se repite pasado este tiempo; uno completo vale mientras el archivo no cambie.</summary>
    internal static readonly TimeSpan ReintentoDeIncompletos = TimeSpan.FromHours(12);

    internal const string OrigenAudio = "audio";
    internal const string OrigenAniSkip = "aniskip";
    internal const string OrigenEscenas = "escenas";

    private readonly IAniSkipService? _aniSkipService;
    private readonly IPythonBridgeService? _pythonBridge;
    private readonly IAnimeThemesDownloadService? _themesDownload;
    private readonly IReferenciasAudioService? _referencias;
    private readonly IDatabaseService? _database;

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

    /// <param name="referencias">Consigue (y baja si falta) el audio oficial de los temas; sin él solo se usan las descargas de la Ficha.</param>
    /// <param name="database">Guarda el análisis de cada episodio (segunda vez: al instante y sin red). Sin él no hay caché.</param>
    public SkipTimesCoordinator(IAniSkipService? aniSkipService, IPythonBridgeService? pythonBridge = null, IAnimeThemesDownloadService? themesDownload = null,
        IReferenciasAudioService? referencias = null, IDatabaseService? database = null)
    {
        _aniSkipService = aniSkipService;
        _pythonBridge = pythonBridge;
        _themesDownload = themesDownload;
        _referencias = referencias;
        _database = database;
    }

    /// <summary>
    /// Ubica el opening, el ending y el resumen del episodio. Orden: análisis ya guardado → audio oficial de AnimeThemes (el mejor
    /// de todos los temas candidatos) → AniSkip solo para lo que falte → comparación con otro episodio local (último recurso para
    /// el opening). El resultado se guarda para no repetirlo.
    /// </summary>
    public Task<IReadOnlyList<AniSkipResult>> CargarSkipTimesAsync(int animeId, int episodio, double duracionSegundos, string? rutaVideoLocal = null, CancellationToken ct = default) =>
        CargarSkipTimesAsync(animeId, episodio, duracionSegundos, rutaVideoLocal, null, ct);

    public async Task<IReadOnlyList<AniSkipResult>> CargarSkipTimesAsync(int animeId, int episodio, double duracionSegundos, string? rutaVideoLocal,
        IProgress<IReadOnlyList<AniSkipResult>>? progreso, CancellationToken ct)
    {
        string? firma = CalcularFirma(rutaVideoLocal);

        // 1) Análisis guardado
        var guardado = await LeerGuardadoAsync(animeId, episodio, firma);
        if (guardado != null) return guardado;

        var resultados = new List<AniSkipResult>();
        bool completo = true;
        bool referenciasCompletas = false;

        // 2) Audio de referencia (AnimeThemes): funciona con un solo episodio local y cubre opening y ending
        bool hayVideo = !string.IsNullOrWhiteSpace(rutaVideoLocal);
        bool puedeDetectar = hayVideo && _pythonBridge != null && RutaPluginAudioSkip.Value != null && (_referencias != null || _themesDownload != null);
        if (puedeDetectar)
        {
            var referencias = await ObtenerReferenciasAsync(animeId, episodio, ct);
            referenciasCompletas = referencias.Completa;
            if (!referencias.Completa) completo = false;

            foreach (var tipo in new[] { "OP", "ED" })
            {
                var mejor = await DetectarMejorAsync(tipo, episodio, referencias.Temas, rutaVideoLocal!, ct);
                if (mejor != null)
                {
                    var skip = CrearSkip(tipo.ToLowerInvariant(), mejor.EstimatedStart, mejor.EstimatedEnd, OrigenAudio, mejor.Confidence);
                    resultados.Add(skip);
                    AppLogger.Info("SkipTimesCoordinator", $"REFERENCIA ANIMETHEMES: {tipo} detectado [{mejor.EstimatedStart:F1} - {mejor.EstimatedEnd:F1}] (Conf: {mejor.Confidence:F2})");
                }
            }
        }
        else
        {
            completo = false;
        }

        ct.ThrowIfCancellationRequested();
        if (resultados.Count > 0) progreso?.Report(resultados.ToList()); // la barra ya puede mostrar lo del audio

        // 3) AniSkip (comunidad): solo si el audio no encontró el opening o el ending, y solo se toma lo que falta (más el resumen previo al
        //    opening, que nunca sale del audio). Con los dos ya ubicados no se consulta: es más rápido y no gasta red ni cuota.
        bool faltaOpening = !resultados.Any(r => r.EsIntro);
        bool faltaEnding = !resultados.Any(r => r.EsEnding);
        if (_aniSkipService != null && animeId > 0 && episodio > 0 && (faltaOpening || faltaEnding))
        {
            var (consultado, lista) = await ConsultarAniSkipAsync(animeId, episodio, duracionSegundos, ct);
            if (!consultado) completo = false;

            foreach (var r in lista)
            {
                bool util = r.EsRecap || (r.EsIntro && faltaOpening) || (r.EsEnding && faltaEnding);
                if (!util) continue;
                r.Origen = OrigenAniSkip;
                resultados.Add(r);
            }
        }

        ct.ThrowIfCancellationRequested();
        if (resultados.Count > 0) progreso?.Report(resultados.ToList()); // con lo de AniSkip incluido

        // 4) Último recurso para el opening: compararlo con otro episodio local. Es lento (decenas de segundos) y poco fiable, así que solo
        //    si AnimeThemes no dio nada con qué trabajar: con las referencias completas, que no salga un opening es un dato (episodio sin OP).
        bool sinReferencias = !puedeDetectar || !referenciasCompletas;
        if (hayVideo && sinReferencias && !resultados.Any(r => r.EsIntro) && _pythonBridge != null && RutaPluginAudioSkip.Value != null)
        {
            var local = await DetectarPorComparacionAsync(rutaVideoLocal!, ct);
            if (local != null) resultados.Add(local);
        }

        // Si aún falta el opening o el ending, el análisis se repite pasadas unas horas: AniSkip recibe datos con el tiempo y
        // AnimeThemes añade temas nuevos. (Un episodio sin opening de verdad solo cuesta esa repetición.)
        if (!resultados.Any(r => r.EsIntro) || !resultados.Any(r => r.EsEnding)) completo = false;

        await GuardarAsync(animeId, episodio, firma, completo, resultados);
        return resultados;
    }

    private async Task<ReferenciasEpisodio> ObtenerReferenciasAsync(int animeId, int episodio, CancellationToken ct)
    {
        try
        {
            if (_referencias != null) return await _referencias.ObtenerAsync(animeId, episodio, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLogger.Debug("SkipTimesCoordinator", $"Error consiguiendo las referencias de audio: {ex.Message}");
        }

        // Sin servicio de referencias: solo lo que el usuario ya descargó desde la Ficha.
        var locales = _themesDownload?.ListarDescargasLocales(animeId) ?? new List<TemaLocalDisponible>();
        return new ReferenciasEpisodio(locales, Completa: false);
    }

    /// <summary>
    /// Prueba los temas de ese tipo contra el episodio y se queda con el de mayor confianza (mínimo <see cref="ConfianzaMinima"/>).
    /// Primero los que AnimeThemes dice que aplican a este episodio; si ninguno acierta se prueban los demás (la numeración de los
    /// archivos no siempre coincide con la de AnimeThemes: temporadas seguidas, episodios sumados…).
    /// </summary>
    private async Task<AudioReferenceSkipResult?> DetectarMejorAsync(string tipo, int episodio, IReadOnlyList<TemaLocalDisponible> temas, string rutaEpisodio, CancellationToken ct)
    {
        var delTipo = temas.Where(t => string.Equals(t.Tipo, tipo, StringComparison.OrdinalIgnoreCase)).ToList();
        if (delTipo.Count == 0) return null;

        var grupos = new[]
        {
            delTipo.Where(t => t.AplicaAlEpisodio(episodio)).ToList(),
            delTipo.Where(t => !t.AplicaAlEpisodio(episodio)).ToList()
        };

        bool desdeElFinal = string.Equals(tipo, "ED", StringComparison.OrdinalIgnoreCase);
        foreach (var grupo in grupos)
        {
            AudioReferenceSkipResult? mejor = null;
            foreach (var tema in grupo)
            {
                ct.ThrowIfCancellationRequested();
                var r = await EjecutarDeteccionReferenciaAsync(rutaEpisodio, tema.RutaArchivo, desdeElFinal, ct);
                if (r != null && r.Confidence >= ConfianzaMinima && (mejor == null || r.Confidence > mejor.Confidence)) mejor = r;
            }
            if (mejor != null) return mejor;
        }
        return null;
    }

    /// <summary>(consultado, tramos): consultado es false si no se pudo saber (sin equivalencia de MAL ID o error de red).</summary>
    private async Task<(bool Consultado, List<AniSkipResult> Lista)> ConsultarAniSkipAsync(int animeId, int episodio, double duracionSegundos, CancellationToken ct)
    {
        try
        {
            var malId = await _aniSkipService!.ObtenerMalIdDesdeAniListAsync(animeId, ct);
            if (!malId.HasValue) return (false, new List<AniSkipResult>());

            var lista = await _aniSkipService.ObtenerSkipTimesAsync(malId.Value, episodio, duracionSegundos, ct);
            return (true, lista ?? new List<AniSkipResult>());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLogger.Debug("SkipTimesCoordinator", $"Error consultando AniSkip: {ex.Message}");
            return (false, new List<AniSkipResult>());
        }
    }

    private async Task<AniSkipResult?> DetectarPorComparacionAsync(string rutaVideoLocal, CancellationToken ct)
    {
        try
        {
            if (!await _pythonBridge!.IsAvailableAsync()) return null;

            string dir = Path.GetDirectoryName(rutaVideoLocal)!;
            if (!Directory.Exists(dir)) return null;
            var otroVideo = Directory.GetFiles(dir, "*.*").FirstOrDefault(f => f != rutaVideoLocal && (f.EndsWith(".mkv") || f.EndsWith(".mp4")));
            if (otroVideo == null) return null;

            var payload = new
            {
                plugin_path = RutaPluginAudioSkip.Value,
                func_name = "detect_opening",
                args = new { video_paths = new[] { rutaVideoLocal, otroVideo } }
            };

            var pluginRes = await _pythonBridge.ExecuteCommandAsync<object, PluginDaemonResponse<AudioSkipResult>>("run-plugin", payload, ct);
            if (pluginRes is { Success: true, Result.Found: true })
            {
                var r = pluginRes.Result;
                if (r.Confidence < ConfianzaMinima)
                {
                    AppLogger.Info("SkipTimesCoordinator", $"PLUGIN AUDIO: opening descartado por confianza baja [{r.IntroEstimatedStart} - {r.IntroEstimatedEnd}] (Conf: {r.Confidence:F2})");
                    return null;
                }
                AppLogger.Info("SkipTimesCoordinator", $"PLUGIN AUDIO: Opening detectado [{r.IntroEstimatedStart} - {r.IntroEstimatedEnd}] (Conf: {r.Confidence})");
                return CrearSkip("op", r.IntroEstimatedStart, r.IntroEstimatedEnd, OrigenEscenas, r.Confidence);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLogger.Debug("SkipTimesCoordinator", $"Error en plugin de audio local: {ex.Message}");
        }
        return null;
    }

    // === Análisis guardado ===

    /// <summary>"tamaño-ticksDeModificación": cambia si el archivo se reemplaza. Null si no hay archivo.</summary>
    internal static string? CalcularFirma(string? ruta)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ruta)) return null;
            var info = new FileInfo(ruta);
            return info.Exists ? $"{info.Length}-{info.LastWriteTimeUtc.Ticks}" : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<AniSkipResult>?> LeerGuardadoAsync(int animeId, int episodio, string? firma)
    {
        if (_database == null || firma == null || animeId <= 0 || episodio <= 0) return null;

        try
        {
            var analisis = await _database.ObtenerAnalisisSkipAsync(animeId, episodio);
            if (!EsAnalisisVigente(analisis, firma, DateTime.UtcNow)) return null;

            var segmentos = await _database.ObtenerSegmentosSkipAsync(animeId, episodio);
            return segmentos.Select(s => new AniSkipResult
            {
                SkipType = s.Tipo,
                Interval = new AniSkipInterval { StartTime = s.Inicio, EndTime = s.Fin },
                Origen = s.Origen,
                Confianza = s.Confianza
            }).ToList();
        }
        catch (Exception ex)
        {
            AppLogger.Debug("SkipTimesCoordinator", $"No se pudo leer el análisis guardado del episodio {episodio}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Vale si es del mismo archivo y (es completo o es reciente): un incompleto se reintenta pasadas unas horas.</summary>
    internal static bool EsAnalisisVigente(AnalisisSkipEpisodio? analisis, string firma, DateTime ahoraUtc)
    {
        if (analisis == null || analisis.Firma != firma) return false;
        if (analisis.Completo) return true;

        var fecha = analisis.FechaUtc.Kind == DateTimeKind.Local ? analisis.FechaUtc.ToUniversalTime() : DateTime.SpecifyKind(analisis.FechaUtc, DateTimeKind.Utc);
        return ahoraUtc - fecha < ReintentoDeIncompletos;
    }

    private async Task GuardarAsync(int animeId, int episodio, string? firma, bool completo, List<AniSkipResult> resultados)
    {
        if (_database == null || firma == null || animeId <= 0 || episodio <= 0) return;

        try
        {
            var analisis = new AnalisisSkipEpisodio { AnimeId = animeId, Episodio = episodio, Firma = firma, FechaUtc = DateTime.UtcNow, Completo = completo };
            var segmentos = resultados.Select(r => new SegmentoSkipGuardado
            {
                Tipo = r.SkipType,
                Inicio = r.Interval.StartTime,
                Fin = r.Interval.EndTime,
                Origen = r.Origen,
                Confianza = r.Confianza
            }).ToList();
            await _database.GuardarAnalisisSkipAsync(analisis, segmentos);
        }
        catch (Exception ex)
        {
            AppLogger.Debug("SkipTimesCoordinator", $"No se pudo guardar el análisis del episodio {episodio}: {ex.Message}");
        }
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

    private static AniSkipResult CrearSkip(string tipo, double inicio, double fin, string origen = "", double confianza = 0)
    {
        return new AniSkipResult
        {
            SkipType = tipo,
            Interval = new AniSkipInterval { StartTime = inicio, EndTime = fin },
            Origen = origen,
            Confianza = confianza
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
