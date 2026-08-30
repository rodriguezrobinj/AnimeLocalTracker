using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AnimeLocalTracker.Services.Native;
using AnimeLocalTracker.Services.Python;

namespace AnimeLocalTracker.Services;

public class HoverThumbnailService : IHoverThumbnailService
{
    private readonly IPythonBridgeService _pythonBridge;
    private readonly ConcurrentDictionary<string, ImageSource> _memoryCache = new();
    private readonly SemaphoreSlim _extractionLock = new(2, 2);
    private readonly string _tempDirectory;
    private const int MaxMemoryItems = 500;

    // Sprite Sheet en memoria RAM para navegación instantánea a 0 ms (60 FPS)
    private BitmapSource? _currentSpritesheet;
    private SpritesheetMetadata? _currentSpritesheetMeta;
    private string? _currentVideoPath;
    private CancellationTokenSource? _spritesheetCts;

    // Precisión de 4 segundos: el mínimo que evita decenas de procesos ffmpeg (uno por
    // segundo) al recorrer la línea de tiempo. A 1s el scrub de una temporada completa
    // lanzaba cientos de extracciones simultáneas que saturaban la CPU y dejaban la
    // preview en estado de carga perpetuo. Con el Sprite Sheet en memoria el recorte
    // sigue siendo instantáneo (0 ms) en cualquier punto exacto.
    public int BucketIntervaloSegundos => 4;

    public HoverThumbnailService(IPythonBridgeService pythonBridge)
    {
        _pythonBridge = pythonBridge;
        _tempDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnimeLocalTracker", "Temp");
        try
        {
            Directory.CreateDirectory(_tempDirectory);
        }
        catch { }
    }

    public void PrepararSpritesheet(string rutaVideo, double duracionTotalSegundos)
    {
        if (string.IsNullOrWhiteSpace(rutaVideo) || !File.Exists(rutaVideo))
            return;

        if (_currentVideoPath == rutaVideo && _currentSpritesheet != null)
            return; // Ya está cargado en memoria

        _spritesheetCts?.Cancel();
        _spritesheetCts?.Dispose();
        _spritesheetCts = new CancellationTokenSource();
        var ct = _spritesheetCts.Token;

        _currentVideoPath = rutaVideo;
        _currentSpritesheet = null;
        _currentSpritesheetMeta = null;

        string videoHash = ObtenerHashRuta(rutaVideo);
        string discoPath = ObtenerRutaSpritesheetDisco(videoHash);

        // 1. Si ya existe en caché de disco, cargarlo en RAM de inmediato
        if (File.Exists(discoPath))
        {
            var bitmap = CargarBitmapCompleto(discoPath);
            if (bitmap != null)
            {
                _currentSpritesheet = bitmap;
                _currentSpritesheetMeta = new SpritesheetMetadata
                {
                    Columns = 10,
                    Rows = 6,
                    ThumbWidth = 160,
                    ThumbHeight = 90,
                    TotalThumbs = 60,
                    IntervalSeconds = (duracionTotalSegundos > 0 ? duracionTotalSegundos : 1440.0) / 60.0
                };
                return;
            }
        }

        // 2. Generar en segundo plano (paralelismo acotado a 2 procesos ffmpeg en Rust)
        _ = Task.Run(() =>
        {
            try
            {
                if (ct.IsCancellationRequested) return;

                if (NativeMethods.IsAvailable)
                {
                    var result = NativeMethods.GenerateSpritesheet(rutaVideo, discoPath, duracionTotalSegundos, 60);
                    if (result != null && result.Success && File.Exists(discoPath) && !ct.IsCancellationRequested)
                    {
                        var bmp = CargarBitmapCompleto(discoPath);
                        if (bmp != null && _currentVideoPath == rutaVideo)
                        {
                            _currentSpritesheet = bmp;
                            _currentSpritesheetMeta = new SpritesheetMetadata
                            {
                                Columns = result.Columns,
                                Rows = result.Rows,
                                ThumbWidth = result.ThumbWidth,
                                ThumbHeight = result.ThumbHeight,
                                TotalThumbs = result.TotalThumbs,
                                IntervalSeconds = result.IntervalSeconds
                            };
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug("HoverThumbnailService", $"Error generando Sprite Sheet en Rust: {ex.Message}");
            }
        }, ct);
    }

    public ImageSource? ObtenerFrameInstantaneo(double segundos)
    {
        var sheet = _currentSpritesheet;
        var meta = _currentSpritesheetMeta;

        if (sheet == null || meta == null || meta.IntervalSeconds <= 0 || segundos < 0)
            return null;

        try
        {
            int index = Math.Clamp((int)(segundos / meta.IntervalSeconds), 0, (int)meta.TotalThumbs - 1);
            int col = index % (int)meta.Columns;
            int row = index / (int)meta.Columns;

            int x = col * (int)meta.ThumbWidth;
            int y = row * (int)meta.ThumbHeight;

            if (x + (int)meta.ThumbWidth <= sheet.PixelWidth && y + (int)meta.ThumbHeight <= sheet.PixelHeight)
            {
                var rect = new Int32Rect(x, y, (int)meta.ThumbWidth, (int)meta.ThumbHeight);
                var cropped = new CroppedBitmap(sheet, rect);
                cropped.Freeze();
                return cropped;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("HoverThumbnailService", $"Error recortando frame de spritesheet: {ex.Message}");
        }

        return null;
    }

    public async Task<ImageSource?> ObtenerMiniaturaHoverAsync(string rutaVideo, double segundos, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rutaVideo) || !File.Exists(rutaVideo) || segundos < 0)
            return null;

        int bucketSec = ((int)segundos / BucketIntervaloSegundos) * BucketIntervaloSegundos;
        string videoHash = ObtenerHashRuta(rutaVideo);
        string cacheKey = $"{videoHash}_{bucketSec}";

        // 1. Verificar caché en memoria RAM (0 ms)
        if (_memoryCache.TryGetValue(cacheKey, out var cachedImage))
        {
            return cachedImage;
        }

        // 2. Intentar corte instantáneo del Sprite Sheet si está listo (0 ms, 0 CPU)
        var instantFrame = ObtenerFrameInstantaneo(segundos);
        if (instantFrame != null)
        {
            GuardarEnCacheMemoria(cacheKey, instantFrame);
            return instantFrame;
        }

        if (ct.IsCancellationRequested) return null;

        // 3. Extracción nativa rápida si el Sprite Sheet aún no está listo. Se extrae a un
        // archivo TEMPORAL (se borra tras cargarla) — NO se persisten fotogramas por segundo:
        // eso llenaba el disco y no aportaba, porque el Sprite Sheet (1 archivo por video)
        // es la fuente persistente real.
        await _extractionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_memoryCache.TryGetValue(cacheKey, out var recheck))
            {
                return recheck;
            }

            var instantFrame2 = ObtenerFrameInstantaneo(segundos);
            if (instantFrame2 != null)
            {
                GuardarEnCacheMemoria(cacheKey, instantFrame2);
                return instantFrame2;
            }

            string tempPath = Path.Combine(_tempDirectory, $"hover_{videoHash}_{bucketSec}.jpg");
            try
            {
                bool extraido = false;
                if (NativeMethods.IsAvailable)
                {
                    extraido = NativeMethods.ExtractFrame(rutaVideo, tempPath, bucketSec, 240);
                }

                // Fallback a Python Bridge si Rust no está disponible
                if (!extraido && !File.Exists(tempPath))
                {
                    var result = await _pythonBridge.ExecuteCommandAsync<object, ThumbResult>(
                        "generate-thumbnail",
                        new
                        {
                            video_path = rutaVideo,
                            output_path = tempPath,
                            timestamp = (float)bucketSec,
                            width = 240
                        },
                        ct).ConfigureAwait(false);

                    extraido = result != null && result.Success;
                }

                if (File.Exists(tempPath))
                {
                    var nuevaImagen = CargarBitmapDesdeArchivo(tempPath);
                    if (nuevaImagen != null)
                    {
                        GuardarEnCacheMemoria(cacheKey, nuevaImagen);
                        return nuevaImagen;
                    }
                }
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLogger.Debug("HoverThumbnailService", $"Error extrayendo miniatura para {rutaVideo} en {bucketSec}s: {ex.Message}");
        }
        finally
        {
            _extractionLock.Release();
        }

        return null;
    }

    public void LimpiarCacheMemoria()
    {
        _memoryCache.Clear();
        _currentSpritesheet = null;
        _currentSpritesheetMeta = null;
        _currentVideoPath = null;
        try { _spritesheetCts?.Cancel(); } catch { }
        _spritesheetCts?.Dispose();
        _spritesheetCts = null;
    }

    private void GuardarEnCacheMemoria(string key, ImageSource img)
    {
        if (_memoryCache.Count >= MaxMemoryItems)
        {
            _memoryCache.Clear();
        }
        _memoryCache[key] = img;
    }

    private static string ObtenerRutaSpritesheetDisco(string videoHash)
    {
        var basePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AnimeLocalTracker", "Thumbnails", "Spritesheets");

        return Path.Combine(basePath, $"{videoHash}.jpg");
    }

    private static string ObtenerHashRuta(string rutaVideo)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(rutaVideo.ToLowerInvariant()));
        return Convert.ToHexString(hash)[..16];
    }

    private static BitmapSource? CargarBitmapCompleto(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length == 0) return null;
            byte[] bytes = File.ReadAllBytes(path);
            using var ms = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = ms;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            AppLogger.Debug("HoverThumbnailService", $"Error cargando spritesheet bitmap desde {path}: {ex.Message}");
            return null;
        }
    }

    private static ImageSource? CargarBitmapDesdeArchivo(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length == 0) return null;
            byte[] bytes = File.ReadAllBytes(path);
            using var ms = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 240;
            bitmap.StreamSource = ms;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            AppLogger.Debug("HoverThumbnailService", $"Error cargando frame bitmap desde {path}: {ex.Message}");
            return null;
        }
    }

    private class SpritesheetMetadata
    {
        public uint Columns { get; set; }
        public uint Rows { get; set; }
        public uint ThumbWidth { get; set; }
        public uint ThumbHeight { get; set; }
        public uint TotalThumbs { get; set; }
        public double IntervalSeconds { get; set; }
    }

    private class ThumbResult
    {
        public bool Success { get; set; }
        public string? Output { get; set; }
        public string? Error { get; set; }
    }
}
