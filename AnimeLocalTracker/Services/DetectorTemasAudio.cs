using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Services.Native;

namespace AnimeLocalTracker.Services;

/// <summary>Audio oficial de un tema. <paramref name="Tipo"/>: "OP" o "ED". <paramref name="Prioridad"/>: 0 = aplica al episodio según AnimeThemes, 1 = resto.</summary>
public sealed record ReferenciaAudio(string Ruta, string Tipo, int Prioridad);

public interface IDetectorTemasAudio
{
    /// <summary>El núcleo nativo que hace el cálculo está cargado.</summary>
    bool Disponible { get; }

    /// <summary>
    /// Ubica el opening (al inicio) y el ending (al final) de un episodio comparándolo con los temas oficiales.
    /// Null = el motor no pudo trabajar (no es un dato sobre el episodio). Con Success = false, Error dice por qué.
    /// </summary>
    Task<SkipTimesCoordinator.DeteccionTemasResult?> DetectarAsync(string rutaEpisodio, IReadOnlyList<ReferenciaAudio> referencias,
        double confianzaMinima, double segundosInicio, double segundosFinal, CancellationToken ct);
}

/// <summary>
/// Dirige la detección: ffmpeg decodifica (aquí, con <see cref="Core.ProcesoExterno"/>), el núcleo Rust calcula huellas y
/// parecidos, y esta clase decide el orden. Por cada tramo se prueban grupos de temas y se para en el primero que acierta (así
/// los temas que no aplican ni se decodifican si el que aplica ya coincide):
///   opening → OP que aplican, OP restantes; si no aparece al principio, otra vez hasta la mitad del episodio.
///   ending  → ED que aplican, ED restantes y, por último, los OP: el episodio 1 y los finales suelen cerrar con el opening.
/// </summary>
public sealed class DetectorTemasAudio : IDetectorTemasAudio
{
    private const int Fps = NativeMethods.FotogramasPorSegundo;
    private const int Columnas = NativeMethods.ColumnasHuella;
    /// <summary>
    /// El opening se busca como mucho hasta esta fracción del episodio. Más allá el mismo tema suele sonar como canción de fondo
    /// del clímax (episodios finales): marcarlo haría que "saltar opening" se llevara la escena.
    /// </summary>
    private const double LimiteOpening = 0.5;
    private const int TemasALaVez = 4;
    /// <summary>Menos de 5 s de tema: archivo roto o vacío.</summary>
    private const int FotogramasMinimosDeTema = 5 * Fps;
    private static readonly TimeSpan TiempoMaximoFfmpeg = TimeSpan.FromMinutes(5);

    private readonly CacheHuellasAudio _cache;
    private readonly Func<string, CancellationToken, Task<double>> _duracion;
    private readonly Func<string, double, double?, CancellationToken, Task<float[]?>> _huellaDeTramo;
    private readonly Func<string, CancellationToken, Task<float[]?>> _huellaDeTema;

    /// <param name="carpetaHuellas">Dónde se guarda la huella de cada tema; por defecto <see cref="AppDataPaths.AudioFingerprintsDir"/>.</param>
    public DetectorTemasAudio(string? carpetaHuellas = null)
        : this(carpetaHuellas ?? AppDataPaths.AudioFingerprintsDir, DuracionConFfprobeAsync, HuellaConFfmpegAsync, null)
    {
    }

    /// <summary>Para pruebas: de dónde salen la duración, la huella de un tramo de un archivo (desde, duración o null = hasta el final) y,
    /// si se da, la huella ya lista de un tema (sin pasar por la caché).</summary>
    internal DetectorTemasAudio(string carpetaHuellas, Func<string, CancellationToken, Task<double>> duracion,
        Func<string, double, double?, CancellationToken, Task<float[]?>> huellaDeTramo, Func<string, CancellationToken, Task<float[]?>>? huellaDeTema)
    {
        _cache = new CacheHuellasAudio(carpetaHuellas);
        _duracion = duracion;
        _huellaDeTramo = huellaDeTramo;
        _huellaDeTema = huellaDeTema ?? HuellaDeTemaConCacheAsync;
    }

    public bool Disponible => NativeMethods.IsAvailable;

    private sealed record Ventana(double Inicio, float[] Huella);

    /// <summary>El núcleo nativo falló: no es un dato sobre el episodio y no debe guardarse como tal.</summary>
    private sealed class ErrorDelMotor : Exception;

    /// <summary>
    /// Todo el trabajo (esperar a ffmpeg y calcular huellas, decenas de ms de procesador) va al grupo de hilos: quien llama es
    /// el reproductor desde el hilo de la interfaz, y seguir ahí tras cada espera daría un tirón al video al abrir el episodio.
    /// </summary>
    public Task<SkipTimesCoordinator.DeteccionTemasResult?> DetectarAsync(string rutaEpisodio, IReadOnlyList<ReferenciaAudio> referencias,
        double confianzaMinima, double segundosInicio, double segundosFinal, CancellationToken ct) =>
        Task.Run(() => DetectarEnSegundoPlanoAsync(rutaEpisodio, referencias, confianzaMinima, segundosInicio, segundosFinal, ct), ct);

    private async Task<SkipTimesCoordinator.DeteccionTemasResult?> DetectarEnSegundoPlanoAsync(string rutaEpisodio, IReadOnlyList<ReferenciaAudio> referencias,
        double confianzaMinima, double segundosInicio, double segundosFinal, CancellationToken ct)
    {
        var reloj = Stopwatch.StartNew();
        if (!Disponible) return null;
        // File.Exists descarta también las URL: ffmpeg las abriría.
        if (string.IsNullOrWhiteSpace(rutaEpisodio) || !File.Exists(rutaEpisodio)) return Fallo("El episodio no existe.");

        var temas = referencias.Where(r => !string.IsNullOrWhiteSpace(r.Ruta) && File.Exists(r.Ruta)).ToList();
        if (temas.Count == 0) return new SkipTimesCoordinator.DeteccionTemasResult { Success = true };

        try
        {
            double duracion = await _duracion(rutaEpisodio, ct);
            if (!(duracion > 0)) return Fallo("No se pudo obtener la duración del episodio (ffprobe).");

            var (inicio, final) = await VentanasDelEpisodioAsync(rutaEpisodio, duracion, segundosInicio, segundosFinal, ct);
            if (inicio == null || final == null) return Fallo("No se pudo extraer el audio del episodio.");

            var huellas = new Dictionary<string, float[]?>();
            var evaluados = new HashSet<string>();
            List<string> Grupo(string tipo, int prioridad) =>
                temas.Where(r => string.Equals(r.Tipo, tipo, StringComparison.OrdinalIgnoreCase) && r.Prioridad == prioridad).Select(r => r.Ruta).ToList();

            var resultado = new SkipTimesCoordinator.DeteccionTemasResult { Success = true };
            List<string>[] gruposOpening = [Grupo("OP", 0), Grupo("OP", 1)];
            var opening = await BuscarEnGruposAsync(inicio, gruposOpening, huellas, evaluados, confianzaMinima, null, ct);
            if (opening == null)
            {
                // No está en los primeros minutos: un episodio doble o con una apertura en frío larga lo trae más tarde (Sasaki to
                // Pii-chan 2, episodio 1 de 47 min: suena a los 8:21). Se mira hasta la mitad del episodio, y solo ahora, para no
                // decodificar de más en el caso normal. El tramo empieza un tema antes del límite: cubre el que cae a caballo.
                double largoTema = gruposOpening.SelectMany(g => g).Select(r => huellas.GetValueOrDefault(r)?.Length ?? 0).DefaultIfEmpty(0).Max()
                                   / (double)Columnas / Fps;
                double hasta = duracion * LimiteOpening;
                if (largoTema > 0 && hasta > segundosInicio)
                {
                    double desde = Math.Max(0.0, segundosInicio - largoTema);
                    var tardia = await _huellaDeTramo(rutaEpisodio, desde, hasta - desde, ct);
                    if (tardia is { Length: >= Columnas })
                    {
                        opening = await BuscarEnGruposAsync(new Ventana(desde, tardia), gruposOpening, huellas, evaluados, confianzaMinima, null, ct);
                    }
                }
            }
            if (opening is { } op) resultado.Matches.Add(ATema("op", op.Ruta, op.Coincidencia));

            (double, double)? excluir = opening is { } hallado ? (hallado.Coincidencia.Inicio, hallado.Coincidencia.Fin) : null;
            List<string>[] gruposEnding = [Grupo("ED", 0), Grupo("ED", 1), Grupo("OP", 0), Grupo("OP", 1)];
            var ending = await BuscarEnGruposAsync(final, gruposEnding, huellas, evaluados, confianzaMinima, excluir, ct);
            if (ending is { } ed) resultado.Matches.Add(ATema("ed", ed.Ruta, ed.Coincidencia));

            resultado.Evaluated = evaluados.Count;
            resultado.Seconds = Math.Round(reloj.Elapsed.TotalSeconds, 3);
            return resultado;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Fallo("ffmpeg tardó demasiado en decodificar el audio.");
        }
        catch (ErrorDelMotor)
        {
            return null;
        }
        catch (Exception ex)
        {
            return Fallo(ex.Message);
        }
    }

    private static SkipTimesCoordinator.DeteccionTemasResult Fallo(string motivo) => new() { Success = false, Error = motivo };

    private static SkipTimesCoordinator.TemaDetectado ATema(string tramo, string ruta, CoincidenciaAudio c) => new()
    {
        Segment = tramo,
        ReferencePath = ruta,
        Start = c.Inicio,
        End = c.Fin,
        Confidence = c.Confianza,
        Mode = c.Parcial != 0 ? "partial" : "full"
    };

    /// <summary>
    /// Tramos de inicio y de final del episodio. Las dos decodificaciones van a la vez; en episodios cortos los tramos se
    /// solapan y se decodifica el archivo entero una sola vez.
    /// </summary>
    private async Task<(Ventana? Inicio, Ventana? Final)> VentanasDelEpisodioAsync(string ruta, double duracion, double segundosInicio, double segundosFinal, CancellationToken ct)
    {
        double largoInicio = Math.Min(segundosInicio, duracion);
        double comienzoFinal = Math.Max(0.0, duracion - segundosFinal);

        if (comienzoFinal <= largoInicio)
        {
            var entera = await _huellaDeTramo(ruta, 0.0, null, ct);
            if (entera is not { Length: >= Columnas }) return (null, null);

            int total = entera.Length / Columnas;
            int corte = Math.Min(total, (int)(comienzoFinal * Fps));
            int finInicio = Math.Min(total, (int)(largoInicio * Fps));
            if (finInicio == 0 || corte >= total) return (null, null);
            return (new Ventana(0.0, entera.AsSpan(0, finInicio * Columnas).ToArray()),
                    new Ventana(corte / (double)Fps, entera.AsSpan(corte * Columnas).ToArray()));
        }

        var tareaInicio = _huellaDeTramo(ruta, 0.0, largoInicio, ct);
        var tareaFinal = _huellaDeTramo(ruta, comienzoFinal, segundosFinal, ct);
        await Task.WhenAll(tareaInicio, tareaFinal);
        var huellaInicio = await tareaInicio;
        var huellaFinal = await tareaFinal;
        if (huellaInicio is not { Length: >= Columnas } || huellaFinal is not { Length: >= Columnas }) return (null, null);
        return (new Ventana(0.0, huellaInicio), new Ventana(comienzoFinal, huellaFinal));
    }

    private async Task<(string Ruta, CoincidenciaAudio Coincidencia)?> BuscarEnGruposAsync(Ventana ventana, IEnumerable<List<string>> grupos,
        Dictionary<string, float[]?> huellas, HashSet<string> evaluados, double confianzaMinima, (double, double)? excluir, CancellationToken ct)
    {
        foreach (var rutas in grupos)
        {
            var pendientes = rutas.Where(r => !huellas.ContainsKey(r)).Distinct().ToList();
            if (pendientes.Count > 0)
            {
                var nuevas = new ConcurrentDictionary<string, float[]?>();
                await Parallel.ForEachAsync(pendientes, new ParallelOptions { MaxDegreeOfParallelism = TemasALaVez, CancellationToken = ct },
                    async (ruta, corte) => nuevas[ruta] = await _huellaDeTema(ruta, corte));
                foreach (var (ruta, huella) in nuevas) huellas[ruta] = huella;
            }

            var candidatos = rutas.Where(r => huellas.GetValueOrDefault(r) != null).Distinct().ToList();
            if (candidatos.Count == 0) continue;
            evaluados.UnionWith(candidatos);

            if (!NativeMethods.MejorCoincidencia(ventana.Huella, ventana.Inicio, candidatos.Select(r => huellas[r]!).ToList(), excluir, out var mejor))
            {
                throw new ErrorDelMotor();
            }
            if (mejor is { } m && m.Confianza >= confianzaMinima) return (candidatos[m.Indice], m);
        }
        return null;
    }

    /// <summary>Huella del tema (sin silencios en los extremos), guardada en disco: la segunda vez no se decodifica. Null si no sirve.</summary>
    private async Task<float[]?> HuellaDeTemaConCacheAsync(string ruta, CancellationToken ct)
    {
        string clave;
        try
        {
            clave = CacheHuellasAudio.Clave(ruta);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        float[]? huella = _cache.Leer(clave);
        if (huella == null)
        {
            var cruda = await _huellaDeTramo(ruta, 0.0, null, ct);
            if (cruda == null) return null;

            huella = RecortarSilencio(cruda);
            if (huella.Length > 0) _cache.Guardar(clave, huella);
        }
        return huella.Length >= FotogramasMinimosDeTema * Columnas ? huella : null;
    }

    /// <summary>Quita el silencio del principio y del final del tema: el tramo detectado empieza y acaba donde suena la música.</summary>
    internal static float[] RecortarSilencio(float[] huella)
    {
        int total = huella.Length / Columnas;
        float maximo = 0;
        for (int f = 0; f < total; f++) maximo = Math.Max(maximo, huella[f * Columnas]);
        float umbral = Math.Max(maximo * 0.02f, 1e-4f);

        int primero = -1, ultimo = -1;
        for (int f = 0; f < total; f++)
        {
            if (huella[f * Columnas] <= umbral) continue;
            if (primero < 0) primero = f;
            ultimo = f;
        }
        return primero < 0 ? [] : huella.AsSpan(primero * Columnas, (ultimo - primero + 1) * Columnas).ToArray();
    }

    private static async Task<double> DuracionConFfprobeAsync(string ruta, CancellationToken ct)
    {
        string[] argumentos = ["-protocol_whitelist", "file", "-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", ruta];
        var resultado = await Core.ProcesoExterno.EjecutarAsync(FfmpegLocator.Ffprobe, argumentos, TimeSpan.FromSeconds(30), ct);
        return resultado is { Codigo: 0 } && double.TryParse(resultado.Salida.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double segundos)
            ? segundos
            : 0.0;
    }

    /// <summary>Audio mono a 8 kHz de un tramo del archivo, ya convertido en huella. Null si ffmpeg falla o no hay audio.</summary>
    private static async Task<float[]?> HuellaConFfmpegAsync(string ruta, double desde, double? duracion, CancellationToken ct)
    {
        // -protocol_whitelist file: un archivo que en realidad sea una lista (HLS, concat) no puede hacer que ffmpeg salga a la
        // red. -max_alloc: tope de 2 GB por reserva de memoria ante una cabecera malformada.
        var argumentos = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-max_alloc", "2147483648", "-protocol_whitelist", "file" };
        if (desde > 0)
        {
            argumentos.Add("-ss");
            argumentos.Add(desde.ToString("0.000", CultureInfo.InvariantCulture));
        }
        argumentos.AddRange(["-i", ruta, "-vn", "-sn", "-dn"]);
        if (duracion is { } segundos)
        {
            argumentos.Add("-t");
            argumentos.Add(segundos.ToString("0.000", CultureInfo.InvariantCulture));
        }
        argumentos.AddRange(["-ac", "1", "-ar", "8000", "-f", "f32le", "-"]);

        var resultado = await Core.ProcesoExterno.EjecutarBinarioAsync(FfmpegLocator.Ffmpeg, argumentos, TiempoMaximoFfmpeg, ct);
        if (resultado is not { Codigo: 0 } || resultado.Salida.Length < sizeof(float)) return null;

        return HuellaDePcm(resultado.Salida);
    }

    /// <summary>Bytes de audio f32 (mono, 8 kHz) → huella. Aparte porque un método asíncrono no puede tener un Span como variable.</summary>
    private static float[] HuellaDePcm(byte[] bytes)
    {
        var pcm = MemoryMarshal.Cast<byte, float>(bytes.AsSpan(0, bytes.Length / sizeof(float) * sizeof(float)));
        return NativeMethods.HuellaDeAudio(pcm) ?? throw new ErrorDelMotor();
    }
}
