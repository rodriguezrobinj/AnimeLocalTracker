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

    // ~500 portadas a 220px ≈ 135MB. Al superar el tope se limpia el caché completo:
    // recargar desde disco es barato (sin red) y evita crecimiento sin límite en bibliotecas grandes.
    private const int MaxEntradasEnMemoria = 500;

    public ImageCacheService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
        
        _coversDirectory = AppDataPaths.CoversDir;
        
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
                    GuardarEnCache(animeId, bitmap);
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

    public ImageSource? ObtenerPortadaEnMemoria(int animeId)
    {
        return _memoryCache.TryGetValue(animeId, out var cached) ? cached : null;
    }

    private void GuardarEnCache(int animeId, ImageSource bitmap)
    {
        // ARQ-04b: al superar el tope se expulsan solo algunas entradas (no el caché completo),
        // evitando invalidar toda la galería a la vez (recargar desde disco es barato, pero el
        // Clear() total provocaba re-decode masivo en el arranque de bibliotecas grandes).
        if (_memoryCache.Count >= MaxEntradasEnMemoria)
        {
            foreach (var kv in _memoryCache.Take(16))
            {
                _memoryCache.TryRemove(kv.Key, out _);
            }
        }
        _memoryCache[animeId] = bitmap;
    }

    public async Task<ImageSource?> ObtenerPortadaAsync(int animeId, string? urlPortada, int decodeWidth = 220)
    {
        if (_memoryCache.TryGetValue(animeId, out var cachedMem))
        {
            return cachedMem;
        }

        string localPath = Path.Combine(_coversDirectory, $"{animeId}.jpg");
        if (File.Exists(localPath))
        {
            var diskBitmap = await Task.Run(() => CargarBitmapDesdeArchivo(localPath, decodeWidth)).ConfigureAwait(false);
            if (diskBitmap != null)
            {
                GuardarEnCache(animeId, diskBitmap);
                return diskBitmap;
            }
        }

        if (string.IsNullOrWhiteSpace(urlPortada)) return null;

        await _downloadSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_memoryCache.TryGetValue(animeId, out var cached))
            {
                return cached;
            }

            if (File.Exists(localPath))
            {
                var bitmap = await Task.Run(() => CargarBitmapDesdeArchivo(localPath, decodeWidth)).ConfigureAwait(false);
                if (bitmap != null)
                {
                    GuardarEnCache(animeId, bitmap);
                    return bitmap;
                }
            }

            if (!Uri.TryCreate(urlPortada, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return null;
            }

            using var client = _httpClientFactory.CreateClient();
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode) return null;

            const long maxBytes = 10L * 1024 * 1024; // 10 MB máximo para una portada
            if (response.Content.Headers.ContentLength is long declaredLen && declaredLen > maxBytes)
            {
                AppLogger.Warn("ImageCacheService", $"Portada rechazada para anime {animeId}: tamaño excesivo ({declaredLen} bytes).");
                return null;
            }

            using var responseStream = await response.Content.ReadAsStreamAsync();
            using var ms = new MemoryStream();
            byte[] buffer = new byte[81920];
            int bytesRead;
            long totalRead = 0;
            while ((bytesRead = await responseStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                totalRead += bytesRead;
                if (totalRead > maxBytes)
                {
                    AppLogger.Warn("ImageCacheService", $"Descarga de portada cancelada para {animeId}: superó el límite de 10 MB.");
                    return null;
                }
                ms.Write(buffer, 0, bytesRead);
            }

            byte[] bytes = ms.ToArray();

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
                GuardarEnCache(animeId, downloadedBitmap);
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
