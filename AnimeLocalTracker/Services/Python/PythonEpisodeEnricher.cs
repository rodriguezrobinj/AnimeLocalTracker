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
    /// Calcula la ruta esperada de la miniatura de forma determinista y persistente (MD5 de la ruta).
    /// </summary>
    public static string ObtenerRutaMiniaturaEsperada(string rutaCompleta)
    {
        if (string.IsNullOrWhiteSpace(rutaCompleta)) return string.Empty;
        var thumbsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AnimeLocalTracker", "Thumbnails");
        
        using var md5 = System.Security.Cryptography.MD5.Create();
        byte[] hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(rutaCompleta.ToLowerInvariant()));
        string hex = Convert.ToHexString(hash).ToLowerInvariant();
        return Path.Combine(thumbsDir, $"{hex}.jpg");
    }

    /// <summary>
    /// Devuelve la ruta de la miniatura si ya existe en disco, de lo contrario null.
    /// </summary>
    public static string? ObtenerRutaMiniaturaSiExiste(string rutaCompleta)
    {
        if (string.IsNullOrWhiteSpace(rutaCompleta)) return null;
        string path = ObtenerRutaMiniaturaEsperada(rutaCompleta);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Genera la miniatura del episodio si no existe (caché en LocalAppData/Thumbnails).
    /// </summary>
    public async Task GenerarMiniaturaAsync(AniSkipModels.EpisodioItem episodio, CancellationToken ct = default)
    {
        if (episodio == null || string.IsNullOrWhiteSpace(episodio.RutaCompleta)) return;

        try
        {
            var thumbsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AnimeLocalTracker", "Thumbnails");
            Directory.CreateDirectory(thumbsDir);

            string thumbPath = ObtenerRutaMiniaturaEsperada(episodio.RutaCompleta);

            if (File.Exists(thumbPath))
            {
                episodio.RutaMiniatura = thumbPath;
                return;
            }

            var result = await _pythonBridge.ExecuteCommandAsync<object, ThumbResult>(
                "generate-thumbnail",
                new { video_path = episodio.RutaCompleta, output_path = thumbPath, timestamp = 30 },
                ct);

            if (result != null && result.Success && File.Exists(thumbPath))
            {
                episodio.RutaMiniatura = thumbPath;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("PythonEpisodeEnricher", $"Error generando miniatura de {episodio.TituloArchivo}: {ex.Message}");
        }
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
