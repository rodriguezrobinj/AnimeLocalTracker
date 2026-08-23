using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AnimeLocalTracker.Services;

public class ImageCacheService : IImageCacheService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ConcurrentDictionary<int, ImageSource> _memoryCache = new();
    private readonly SemaphoreSlim _downloadSemaphore = new(6, 6);
    private readonly string _coversDirectory;

    public ImageCacheService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
        
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _coversDirectory = Path.Combine(appData, "AnimeLocalTracker", "Covers");
        
        try
        {
            if (!Directory.Exists(_coversDirectory))
            {
                Directory.CreateDirectory(_coversDirectory);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("ImageCacheService", $"No se pudo crear carpeta de portadas: {ex.Message}");
        }
    }

    public ImageSource? ObtenerPortada(int animeId, string? urlPortada, int decodeWidth = 220)
    {
        if (_memoryCache.TryGetValue(animeId, out var cached))
        {
            return cached;
        }

        string localPath = Path.Combine(_coversDirectory, $"{animeId}.jpg");
        if (File.Exists(localPath))
        {
            try
            {
                var bitmap = CargarBitmapDesdeArchivo(localPath, decodeWidth);
                if (bitmap != null)
                {
                    _memoryCache[animeId] = bitmap;
                    return bitmap;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug("ImageCacheService", $"Error cargando portada local para {animeId}: {ex.Message}");
            }
        }

        return null;
    }

    public async Task<ImageSource?> ObtenerPortadaAsync(int animeId, string? urlPortada, int decodeWidth = 220)
    {
        var existing = ObtenerPortada(animeId, urlPortada, decodeWidth);
        if (existing != null) return existing;

        if (string.IsNullOrWhiteSpace(urlPortada)) return null;

        string localPath = Path.Combine(_coversDirectory, $"{animeId}.jpg");

        await _downloadSemaphore.WaitAsync();
        try
        {
            if (_memoryCache.TryGetValue(animeId, out var cached))
            {
                return cached;
            }

            if (File.Exists(localPath))
            {
                var bitmap = CargarBitmapDesdeArchivo(localPath, decodeWidth);
                if (bitmap != null)
                {
                    _memoryCache[animeId] = bitmap;
                    return bitmap;
                }
            }

            using var client = _httpClientFactory.CreateClient();
            var bytes = await client.GetByteArrayAsync(urlPortada);

            try
            {
                await File.WriteAllBytesAsync(localPath, bytes);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("ImageCacheService", $"Error guardando portada en disco para {animeId}: {ex.Message}");
            }

            var downloadedBitmap = CargarBitmapDesdeBytes(bytes, decodeWidth);
            if (downloadedBitmap != null)
            {
                _memoryCache[animeId] = downloadedBitmap;
                return downloadedBitmap;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ImageCacheService", $"No se pudo descargar portada para {animeId}: {ex.Message}");
        }
        finally
        {
            _downloadSemaphore.Release();
        }

        return null;
    }

    public void InvalidarCache(int animeId)
    {
        _memoryCache.TryRemove(animeId, out _);
    }

    private static BitmapSource? CargarBitmapDesdeArchivo(string filePath, int decodeWidth)
    {
        try
        {
            if (!File.Exists(filePath)) return null;
            var bytes = File.ReadAllBytes(filePath);
            return CargarBitmapDesdeBytes(bytes, decodeWidth);
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ImageCacheService", $"Error cargando bitmap desde archivo '{filePath}': {ex.Message}");
            return null;
        }
    }

    private static BitmapSource? CargarBitmapDesdeBytes(byte[] bytes, int decodeWidth)
    {
        try
        {
            if (bytes == null || bytes.Length == 0) return null;

            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            // IMPORTANTE: Con StreamSource no se debe usar IgnoreImageCache porque WPF
            // intenta buscar un Uri nulo en ImagingCache y lanza ArgumentNullException.
            bitmap.CreateOptions = BitmapCreateOptions.None;
            if (decodeWidth > 0)
            {
                bitmap.DecodePixelWidth = decodeWidth;
            }
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ImageCacheService", $"Error cargando bitmap desde bytes: {ex.Message}");
            return null;
        }
    }
}
