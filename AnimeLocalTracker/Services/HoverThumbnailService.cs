using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AnimeLocalTracker.Services.Python;

namespace AnimeLocalTracker.Services;

public class HoverThumbnailService : IHoverThumbnailService
{
    private readonly IPythonBridgeService _pythonBridge;
    private readonly ConcurrentDictionary<string, ImageSource> _memoryCache = new();
    private readonly SemaphoreSlim _extractionLock = new(2, 2);
    private const int MaxMemoryItems = 200;

    public int BucketIntervaloSegundos => 4;

    public HoverThumbnailService(IPythonBridgeService pythonBridge)
    {
        _pythonBridge = pythonBridge;
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

        // 2. Verificar caché en disco
        string discoPath = ObtenerRutaDisco(videoHash, bucketSec);
        if (File.Exists(discoPath))
        {
            var imgDesdeDisco = CargarBitmapDesdeArchivo(discoPath);
            if (imgDesdeDisco != null)
            {
                GuardarEnCacheMemoria(cacheKey, imgDesdeDisco);
                return imgDesdeDisco;
            }
        }

        // 3. Extracción en segundo plano mediante Python Bridge / FFmpeg
        await _extractionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Doble comprobación tras esperar el semáforo
            if (File.Exists(discoPath))
            {
                var img = CargarBitmapDesdeArchivo(discoPath);
                if (img != null)
                {
                    GuardarEnCacheMemoria(cacheKey, img);
                    return img;
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(discoPath)!);

            var result = await _pythonBridge.ExecuteCommandAsync<object, ThumbResult>(
                "generate-thumbnail",
                new
                {
                    video_path = rutaVideo,
                    output_path = discoPath,
                    timestamp = (float)bucketSec,
                    width = 240
                },
                ct).ConfigureAwait(false);

            if (result != null && result.Success && File.Exists(discoPath))
            {
                var nuevaImagen = CargarBitmapDesdeArchivo(discoPath);
                if (nuevaImagen != null)
                {
                    GuardarEnCacheMemoria(cacheKey, nuevaImagen);
                    return nuevaImagen;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Petición cancelada porque el mouse se movió a otro punto
        }
        catch (Exception ex)
        {
            AppLogger.Debug("HoverThumbnailService", $"No se pudo generar miniatura para {rutaVideo} en {bucketSec}s: {ex.Message}");
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
    }

    private void GuardarEnCacheMemoria(string key, ImageSource img)
    {
        if (_memoryCache.Count >= MaxMemoryItems)
        {
            _memoryCache.Clear();
        }
        _memoryCache[key] = img;
    }

    private static string ObtenerRutaDisco(string videoHash, int bucketSec)
    {
        var basePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AnimeLocalTracker", "Thumbnails", "Preview", videoHash);

        return Path.Combine(basePath, $"{bucketSec}.jpg");
    }

    private static string ObtenerHashRuta(string rutaVideo)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(rutaVideo.ToLowerInvariant()));
        return Convert.ToHexString(hash)[..16];
    }

    private static ImageSource? CargarBitmapDesdeArchivo(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.DecodePixelWidth = 240;
            bitmap.EndInit();
            bitmap.Freeze(); // Hace que la imagen sea inmutable y apta para pasar entre hilos y UI
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private class ThumbResult
    {
        public bool Success { get; set; }
        public string? Output { get; set; }
        public string? Error { get; set; }
    }
}
