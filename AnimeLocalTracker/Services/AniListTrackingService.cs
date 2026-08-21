// 2. La Implementación (AniListService.cs)
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

public class AniListTrackingService(HttpClient httpClient) : IAnimeTrackingService
{
    private readonly HttpClient _httpClient = httpClient;
    private static readonly ConcurrentDictionary<string, (object Data, DateTime Expiration)> _cache = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static bool TryGetFromCache<T>(string key, out T? result) where T : class
    {
        if (_cache.TryGetValue(key, out var entry))
        {
            if (DateTime.UtcNow < entry.Expiration)
            {
                result = entry.Data as T;
                return result != null;
            }
            _cache.TryRemove(key, out _);
        }
        result = null;
        return false;
    }

    private static void SetInCache<T>(string key, T data, TimeSpan duration) where T : class
    {
        _cache[key] = (data, DateTime.UtcNow.Add(duration));
    }

    public static void InvalidateCacheForMedia(int mediaId)
    {
        _cache.TryRemove($"media_{mediaId}", out _);
    }
    
    public async Task<AniListMedia?> ObtenerAnimePorIdAsync(int id)
    {
        var cacheKey = $"media_{id}";
        if (TryGetFromCache<AniListMedia>(cacheKey, out var cachedMedia))
        {
            return cachedMedia;
        }

        try
        {
            var query = @"
            query ($id: Int) {
                Media(id: $id, type: ANIME) {
                    id
                    title { romaji english native userPreferred }
                    synonyms
                    coverImage { extraLarge }
                    description(asHtml: false) 
                    genres
                    episodes
                    status
                    nextAiringEpisode { episode } 
                }
            }";

            var variables = new { id };
            var payload = new { query, variables };
            
            var jsonContent = JsonSerializer.Serialize(payload, JsonOptions);
            var requestContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("https://graphql.anilist.co", requestContent);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<AniListResponse>(content, JsonOptions);
                var media = result?.Data?.Media;
                if (media != null)
                {
                    SetInCache(cacheKey, media, TimeSpan.FromMinutes(30));
                }
                return media;
            }
            return null;
        }
        catch (Exception ex)
        {
            AppLogger.Error("AniListTrackingService", $"Error al obtener anime por ID {id}", ex);
            return null;
        }
    }

    public async Task<List<AniListMedia>> BuscarAnimePorTituloAsync(string titulo)
    {
        if (string.IsNullOrWhiteSpace(titulo)) return [];

        var cacheKey = $"search_{titulo.Trim().ToLowerInvariant()}";
        if (TryGetFromCache<List<AniListMedia>>(cacheKey, out var cachedList))
        {
            return cachedList!;
        }

        var query = @"
            query ($search: String) {
                Page(page: 1, perPage: 5) { 
                    media(search: $search, type: ANIME) {
                        id
                        title { romaji english native userPreferred }
                        synonyms
                        coverImage { extraLarge }
                        description(asHtml: false) 
                        genres
                        episodes
                        nextAiringEpisode { episode } 
                    }
                }
            }";

        var requestBody = new { query, variables = new { search = titulo } };
        var jsonContent = new StringContent(JsonSerializer.Serialize(requestBody, JsonOptions), Encoding.UTF8, "application/json");

        try
        {
            var response = await _httpClient.PostAsync("https://graphql.anilist.co", jsonContent);
            response.EnsureSuccessStatusCode();

            var jsonResponse = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<AniListResponse>(jsonResponse, JsonOptions);
            var list = result?.Data?.Page?.Media ?? [];
            if (list.Count > 0)
            {
                SetInCache(cacheKey, list, TimeSpan.FromMinutes(10));
            }

            return list; 
        }
        catch (Exception ex)
        {
            AppLogger.Error("AniListTrackingService", $"Error al buscar anime por título '{titulo}'", ex);
            return [];
        }
    }
    
    public async Task<bool> ActualizarProgresoAsync(int mediaId, int episodio, string token)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "https://graphql.anilist.co");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            var query = @"
            mutation ($mediaId: Int, $progress: Int) {
                SaveMediaListEntry (mediaId: $mediaId, progress: $progress) {
                    id
                    progress
                }
            }";

            var variables = new { mediaId, progress = episodio };
            var payload = new { query, variables };

            var jsonContent = JsonSerializer.Serialize(payload, JsonOptions);
            request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                InvalidateCacheForMedia(mediaId);
                return true;
            }
            return false; 
        }
        catch (Exception ex)
        {
            AppLogger.Error("AniListTrackingService", $"Error al actualizar progreso para MediaId {mediaId}", ex);
            return false;
        }
    }
    
    public async Task<AniListMediaList?> ObtenerSeguimientoUsuarioAsync(int mediaId, string token)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "https://graphql.anilist.co");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            var query = @"
            query ($id: Int) {
                Media(id: $id) {
                    mediaListEntry {
                        id
                        status
                        score
                        progress
                        startedAt { year month day }
                        completedAt { year month day }
                    }
                }
            }";

            var payload = new { query, variables = new { id = mediaId } };
            var jsonContent = JsonSerializer.Serialize(payload, JsonOptions);
            request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<AniListResponse>(content, JsonOptions);
                return result?.Data?.Media?.MediaListEntry; 
            }
            return null;
        }
        catch (Exception ex)
        {
            AppLogger.Error("AniListTrackingService", $"Error al obtener seguimiento de usuario para MediaId {mediaId}", ex);
            return null;
        }
    }

    public async Task<bool> GuardarSeguimientoUsuarioAsync(int mediaId, string estado, int progreso, float puntaje, DateTime? fechaInicio, DateTime? fechaFin, string token)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "https://graphql.anilist.co");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            var query = @"
            mutation ($mediaId: Int, $status: MediaListStatus, $scoreRaw: Int, $progress: Int, $startedAt: FuzzyDateInput, $completedAt: FuzzyDateInput) {
                SaveMediaListEntry (mediaId: $mediaId, status: $status, scoreRaw: $scoreRaw, progress: $progress, startedAt: $startedAt, completedAt: $completedAt) {
                    id
                }
            }";

            var startedAt = fechaInicio.HasValue ? new { year = fechaInicio.Value.Year, month = fechaInicio.Value.Month, day = fechaInicio.Value.Day } : null;
            var completedAt = fechaFin.HasValue ? new { year = fechaFin.Value.Year, month = fechaFin.Value.Month, day = fechaFin.Value.Day } : null;

            var variables = new 
            { 
                mediaId, 
                status = estado, 
                scoreRaw = (int)puntaje,
                progress = progreso,
                startedAt,
                completedAt
            };

            var payload = new { query, variables };
            var jsonContent = JsonSerializer.Serialize(payload, JsonOptions);
            request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();
            
            if (content.Contains("\"errors\""))
            {
                AppLogger.Warn("AniListTrackingService", $"AniList rechazó los datos para MediaId {mediaId}. Servidor: {content}");
                return false;
            }

            if (response.IsSuccessStatusCode)
            {
                InvalidateCacheForMedia(mediaId);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            AppLogger.Error("AniListTrackingService", $"Error al guardar seguimiento de usuario para MediaId {mediaId}", ex);
            return false; 
        }
    }
    
    public async Task<AniListUser?> ObtenerPerfilUsuarioAsync(string token)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "https://graphql.anilist.co");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            var query = @"
            query {
                Viewer {
                    name
                    avatar { large }
                }
            }";

            var payload = new { query };
            var jsonContent = JsonSerializer.Serialize(payload, JsonOptions);
            request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<AniListResponse>(content, JsonOptions);
                return result?.Data?.Viewer;
            }
            return null;
        }
        catch (Exception ex)
        {
            AppLogger.Error("AniListTrackingService", "Error al obtener perfil de usuario", ex);
            return null;
        }
    }
    
    public async Task<List<AniListMedia>> BuscarAnimesEnVivoAsync(string busqueda)
    {
        if (string.IsNullOrWhiteSpace(busqueda)) return [];

        var cacheKey = $"live_{busqueda.Trim().ToLowerInvariant()}";
        if (TryGetFromCache<List<AniListMedia>>(cacheKey, out var cachedList))
        {
            return cachedList!;
        }

        try
        {
            var query = @"
            query ($search: String) {
                Page (page: 1, perPage: 8) {
                    media (search: $search, type: ANIME) {
                        id
                        title { romaji english native userPreferred }
                        synonyms
                        coverImage { extraLarge }
                        status
                        description
                        genres
                        episodes
                        startDate { year month day }
                        nextAiringEpisode { episode }
                    }
                }
            }";

            var payload = new { query, variables = new { search = busqueda } };
            var jsonContent = JsonSerializer.Serialize(payload, JsonOptions);
            var requestContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("https://graphql.anilist.co", requestContent);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<AniListResponse>(content, JsonOptions);
                var mediaList = result?.Data?.Page?.Media ?? [];
                if (mediaList.Count > 0)
                {
                    SetInCache(cacheKey, mediaList, TimeSpan.FromMinutes(5));
                }
                return mediaList;
            }
            return [];
        }
        catch (Exception ex)
        {
            AppLogger.Error("AniListTrackingService", $"Error al buscar animes en vivo para '{busqueda}'", ex);
            return [];
        }
    }

    public async Task<List<AiringEpisode>> ObtenerCalendarioEmisionAsync(List<int> mediaIds, long inicioSemana, long finSemana)
    {
        if (mediaIds == null || mediaIds.Count == 0) return [];

        var query = @"
        query($mediaIds: [Int], $airingAt_greater: Int, $airingAt_lesser: Int) {
          Page(page: 1, perPage: 50) {
            airingSchedules(mediaId_in: $mediaIds, airingAt_greater: $airingAt_greater, airingAt_lesser: $airingAt_lesser, sort: TIME) {
              episode
              airingAt
              media {
                id
                title { romaji }
                coverImage { extraLarge }
              }
            }
          }
        }";

        var requestBody = new
        {
            query,
            variables = new
            {
                mediaIds,
                airingAt_greater = inicioSemana,
                airingAt_lesser = finSemana
            }
        };

        var jsonContent = new StringContent(JsonSerializer.Serialize(requestBody, JsonOptions), Encoding.UTF8, "application/json");

        try
        {
            var response = await _httpClient.PostAsync("https://graphql.anilist.co", jsonContent);
            if (!response.IsSuccessStatusCode) return [];

            var jsonResponse = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<AniListResponse>(jsonResponse, JsonOptions);

            var schedules = result?.Data?.Page?.AiringSchedules;
            if (schedules == null) return [];

            var airingList = new List<AiringEpisode>();
            foreach (var s in schedules)
            {
                if (s.Media == null) continue;
                airingList.Add(new AiringEpisode
                {
                    AniListId = s.Media.Id,
                    Titulo = s.Media.Title.Romaji,
                    UrlPortada = s.Media.CoverImage.ExtraLarge ?? "",
                    NumeroEpisodio = s.Episode,
                    FechaEmision = DateTimeOffset.FromUnixTimeSeconds(s.AiringAt).DateTime
                });
            }
            return airingList;
        }
        catch (Exception ex)
        {
            AppLogger.Error("AniListTrackingService", "Error al obtener calendario de emisión", ex);
            return [];
        }
    }
}