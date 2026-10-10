using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AniSkipModels = AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services.Python;

/// <summary>
/// Enriquece episodios locales con metadata técnica (ffprobe) y miniaturas (ffmpeg), lanzados directamente,
/// y análisis de duplicados (perceptual hash), este con el bridge Python (daemon persistente) de respaldo.
/// </summary>
public class PythonEpisodeEnricher
{
    private readonly IPythonBridgeService _pythonBridge;

    public PythonEpisodeEnricher(IPythonBridgeService pythonBridge)
    {
        _pythonBridge = pythonBridge;
    }

    private static readonly TimeSpan TiempoMaximoFfprobe = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Obtiene metadata técnica de un video y la aplica al EpisodioItem. Llama a ffprobe directamente (sin el daemon Python):
    /// así no espera detrás de un análisis de opening ni depende de que el daemon haya arrancado.
    /// </summary>
    public async Task EnriquecerEpisodioAsync(AniSkipModels.EpisodioItem episodio, CancellationToken ct = default)
    {
        // File.Exists descarta también las URLs: ffprobe las abriría.
        if (episodio == null || string.IsNullOrWhiteSpace(episodio.RutaCompleta) || !File.Exists(episodio.RutaCompleta)) return;

        try
        {
            // -protocol_whitelist file: un archivo que en realidad sea una lista (HLS, concat) no puede hacer que ffprobe salga a
            // la red. -max_alloc: tope de 2 GB por reserva de memoria ante una cabecera malformada.
            string[] argumentos =
            [
                "-protocol_whitelist", "file", "-max_alloc", "2147483648", "-v", "error", "-select_streams", "v:0",
                "-show_entries", "stream=codec_name,width,height,pix_fmt,r_frame_rate", "-of", "json", episodio.RutaCompleta
            ];
            var resultado = await Core.ProcesoExterno.EjecutarAsync(FfmpegLocator.Ffprobe, argumentos, TiempoMaximoFfprobe, ct);
            if (resultado is not { Codigo: 0 } || LeerDatosTecnicos(resultado.Salida) is not { } datos) return;

            episodio.Resolucion = datos.Resolucion;
            episodio.CodecVideo = datos.CodecVideo;
            episodio.Fps = datos.Fps;
            episodio.Es10Bit = datos.Es10Bit;
        }
        catch (Exception ex)
        {
            AppLogger.Debug("PythonEpisodeEnricher", $"Error inspeccionando {episodio.TituloArchivo}: {ex.Message}");
        }
    }

    internal sealed record DatosTecnicos(string Resolucion, string CodecVideo, string Fps, bool Es10Bit);

    /// <summary>Salida JSON de ffprobe → datos de la primera pista de video. Null si no hay pista de video o no se entiende.</summary>
    internal static DatosTecnicos? LeerDatosTecnicos(string salidaFfprobe)
    {
        try
        {
            var video = JsonSerializer.Deserialize<SalidaFfprobe>(salidaFfprobe)?.Streams?.FirstOrDefault();
            if (video == null) return null;

            return new DatosTecnicos(
                video.Width > 0 && video.Height > 0 ? $"{video.Width}x{video.Height}" : string.Empty,
                video.CodecName ?? string.Empty,
                video.RFrameRate ?? string.Empty,
                video.PixFmt?.Contains("10") == true);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Carpeta donde se guardan las miniaturas. Solo la cambian las pruebas (a una temporal, ver TestInitializer): con
    /// la real dejaban miniaturas falsas entre los datos del usuario, una por cada corrida de la suite.</summary>
    internal static string CarpetaMiniaturas { get; set; } = AppDataPaths.ThumbnailsDir;

    /// <summary>
    /// Calcula la ruta esperada de la miniatura de forma determinista y persistente (SHA-256 de la ruta).
    /// </summary>
    public static string ObtenerRutaMiniaturaEsperada(string rutaCompleta)
    {
        if (string.IsNullOrWhiteSpace(rutaCompleta)) return string.Empty;
        var thumbsDir = CarpetaMiniaturas;

        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rutaCompleta.ToLowerInvariant()));
        string hex = Convert.ToHexString(hash).ToLowerInvariant();
        return Path.Combine(thumbsDir, $"{hex}.jpg");
    }

    /// <summary>
    /// BUG-02: un proceso ffmpeg interrumpido (Rust o Python) puede dejar un JPEG truncado
    /// que NO está vacío (unos cientos/miles de bytes) pero tampoco es una imagen completa —
    /// comprobar solo "Length == 0"/"Length > 0" dejaba esa miniatura rota marcada como
    /// "ya generada" para siempre: ni un reintento en segundo plano ni pulsar "Actualizar"
    /// la regeneraban jamás. Validar los marcadores JPEG (SOI al inicio, EOI al final) detecta
    /// el caso típico de escritura cortada sin decodificar la imagen completa.
    /// </summary>
    public static bool EsMiniaturaValida(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var info = new FileInfo(path);
            // Una miniatura real (320px) nunca pesa unos pocos cientos de bytes.
            if (info.Length < 512) return false;

            using var stream = File.OpenRead(path);
            Span<byte> inicio = stackalloc byte[3];
            if (stream.Read(inicio) != 3) return false;
            if (inicio[0] != 0xFF || inicio[1] != 0xD8 || inicio[2] != 0xFF) return false;

            stream.Seek(-2, SeekOrigin.End);
            Span<byte> fin = stackalloc byte[2];
            if (stream.Read(fin) != 2) return false;
            return fin[0] == 0xFF && fin[1] == 0xD9;
        }
        catch
        {
            return false;
        }
    }

    // BUG-03: muchos rips de fansub tienen una cortinilla/watermark casi negra en los
    // primeros segundos (bumper de intro). Un frame extraído ahí es un JPEG técnicamente
    // válido pero visualmente inútil (se ve igual que "sin miniatura"). Un frame real de
    // 320px de ancho jamás comprime tan pequeño — se usa como señal barata de "frame casi
    // vacío" sin necesitar decodificar la imagen.
    private const long TamanoMinimoContenidoReal = 4096;
    private static readonly double[] TimestampsCandidatos = { 2.0, 10.0, 30.0 };

    public static bool EsFrameDemasiadoVacio(string path)
    {
        try { return new FileInfo(path).Length < TamanoMinimoContenidoReal; }
        catch { return true; }
    }

    /// <summary>
    /// Devuelve la ruta de la miniatura si ya existe en disco, NO está corrupta/truncada y
    /// tiene contenido real (no un frame casi negro de una cortinilla), de lo contrario null.
    /// Una miniatura corrupta o casi vacía se borra para que se regenere en la próxima pasada.
    /// </summary>
    public static string? ObtenerRutaMiniaturaSiExiste(string rutaCompleta)
    {
        if (string.IsNullOrWhiteSpace(rutaCompleta)) return null;
        string path = ObtenerRutaMiniaturaEsperada(rutaCompleta);

        if (!File.Exists(path)) return null;
        if (!EsMiniaturaValida(path) || EsFrameDemasiadoVacio(path))
        {
            try { File.Delete(path); } catch { }
            return null;
        }
        return path;
    }

    private static readonly TimeSpan TiempoMaximoFfmpeg = TimeSpan.FromSeconds(60);

    /// <summary>Ancho de las miniaturas. 512 y no 320: es el que ya tienen las guardadas (el núcleo Rust, que las hacía antes,
    /// redondeaba el 320 pedido a la potencia de 2 siguiente) y con el que se midió <see cref="TamanoMinimoContenidoReal"/>.</summary>
    private const int AnchoMiniatura = 512;

    /// <summary>
    /// Extrae la miniatura del episodio probando varios timestamps si el primero cae en
    /// un frame casi negro/vacío (ver <see cref="TimestampsCandidatos"/>). Se queda con
    /// el primer frame que ya no parezca vacío; si ninguno lo logra, conserva el último
    /// intento válido (mejor una miniatura "pobre" que ninguna). Devuelve true si quedó
    /// alguna miniatura utilizable en <paramref name="outPath"/>.
    /// Es el único camino para sacar un fotograma: ffmpeg lanzado desde aquí (antes, Rust lanzaba ffmpeg y, si fallaba,
    /// el daemon Python volvía a lanzarlo).
    /// </summary>
    public static async Task<bool> ExtraerMiniaturaAsync(string rutaVideo, string outPath, CancellationToken ct = default)
    {
        // File.Exists descarta también las URLs: ffmpeg las abriría.
        if (string.IsNullOrWhiteSpace(rutaVideo) || string.IsNullOrWhiteSpace(outPath) || !File.Exists(rutaVideo)) return false;

        try
        {
            // Ya hay una miniatura válida y con contenido real: nada que hacer.
            if (EsMiniaturaValida(outPath) && !EsFrameDemasiadoVacio(outPath)) return true;

            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

            bool huboExito = false;
            foreach (var timestamp in TimestampsCandidatos)
            {
                if (!await ExtraerFotogramaAsync(rutaVideo, outPath, timestamp, ct)) continue;

                huboExito = true;
                if (!EsFrameDemasiadoVacio(outPath)) return true;
                // Frame válido pero casi vacío: se conserva por si es el mejor que
                // consigamos, y se prueba el siguiente timestamp por si hay algo mejor.
            }

            if (!huboExito) LimpiarMiniaturaCorrupta(rutaVideo);
            return huboExito;
        }
        catch (Exception ex)
        {
            AppLogger.Debug("PythonEpisodeEnricher", $"Error extrayendo miniatura de {rutaVideo}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Un fotograma del video a <paramref name="outPath"/>. ffmpeg escribe en un archivo aparte que solo sustituye al destino
    /// si salió un JPEG completo: un intento fallido (instante más allá del final, proceso cortado) no deja una miniatura a
    /// medias (BUG-02) ni se lleva por delante la que ya había de un instante anterior.
    /// </summary>
    private static async Task<bool> ExtraerFotogramaAsync(string rutaVideo, string outPath, double segundo, CancellationToken ct)
    {
        string temporal = $"{outPath}.{Guid.NewGuid():N}.jpg";
        try
        {
            // -protocol_whitelist file: un archivo que en realidad sea una lista (HLS, concat) no puede hacer que ffmpeg salga a
            // la red. -max_alloc: tope de 2 GB por reserva de memoria ante una cabecera malformada.
            string[] argumentos =
            [
                "-y", "-nostdin", "-loglevel", "error", "-max_alloc", "2147483648", "-protocol_whitelist", "file",
                "-ss", segundo.ToString("0.##", CultureInfo.InvariantCulture), "-i", rutaVideo,
                "-an", "-sn", "-dn", "-frames:v", "1", "-vf", $"scale={AnchoMiniatura}:-2", "-q:v", "3", temporal
            ];
            var resultado = await Core.ProcesoExterno.EjecutarAsync(FfmpegLocator.Ffmpeg, argumentos, TiempoMaximoFfmpeg, ct,
                ProcessPriorityClass.BelowNormal);
            if (resultado is not { Codigo: 0 } || !EsMiniaturaValida(temporal)) return false;

            File.Move(temporal, outPath, overwrite: true);
            return true;
        }
        finally
        {
            try { File.Delete(temporal); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Elimina miniaturas corruptas/truncadas de una ruta de video, dejando la caché limpia
    /// para que la próxima pasada las regenere.
    /// </summary>
    public static void LimpiarMiniaturaCorrupta(string rutaCompleta)
    {
        if (string.IsNullOrWhiteSpace(rutaCompleta)) return;
        try
        {
            string thumbPath = ObtenerRutaMiniaturaEsperada(rutaCompleta);
            if (File.Exists(thumbPath) && !EsMiniaturaValida(thumbPath))
            {
                File.Delete(thumbPath);
            }
        }
        catch { }
    }

    /// <summary>
    /// Analiza duplicados entre los episodios de un anime (perceptual hash).
    /// Devuelve las rutas duplicadas para informar al usuario.
    /// </summary>
    public async Task<List<string>> EncontrarDuplicadosAsync(IEnumerable<AniSkipModels.EpisodioItem> episodios, CancellationToken ct = default)
    {
        var rutas = episodios
            .Where(e => !string.IsNullOrWhiteSpace(e.RutaCompleta) && File.Exists(e.RutaCompleta))
            .Select(e => e.RutaCompleta)
            .ToList();

        if (rutas.Count < 2) return [];

        // 1. Detección ultrarrápida en Rust FFI (muestreo SIMD a velocidad de disco NVMe)
        if (Native.NativeMethods.IsAvailable)
        {
            try
            {
                var fingerprints = new System.Collections.Concurrent.ConcurrentDictionary<string, List<string>>();
                Parallel.ForEach(rutas, ruta =>
                {
                    var fp = Native.NativeMethods.ComputeFingerprint(ruta);
                    if (fp != null && fp.Success && !string.IsNullOrEmpty(fp.Fingerprint))
                    {
                        string key = $"{fp.Fingerprint}_{fp.FileSize}";
                        fingerprints.AddOrUpdate(
                            key,
                            _ => new List<string> { ruta },
                            (_, list) => { lock (list) { list.Add(ruta); } return list; });
                    }
                });

                var duplicadosNativos = new List<string>();
                foreach (var list in fingerprints.Values)
                {
                    if (list.Count > 1)
                    {
                        duplicadosNativos.AddRange(list.Skip(1));
                    }
                }

                if (duplicadosNativos.Count > 0 || fingerprints.Count == rutas.Count)
                {
                    return duplicadosNativos;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug("PythonEpisodeEnricher", $"Fallo en fingerprint nativo Rust: {ex.Message}");
            }
        }

        // 2. Fallback a Python Bridge (perceptual hashing)
        try
        {
            var result = await _pythonBridge.ExecuteCommandAsync<object, DuplicatesResult>(
                "find-duplicates",
                new { video_paths = rutas, max_distance = 8 },
                ct);

            if (result == null || !result.Success || result.Duplicados == null) return [];

            // Aplastar grupos: todos los duplicados (menos el primero = original)
            var duplicados = new List<string>();
            foreach (var grupo in result.Duplicados)
            {
                if (grupo.Items.Count > 1)
                    duplicados.AddRange(grupo.Items.Skip(1));
            }
            return duplicados;
        }
        catch (Exception ex)
        {
            AppLogger.Debug("PythonEpisodeEnricher", $"Error analizando duplicados: {ex.Message}");
            return [];
        }
    }

    // ── Salida de ffprobe (-of json) ──
    private sealed class SalidaFfprobe
    {
        [JsonPropertyName("streams")] public List<PistaFfprobe>? Streams { get; set; }
    }

    private sealed class PistaFfprobe
    {
        [JsonPropertyName("codec_name")] public string? CodecName { get; set; }
        [JsonPropertyName("width")] public int Width { get; set; }
        [JsonPropertyName("height")] public int Height { get; set; }
        [JsonPropertyName("pix_fmt")] public string? PixFmt { get; set; }
        [JsonPropertyName("r_frame_rate")] public string? RFrameRate { get; set; }
    }

    // ── Modelos de respuesta JSON (snake_case del CLI) ──

    private class DuplicatesResult
    {
        public bool Success { get; set; }
        public List<DuplicateGroup>? Duplicados { get; set; }
    }

    private class DuplicateGroup
    {
        public List<string> Items { get; set; } = new();
    }
}
