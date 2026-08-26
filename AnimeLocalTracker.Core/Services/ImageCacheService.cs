using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Core.Services;

public class ImageCacheService : IImageCacheService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _coversDirectory;
    private readonly ConcurrentDictionary<int, byte[]> _memoryCache;
    private readonly SemaphoreSlim _downloadSemaphore = new SemaphoreSlim(3, 3);
    private const int MaxEntradasEnMemoria = 200;

    public ImageCacheService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
        _coversDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AnimeLocalTracker", "Covers");
        _memoryCache = new ConcurrentDictionary<int, byte[]>();

        if (!Directory.Exists(_coversDirectory))
        {
            Directory.CreateDirectory(_coversDirectory);
        }
    }

    public byte[]? CargarBitmapDesdeArchivo(string? imagePath, int animeId)
    {
        if (_memoryCache.TryGetValue(animeId, out var cached))
        {
            return cached;
        }

        string localPath = Path.Combine(_coversDirectory, $".jpg");
        if (File.Exists(localPath))
        {
            try
            {
                var bytes = File.ReadAllBytes(localPath);
                GuardarEnCache(animeId, bytes);
                return bytes;
            }
            catch (Exception ex)
            {
                AppLogger.Debug("ImageCacheService", $"Error cargando portada local para {animeId}: {ex.Message}");
            }
        }

        return null;
    }

    private void GuardarEnCache(int animeId, byte[] bytes)
    {
        if (_memoryCache.Count >= MaxEntradasEnMemoria)
        {
            _memoryCache.Clear();
        }
        _memoryCache[animeId] = bytes;
    }

        public byte[]? ObtenerPortada(int animeId, string? urlPortada, int decodeWidth = 220)
    {
        return CargarBitmapDesdeArchivo(urlPortada, animeId);
    }

    public void InvalidarCache(int animeId)
    {
        _memoryCache.TryRemove(animeId, out _);
    }

    public async Task<byte[]?> ObtenerPortadaAsync(int animeId, string? urlPortada, int decodeWidth = 220)
    {
        if (_memoryCache.TryGetValue(animeId, out var cached))
        {
            return cached;
        }

        if (string.IsNullOrWhiteSpace(urlPortada)) return null;

        string localPath = Path.Combine(_coversDirectory, $".jpg");

        await _downloadSemaphore.WaitAsync();
        try
        {
            if (_memoryCache.TryGetValue(animeId, out cached))
            {
                return cached;
            }

            if (File.Exists(localPath))
            {
                var bytes = await File.ReadAllBytesAsync(localPath);
                GuardarEnCache(animeId, bytes);
                return bytes;
            }

            using var client = _httpClientFactory.CreateClient();
            var downloadedBytes = await client.GetByteArrayAsync(urlPortada);

            try
            {
                await File.WriteAllBytesAsync(localPath, downloadedBytes);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("ImageCacheService", $"Error guardando portada en disco para {animeId}: {ex.Message}");
            }

            GuardarEnCache(animeId, downloadedBytes);
            return downloadedBytes;
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ImageCacheService", $"Error descargando portada para {animeId}: {ex.Message}");
            return null;
        }
        finally
        {
            _downloadSemaphore.Release();
        }
    }
}