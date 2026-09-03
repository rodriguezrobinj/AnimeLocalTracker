using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

public class AniSkipService : IAniSkipService
{
    private readonly HttpClient _httpClient;
    private static readonly ConcurrentDictionary<string, CacheEntry<List<AniSkipResult>>> _skipCache = new();
    private static readonly ConcurrentDictionary<int, int?> _malIdCache = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // ARQ-04: topes de capacidad para consumo de RAM constante en sesiones largas.
    private const int MaxSkipCacheEntries = 250;
    private const int MaxMalIdCacheEntries = 2000;

    public AniSkipService(HttpClient httpClient)
    {
        _httpClient = httpClient;

        // INT-02: las consultas de AniSkip son GETs ligeros — 30 s son suficientes
        // (el default de 100 s dejaba la llamada colgada demasiado tiempo).
        try
        {
            if (_httpClient.Timeout.TotalSeconds >= 100)
            {
                _httpClient.Timeout = TimeSpan.FromSeconds(30);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("AniSkipService", $"No se pudo ajustar el timeout del HttpClient: {ex.Message}");
        }
    }

    public async Task<List<AniSkipResult>> ObtenerSkipTimesAsync(int malId, int episodio, double duracionSegundos = 0, CancellationToken ct = default)
    {
        if (malId <= 0 || episodio <= 0) return [];

        string cacheKey = $"{malId}_{episodio}";
        if (_skipCache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow < cached.Expiration)
        {
            return cached.Data;
        }

        try
        {
            var uriBuilder = new StringBuilder();
            uriBuilder.Append($"https://api.aniskip.com/v2/skip-times/{malId}/{episodio}?types=op&types=ed&types=mixed-op&types=mixed-ed&types=recap");
            if (duracionSegundos > 0)
            {
                uriBuilder.Append($"&episodeLength={Math.Round(duracionSegundos, 0, MidpointRounding.AwayFromZero)}");
            }

            var request = new HttpRequestMessage(HttpMethod.Get, uriBuilder.ToString());
            request.Headers.Add("User-Agent", "AnimeLocalTracker/1.0");

            var response = await _httpClient.SendAsync(request, ct);

            // 404 significa que no hay registros para este episodio aún en la base de datos comunitaria de AniSkip
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                BoundedCache.Insert(_skipCache, cacheKey, [], MaxSkipCacheEntries, TimeSpan.FromMinutes(30));
                return [];
            }

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(ct);
                var result = JsonSerializer.Deserialize<AniSkipResponse>(json, JsonOptions);
                if (result != null && result.Found && result.Results != null && result.Results.Count > 0)
                {
                    BoundedCache.Insert(_skipCache, cacheKey, result.Results, MaxSkipCacheEntries, TimeSpan.FromHours(2));
                    return result.Results;
                }
            }

            // INT-05: un fallo puntual (429 tras reintentos, 5xx) no debe bloquear el salto
            // OP/ED durante 15 min: se cachea vacío solo 5 min y se reintenta antes.
            BoundedCache.Insert(_skipCache, cacheKey, [], MaxSkipCacheEntries, TimeSpan.FromMinutes(5));
            return [];
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (Exception ex)
        {
            AppLogger.Debug("AniSkipService", $"No se pudieron obtener skip-times para MAL ID {malId}, Ep {episodio}: {ex.Message}");
            return [];
        }
    }

    public async Task<int?> ObtenerMalIdDesdeAniListAsync(int aniListId, CancellationToken ct = default)
    {
        if (aniListId <= 0) return null;

        if (_malIdCache.TryGetValue(aniListId, out var cachedMalId))
        {
            return cachedMalId;
        }

        try
        {
            var query = @"
            query ($id: Int) {
                Media(id: $id, type: ANIME) {
                    id
                    idMal
                }
            }";

            var payload = new { query, variables = new { id = aniListId } };
            var jsonContent = JsonSerializer.Serialize(payload, JsonOptions);
            var requestContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("https://graphql.anilist.co", requestContent, ct);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(ct);
                var result = JsonSerializer.Deserialize<AniListResponse>(content, JsonOptions);
                var malId = result?.Data?.Media?.IdMal;
                BoundedCache.InsertNoExpiry(_malIdCache, aniListId, malId, MaxMalIdCacheEntries);
                return malId;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("AniSkipService", $"Error al resolver MAL ID para AniListId {aniListId}: {ex.Message}");
        }

        return null;
    }
}
