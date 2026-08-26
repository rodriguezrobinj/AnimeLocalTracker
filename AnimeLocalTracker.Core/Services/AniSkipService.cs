using AnimeLocalTracker.Core.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Core.Models;

namespace AnimeLocalTracker.Core.Services;

public class AniSkipService : IAniSkipService
{
    private readonly HttpClient _httpClient;
    private static readonly ConcurrentDictionary<string, (List<AniSkipResult> Data, DateTime Expiration)> _skipCache = new();
    private static readonly ConcurrentDictionary<int, int?> _malIdCache = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public AniSkipService(HttpClient httpClient)
    {
        _httpClient = httpClient;
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
                _skipCache[cacheKey] = ([], DateTime.UtcNow.AddMinutes(30));
                return [];
            }

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(ct);
                var result = JsonSerializer.Deserialize<AniSkipResponse>(json, JsonOptions);
                if (result != null && result.Found && result.Results != null && result.Results.Count > 0)
                {
                    _skipCache[cacheKey] = (result.Results, DateTime.UtcNow.AddHours(2));
                    return result.Results;
                }
            }

            _skipCache[cacheKey] = ([], DateTime.UtcNow.AddMinutes(15));
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
                _malIdCache[aniListId] = malId;
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
