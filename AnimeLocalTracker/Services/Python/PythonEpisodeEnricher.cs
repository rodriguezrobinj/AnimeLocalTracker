using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AniSkipModels = AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services.Python;

/// <summary>
/// Enriquece episodios locales con metadata técnica (ffprobe), miniaturas (ffmpeg)
/// y análisis de duplicados (perceptual hash) — todo vía el bridge Python (daemon persistente).
/// </summary>
public class PythonEpisodeEnricher
{
    private readonly IPythonBridgeService _pythonBridge;

    public PythonEpisodeEnricher(IPythonBridgeService pythonBridge)
    {
        _pythonBridge = pythonBridge;
    }

    public async Task<bool> EstáDisponibleAsync()
    {
        try { return await _pythonBridge.IsAvailableAsync(); }
        catch { return false; }
    }

    /// <summary>
    /// Obtiene metadata técnica de un video (ffprobe) y la aplica al EpisodioItem.
    /// </summary>
    public async Task EnriquecerEpisodioAsync(AniSkipModels.EpisodioItem episodio, CancellationToken ct = default)
    {
        if (episodio == null || string.IsNullOrWhiteSpace(episodio.RutaCompleta)) return;

        try
        {
            var result = await _pythonBridge.ExecuteCommandAsync<object, EpisodeInfoResult>(
                "inspect-episode",
                new { video_path = episodio.RutaCompleta },
                ct);

            if (result != null && result.Success)
            {
                episodio.Resolucion = result.Ancho > 0 && result.Alto > 0
                    ? $"{result.Ancho}x{result.Alto}" : string.Empty;
                episodio.CodecVideo = result.CodecVideo ?? string.Empty;
                episodio.Fps = result.Fps ?? string.Empty;
                episodio.Es10Bit = result.Es10Bit;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("PythonEpisodeEnricher", $"Error inspeccionando {episodio.TituloArchivo}: {ex.Message}");
        }
    }

    /// <summary>
    /// Calcula la ruta esperada de la miniatura de forma determinista y persistente (SHA-256 de la ruta).
    /// </summary>
    public static string ObtenerRutaMiniaturaEsperada(string rutaCompleta)
    {
        if (string.IsNullOrWhiteSpace(rutaCompleta)) return string.Empty;
        var thumbsDir = AppDataPaths.ThumbnailsDir;

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

    /// <summary>
    /// Extrae la miniatura del episodio probando varios timestamps si el primero cae en
    /// un frame casi negro/vacío (ver <see cref="TimestampsCandidatos"/>), usando Rust FFI
    /// como primera opción y el bridge Python como respaldo en cada timestamp. Se queda con
    /// el primer frame que ya no parezca vacío; si ninguno lo logra, conserva el último
    /// intento válido (mejor una miniatura "pobre" que ninguna). Devuelve true si quedó
    /// alguna miniatura utilizable en <paramref name="outPath"/>.
    /// </summary>
    public async Task<bool> ExtraerMiniaturaAsync(string rutaVideo, string outPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rutaVideo) || string.IsNullOrWhiteSpace(outPath)) return false;

        try
        {
            // Ya hay una miniatura válida y con contenido real: nada que hacer.
            if (EsMiniaturaValida(outPath) && !EsFrameDemasiadoVacio(outPath)) return true;

            Directory.CreateDirectory(AppDataPaths.ThumbnailsDir);

            bool huboExito = false;
            foreach (var timestamp in TimestampsCandidatos)
            {
                bool extraido = Native.NativeMethods.IsAvailable
                    && Native.NativeMethods.ExtractFrame(rutaVideo, outPath, timestamp, 320);

                // BUG-02: si Rust falló o dejó un JPEG truncado a medias, intentar el
                // bridge Python para ESTE mismo timestamp antes de pasar al siguiente.
                if (!extraido || !EsMiniaturaValida(outPath))
                {
                    if (File.Exists(outPath)) { try { File.Delete(outPath); } catch { } }

                    var result = await _pythonBridge.ExecuteCommandAsync<object, ThumbResult>(
                        "generate-thumbnail",
                        new { video_path = rutaVideo, output_path = outPath, timestamp = (int)timestamp },
                        ct);

                    extraido = result != null && result.Success && EsMiniaturaValida(outPath);
                }

                if (extraido)
                {
                    huboExito = true;
                    if (!EsFrameDemasiadoVacio(outPath)) return true;
                    // Frame válido pero casi vacío: se conserva por si es el mejor que
                    // consigamos, y se prueba el siguiente timestamp por si hay algo mejor.
                }
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
    /// Genera la miniatura del episodio si no existe (caché en LocalAppData/Thumbnails).
    /// </summary>
    public async Task GenerarMiniaturaAsync(AniSkipModels.EpisodioItem episodio, CancellationToken ct = default)
    {
        if (episodio == null || string.IsNullOrWhiteSpace(episodio.RutaCompleta)) return;

        string thumbPath = ObtenerRutaMiniaturaEsperada(episodio.RutaCompleta);
        if (await ExtraerMiniaturaAsync(episodio.RutaCompleta, thumbPath, ct))
        {
            episodio.RutaMiniatura = thumbPath;
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

    // ── Modelos de respuesta JSON (snake_case del CLI) ──
    private class EpisodeInfoResult
    {
        public bool Success { get; set; }
        public double DuracionSegundos { get; set; }
        public int Ancho { get; set; }
        public int Alto { get; set; }
        public string? CodecVideo { get; set; }
        public string? Fps { get; set; }
        public bool Es10Bit { get; set; }
    }

    private class ThumbResult
    {
        public bool Success { get; set; }
        public string? Output { get; set; }
    }

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
