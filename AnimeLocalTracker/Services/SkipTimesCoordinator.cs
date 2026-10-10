using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

public class SkipTimesCoordinator : ISkipTimesCoordinator
{
    /// <summary>
    /// Confianza mínima para aceptar una detección por audio. Con el tema correcto sale entre 0,73 y 1,0 (0,73: opening que cierra
    /// el episodio final con diálogo encima); un tema equivocado, o otro corte de la misma canción, se queda en 0,3-0,6 (medido con
    /// 25 episodios reales de 9 animes).
    /// </summary>
    internal const double ConfianzaMinima = 0.7;

    /// <summary>Un análisis incompleto (sin red, anime sin datos…) se repite pasado este tiempo; uno completo vale mientras el archivo no cambie.</summary>
    internal static readonly TimeSpan ReintentoDeIncompletos = TimeSpan.FromHours(12);

    /// <summary>
    /// Tope para la consulta a AniSkip (pasa antes por AniList para el MAL ID). Con AniList caído cada petición esperaba 30-60 s y, si
    /// el usuario cerraba el reproductor mientras tanto, se perdía también lo ya detectado por audio.
    /// </summary>
    internal TimeSpan TiempoMaximoAniSkip { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Segundos del principio del episodio donde se busca el opening (hay aperturas en frío de más de 3 minutos). Si no aparece ahí,
    /// el motor mira además hasta la mitad del episodio (episodios dobles: Sasaki to Pii-chan 2, episodio 1, lo trae a los 8:21).
    /// </summary>
    internal const double SegundosBusquedaOpening = 480;

    /// <summary>Segundos del final donde se busca el ending (ending + escena poscréditos + avance).</summary>
    internal const double SegundosBusquedaEnding = 360;

    internal const string OrigenAudio = "audio";
    internal const string OrigenAniSkip = "aniskip";

    private readonly IAniSkipService? _aniSkipService;
    private readonly IDetectorTemasAudio? _detector;
    private readonly IAnimeThemesDownloadService? _themesDownload;
    private readonly IReferenciasAudioService? _referencias;
    private readonly IDatabaseService? _database;

    /// <summary>Análisis en marcha por "anime|episodio|firma": abrir el episodio que se está pre-analizando se une a ese trabajo.</summary>
    private readonly ConcurrentDictionary<string, Task<IReadOnlyList<AniSkipResult>>> _enCurso = new();

    /// <param name="detector">Ubica los temas por audio (núcleo Rust); sin él solo queda AniSkip.</param>
    /// <param name="referencias">Consigue (y baja si falta) el audio oficial de los temas; sin él solo se usan las descargas de la Ficha.</param>
    /// <param name="database">Guarda el análisis de cada episodio (segunda vez: al instante y sin red). Sin él no hay caché.</param>
    public SkipTimesCoordinator(IAniSkipService? aniSkipService, IDetectorTemasAudio? detector = null, IAnimeThemesDownloadService? themesDownload = null,
        IReferenciasAudioService? referencias = null, IDatabaseService? database = null)
    {
        _aniSkipService = aniSkipService;
        _detector = detector;
        _themesDownload = themesDownload;
        _referencias = referencias;
        _database = database;
    }

    /// <summary>
    /// Ubica el opening, el ending y el resumen del episodio. Orden: análisis ya guardado → audio oficial de AnimeThemes (el mejor
    /// de todos los temas candidatos) → AniSkip solo para lo que falte.
    /// El resultado se guarda para no repetirlo.
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

        // 2) Ya se está analizando (pre-análisis del siguiente episodio): esperar ese resultado en vez de repetir el trabajo.
        string? clave = firma != null && animeId > 0 && episodio > 0 ? $"{animeId}|{episodio}|{firma}" : null;
        if (clave != null && _enCurso.TryGetValue(clave, out var enCurso))
        {
            try
            {
                return await enCurso.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Se canceló el análisis al que se unía (no esta carga): se hace aquí.
            }
        }

        var tarea = AnalizarAsync(animeId, episodio, duracionSegundos, rutaVideoLocal, firma, progreso, ct);
        if (clave == null) return await tarea;

        _enCurso[clave] = tarea;
        try
        {
            return await tarea;
        }
        finally
        {
            _enCurso.TryRemove(new KeyValuePair<string, Task<IReadOnlyList<AniSkipResult>>>(clave, tarea));
        }
    }

    public async Task<IReadOnlyList<AniSkipResult>> PreanalizarAsync(int animeId, int episodio, string rutaVideoLocal, CancellationToken ct)
    {
        var inicio = System.Diagnostics.Stopwatch.StartNew();
        // Sin duración: el reproductor aún no abrió el archivo (AniSkip la usa solo para afinar y funciona sin ella).
        var resultado = await CargarSkipTimesAsync(animeId, episodio, 0, rutaVideoLocal, null, ct);
        AppLogger.Debug("SkipTimesCoordinator", $"Pre-análisis del episodio {episodio}: {resultado.Count} tramo(s) en {inicio.Elapsed.TotalSeconds:F1} s.");
        return resultado;
    }

    private async Task<IReadOnlyList<AniSkipResult>> AnalizarAsync(int animeId, int episodio, double duracionSegundos, string? rutaVideoLocal, string? firma,
        IProgress<IReadOnlyList<AniSkipResult>>? progreso, CancellationToken ct)
    {
        var resultados = new List<AniSkipResult>();
        bool completo = true;
        bool motorSinRespuesta = false;

        // 2) Audio de referencia (AnimeThemes): funciona con un solo episodio local y cubre opening y ending
        bool hayVideo = !string.IsNullOrWhiteSpace(rutaVideoLocal);
        bool puedeDetectar = hayVideo && _detector is { Disponible: true } && (_referencias != null || _themesDownload != null);
        if (puedeDetectar)
        {
            var referencias = await ObtenerReferenciasAsync(animeId, episodio, ct);
            if (!referencias.Completa) completo = false;

            var deteccion = await DetectarTemasAsync(episodio, referencias.Temas, rutaVideoLocal!, ct);
            motorSinRespuesta = deteccion.MotorSinRespuesta;
            resultados.AddRange(deteccion.Tramos);
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

        // Si aún falta el opening o el ending, el análisis se repite pasadas unas horas: AniSkip recibe datos con el tiempo y
        // AnimeThemes añade temas nuevos. (Un episodio sin opening de verdad solo cuesta esa repetición.) Con los dos ubicados por audio
        // está completo aunque AnimeThemes no respondiera esta vez: no queda nada que mejorar y repetirlo cada 12 h solo gastaba CPU.
        bool ambosPorAudio = resultados.Any(r => r.EsIntro && r.Origen == OrigenAudio) && resultados.Any(r => r.EsEnding && r.Origen == OrigenAudio);
        if (ambosPorAudio) completo = true;
        else if (!resultados.Any(r => r.EsIntro) || !resultados.Any(r => r.EsEnding)) completo = false;

        // Si el motor de audio no respondió (fallo pasajero, no del archivo) no se guarda: guardarlo como incompleto dejaba el episodio sin
        // marcas 12 h aunque al volver a abrirlo el análisis ya funcionara (caso real: Katainaka no Ossan II, episodio 8).
        if (motorSinRespuesta)
            AppLogger.Info("SkipTimesCoordinator", $"Análisis del episodio {episodio} sin guardar: el motor de audio no respondió; se repetirá al volver a abrirlo.");
        else
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
    /// Ubica opening y ending con TODOS los temas en una sola pasada: el episodio se decodifica una vez y la huella de cada tema
    /// queda guardada en disco. Primero se prueban los temas que AnimeThemes dice que aplican al episodio y solo si ninguno acierta
    /// los demás (la numeración de los archivos no siempre coincide con la de AnimeThemes); para el ending también los openings (el
    /// episodio 1 y los finales suelen cerrar con él).
    /// </summary>
    private async Task<(List<AniSkipResult> Tramos, bool MotorSinRespuesta)> DetectarTemasAsync(int episodio, IReadOnlyList<TemaLocalDisponible> temas, string rutaEpisodio, CancellationToken ct)
    {
        var lista = new List<AniSkipResult>();
        var enviados = temas
            .Where(t => string.Equals(t.Tipo, "OP", StringComparison.OrdinalIgnoreCase) || string.Equals(t.Tipo, "ED", StringComparison.OrdinalIgnoreCase))
            .Select(t => new ReferenciaAudio(t.RutaArchivo, t.Tipo.ToUpperInvariant(), t.AplicaAlEpisodio(episodio) ? 0 : 1))
            .ToList();
        if (enviados.Count == 0) return (lista, false);

        var resultado = await _detector!.DetectarAsync(rutaEpisodio, enviados, ConfianzaMinima, SegundosBusquedaOpening, SegundosBusquedaEnding, ct);
        if (resultado is not { Success: true })
        {
            // Antes un fallo del motor (ffmpeg, archivo ilegible…) se tragaba en silencio y parecía "no hay opening".
            // Un error concreto viene con texto; sin él (null), el motor no pudo trabajar y el análisis no se guarda.
            string? motivo = resultado?.Error;
            AppLogger.Warn("SkipTimesCoordinator", $"No se pudo analizar el audio del episodio {episodio}: {motivo ?? "el motor de audio no respondió"}");
            return (lista, motivo == null);
        }

        foreach (var tramo in new[] { "op", "ed" })
        {
            var mejor = resultado.Matches
                .Where(m => string.Equals(m.Segment, tramo, StringComparison.OrdinalIgnoreCase) && m.Confidence >= ConfianzaMinima && m.End > m.Start)
                .OrderByDescending(m => m.Confidence)
                .FirstOrDefault();
            if (mejor == null) continue;

            lista.Add(CrearSkip(tramo, mejor.Start, mejor.End, OrigenAudio, mejor.Confidence));
            string parcial = mejor.Mode == "partial" ? ", parcial" : "";
            AppLogger.Info("SkipTimesCoordinator", $"REFERENCIA ANIMETHEMES: {tramo.ToUpperInvariant()} detectado [{mejor.Start:F1} - {mejor.End:F1}] (Conf: {mejor.Confidence:F2}{parcial}) con '{Path.GetFileName(mejor.ReferencePath)}'");
        }
        AppLogger.Debug("SkipTimesCoordinator", $"Audio de referencia del episodio {episodio}: {resultado.Evaluated} de {enviados.Count} tema(s) comparados en {resultado.Seconds:F1} s.");
        return (lista, false);
    }

    /// <summary>(consultado, tramos): consultado es false si no se pudo saber (sin equivalencia de MAL ID o error de red).</summary>
    private async Task<(bool Consultado, List<AniSkipResult> Lista)> ConsultarAniSkipAsync(int animeId, int episodio, double duracionSegundos, CancellationToken ct)
    {
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(TiempoMaximoAniSkip);
        try
        {
            var malId = await _aniSkipService!.ObtenerMalIdDesdeAniListAsync(animeId, limite.Token);
            if (!malId.HasValue) return (false, new List<AniSkipResult>());

            var lista = await _aniSkipService.ObtenerSkipTimesAsync(malId.Value, episodio, duracionSegundos, limite.Token);
            return (!limite.IsCancellationRequested, lista ?? new List<AniSkipResult>());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            AppLogger.Debug("SkipTimesCoordinator", $"AniSkip no respondió en {TiempoMaximoAniSkip.TotalSeconds:F0} s; se sigue sin sus datos.");
            return (false, new List<AniSkipResult>());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLogger.Debug("SkipTimesCoordinator", $"Error consultando AniSkip: {ex.Message}");
            return (false, new List<AniSkipResult>());
        }
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
            // La fecha de la música solo importa si el análisis quedó incompleto (y se mira solo entonces: lista archivos).
            DateTime? referenciaMasNueva = analisis is { Completo: false } ? ReferenciaMasNueva(animeId) : null;
            if (!EsAnalisisVigente(analisis, firma, DateTime.UtcNow, referenciaMasNueva)) return null;

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

    /// <summary>
    /// Vale si es del mismo archivo y (es completo o es reciente): un incompleto se reintenta pasadas unas horas, o en cuanto aparece
    /// audio de referencia más nuevo que el análisis (<paramref name="referenciaMasNuevaUtc"/>). Caso real: sin conexión se guardó un
    /// análisis vacío y, aunque el usuario bajó el opening y el ending desde la Ficha poco después, el episodio siguió sin marcas 12 h.
    /// </summary>
    internal static bool EsAnalisisVigente(AnalisisSkipEpisodio? analisis, string firma, DateTime ahoraUtc, DateTime? referenciaMasNuevaUtc = null)
    {
        if (analisis == null || analisis.Firma != firma) return false;
        if (analisis.Completo) return true;

        var fecha = analisis.FechaUtc.Kind == DateTimeKind.Local ? analisis.FechaUtc.ToUniversalTime() : DateTime.SpecifyKind(analisis.FechaUtc, DateTimeKind.Utc);
        if (referenciaMasNuevaUtc is DateTime nueva && nueva > fecha) return false;
        return ahoraUtc - fecha < ReintentoDeIncompletos;
    }

    private DateTime? ReferenciaMasNueva(int animeId)
    {
        try
        {
            return _referencias?.ReferenciaMasNuevaUtc(animeId);
        }
        catch (Exception ex)
        {
            AppLogger.Debug("SkipTimesCoordinator", $"No se pudo revisar la música de {animeId}: {ex.Message}");
            return null;
        }
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

    /// <summary>Resultado de <see cref="IDetectorTemasAudio.DetectarAsync"/>.</summary>
    public class DeteccionTemasResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public List<TemaDetectado> Matches { get; set; } = new();
        /// <summary>Temas que llegaron a compararse (los que no aplican solo se prueban si ninguno de los que aplican acierta).</summary>
        public int Evaluated { get; set; }
        public double Seconds { get; set; }
    }

    public class TemaDetectado
    {
        /// <summary>"op" o "ed": dónde suena en el episodio (un opening que cierra el episodio sale como "ed").</summary>
        public string Segment { get; set; } = "";
        public string? ReferencePath { get; set; }
        public double Start { get; set; }
        public double End { get; set; }
        public double Confidence { get; set; }
        /// <summary>"full" (el tema entero) o "partial" (el episodio usa solo una parte).</summary>
        public string? Mode { get; set; }
    }
}
