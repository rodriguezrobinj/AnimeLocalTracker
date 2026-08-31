// 2. La Implementación (AniListService.cs)
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

public class AniListTrackingService : IAnimeTrackingService
{
    private readonly HttpClient _httpClient;
    private readonly IAuthService? _authService;
    private static readonly ConcurrentDictionary<string, CacheEntry<object>> _cache = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // ARQ-04: tope de capacidad para que el consumo de RAM sea constante en sesiones largas.
    private const int MaxCacheEntries = 250;

    private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    public AniListTrackingService(HttpClient httpClient, IAuthService? authService = null)
    {
        _httpClient = httpClient;
        _authService = authService;

        try
        {
            if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
            {
                _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
            }
            if (!_httpClient.DefaultRequestHeaders.Contains("Accept"))
            {
                _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
            }
            if (_httpClient.Timeout.TotalSeconds >= 100) // default .NET
            {
                _httpClient.Timeout = TimeSpan.FromSeconds(30);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("AniListTrackingService", $"No se pudo configurar el HttpClient: {ex.Message}");
        }
    }

    private HttpRequestMessage CrearRequest(string jsonPayload, string? explicitToken = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://graphql.anilist.co");
        request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        string? token = !string.IsNullOrEmpty(explicitToken) ? explicitToken : _authService?.ObtenerTokenGuardado();
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Trim());
        }

        return request;
    }

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
        BoundedCache.Insert(_cache, key, data, MaxCacheEntries, duration);
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
                    idMal
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

            var request = CrearRequest(jsonContent);
            var response = await _httpClient.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                if (content.Contains("\"errors\""))
                {
                    AppLogger.Warn("AniListTrackingService", $"AniList devolvió error al obtener anime {id}: {Truncar(content)}");
                    return null;
                }

                var result = JsonSerializer.Deserialize<AniListResponse>(content, JsonOptions);
                var media = result?.Data?.Media;
                if (media != null)
                {
                    SetInCache(cacheKey, media, TimeSpan.FromMinutes(30));
                }
                return media;
            }
            else
            {
                AppLogger.Warn("AniListTrackingService", $"ObtenerAnimePorId ({id}) falló con HTTP {(int)response.StatusCode}.");
            }
            return null;
        }
        catch (OperationCanceledException)
        {
            AppLogger.Warn("AniListTrackingService", $"Timeout al obtener anime por ID {id} de AniList (la API tardó más de 30s o la conexión fue lenta).");
            return null;
        }
        catch (HttpRequestException ex)
        {
            AppLogger.Warn("AniListTrackingService", $"Fallo de red al conectar con AniList ({id}): {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            AppLogger.Error("AniListTrackingService", $"Error al obtener anime por ID {id}", ex);
            return null;
        }
    }

    /// <summary>
    /// RND-02: consulta muchos animes de una vez (Page.media con id_in + mediaListEntry
    /// del usuario autenticado). Reemplaza N× ObtenerAnimePorIdAsync + N×
    /// ObtenerSeguimientoUsuarioAsync por ~1 request cada 50 animes: la actualización
    /// de biblioteca pasa de cientos de llamadas seriales a unas pocas.
    /// Usa el caché para los IDs ya frescos (30 min).
    /// </summary>
    public async Task<Dictionary<int, AniListMedia>> ObtenerAnimesPorIdsLoteAsync(IEnumerable<int> ids, string? token = null)
    {
        var resultado = new Dictionary<int, AniListMedia>();
        var idsUnicos = ids.Distinct().ToList();
        if (idsUnicos.Count == 0) return resultado;

        // 1. Servir los que ya estén en caché
        var pendientes = new List<int>();
        foreach (var id in idsUnicos)
        {
            if (TryGetFromCache<AniListMedia>($"media_{id}", out var cached) && cached != null)
            {
                resultado[id] = cached;
            }
            else
            {
                pendientes.Add(id);
            }
        }
        if (pendientes.Count == 0) return resultado;

        // 2. Lotes de 50 (máximo perPage de AniList para la conexión media)
        const int TamanoLote = 50;
        foreach (var chunk in pendientes.Chunk(TamanoLote))
        {
            try
            {
                var query = @"
                query ($ids: [Int]) {
                    Page(page: 1, perPage: 50) {
                        media(id_in: $ids, type: ANIME) {
                            id
                            idMal
                            title { romaji english native userPreferred }
                            synonyms
                            coverImage { extraLarge }
                            description(asHtml: false)
                            genres
                            episodes
                            status
                            nextAiringEpisode { episode }
                            mediaListEntry {
                                id
                                status
                                score
                                progress
                                startedAt { year month day }
                                completedAt { year month day }
                            }
                        }
                    }
                }";

                var payload = new { query, variables = new { ids = chunk } };
                var jsonContent = JsonSerializer.Serialize(payload, JsonOptions);

                var request = CrearRequest(jsonContent, token);
                var response = await _httpClient.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                {
                    AppLogger.Warn("AniListTrackingService", $"Lote de animes ({chunk.Length} ids) falló con HTTP {(int)response.StatusCode}.");
                    continue;
                }

                var content = await response.Content.ReadAsStringAsync();
                if (content.Contains("\"errors\""))
                {
                    AppLogger.Warn("AniListTrackingService", $"AniList devolvió error en lote de animes: {Truncar(content)}");
                    continue;
                }

                var result = JsonSerializer.Deserialize<AniListResponse>(content, JsonOptions);
                var medias = result?.Data?.Page?.Media;
                if (medias == null) continue;

                foreach (var media in medias)
                {
                    resultado[media.Id] = media;
                    SetInCache($"media_{media.Id}", media, TimeSpan.FromMinutes(30));
                }
            }
            catch (OperationCanceledException)
            {
                AppLogger.Warn("AniListTrackingService", $"Timeout al obtener lote de animes de AniList.");
            }
            catch (HttpRequestException ex)
            {
                AppLogger.Warn("AniListTrackingService", $"Fallo de red al conectar con AniList (lote): {ex.Message}");
            }
            catch (Exception ex)
            {
                AppLogger.Error("AniListTrackingService", $"Error al obtener lote de animes", ex);
            }
        }

        return resultado;
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
                        idMal
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
        var jsonContent = JsonSerializer.Serialize(requestBody, JsonOptions);

        try
        {
            var request = CrearRequest(jsonContent);
            var response = await _httpClient.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                var jsonResponse = await response.Content.ReadAsStringAsync();
                if (jsonResponse.Contains("\"errors\""))
                {
                    AppLogger.Warn("AniListTrackingService", $"AniList devolvió error al buscar '{titulo}': {Truncar(jsonResponse)}");
                    return [];
                }

                var result = JsonSerializer.Deserialize<AniListResponse>(jsonResponse, JsonOptions);
                var list = result?.Data?.Page?.Media ?? [];
                if (list.Count > 0)
                {
                    SetInCache(cacheKey, list, TimeSpan.FromMinutes(10));
                }

                return list;
            }
            else
            {
                AppLogger.Warn("AniListTrackingService", $"BuscarAnimePorTitulo '{titulo}' falló con HTTP {(int)response.StatusCode}.");
                return [];
            }
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

            var request = CrearRequest(jsonContent, token);
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

            var request = CrearRequest(jsonContent, token);
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

            var request = CrearRequest(jsonContent, token);
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
            var query = @"
            query {
                Viewer {
                    name
                    avatar { large }
                }
            }";

            var payload = new { query };
            var jsonContent = JsonSerializer.Serialize(payload, JsonOptions);

            var request = CrearRequest(jsonContent, token);
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
    
    public async Task<List<AniListMedia>> BuscarAnimesEnVivoAsync(string busqueda, System.Threading.CancellationToken cancellationToken = default)
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
                Page (page: 1, perPage: 24) {
                    media (search: $search, type: ANIME, sort: SEARCH_MATCH) {
                        id
                        idMal
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

            var request = CrearRequest(jsonContent);
            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                if (content.Contains("\"errors\""))
                {
                    AppLogger.Error("AniListTrackingService", $"AniList devolvió errores GraphQL buscando '{busqueda}': {Truncar(content)}", null);
                    return [];
                }
                var result = JsonSerializer.Deserialize<AniListResponse>(content, JsonOptions);
                var mediaList = result?.Data?.Page?.Media ?? [];
                if (mediaList.Count > 0)
                {
                    SetInCache(cacheKey, mediaList, TimeSpan.FromMinutes(5));
                }
                return mediaList;
            }

            AppLogger.Warn("AniListTrackingService", $"Búsqueda en vivo '{busqueda}' falló con HTTP {(int)response.StatusCode}.");
            return [];
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (Exception ex)
        {
            AppLogger.Error("AniListTrackingService", $"Error al buscar animes en vivo para '{busqueda}'", ex);
            return [];
        }
    }

    public async Task<List<AniListMedia>> ObtenerAnimesTendenciaAsync(System.Threading.CancellationToken cancellationToken = default)
    {
        const string cacheKey = "tendencias_anilist";
        if (TryGetFromCache<List<AniListMedia>>(cacheKey, out var cachedList))
        {
            return cachedList!;
        }

        try
        {
            var query = @"
            query {
                Page (page: 1, perPage: 24) {
                    media (type: ANIME, sort: TRENDING_DESC) {
                        id
                        idMal
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

            var payload = new { query };
            var jsonContent = JsonSerializer.Serialize(payload, JsonOptions);

            var request = CrearRequest(jsonContent);
            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                if (content.Contains("\"errors\""))
                {
                    AppLogger.Error("AniListTrackingService", $"AniList devolvió errores GraphQL obteniendo tendencias: {Truncar(content)}", null);
                    return [];
                }
                var result = JsonSerializer.Deserialize<AniListResponse>(content, JsonOptions);
                var mediaList = result?.Data?.Page?.Media ?? [];
                if (mediaList.Count > 0)
                {
                    SetInCache(cacheKey, mediaList, TimeSpan.FromMinutes(15));
                }
                return mediaList;
            }

            AppLogger.Warn("AniListTrackingService", $"Obtener tendencias falló con HTTP {(int)response.StatusCode}.");
            return [];
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (Exception ex)
        {
            AppLogger.Error("AniListTrackingService", "Error al obtener animes en tendencia", ex);
            return [];
        }
    }

    public async Task<List<AiringEpisode>> ObtenerCalendarioEmisionAsync(List<int> mediaIds, long inicioSemana, long finSemana)
    {
        if (mediaIds == null || mediaIds.Count == 0) return [];

        var validIds = mediaIds.Where(id => id > 0).Distinct().ToList();
        if (validIds.Count == 0) return [];

        // Caché corta: evita golpear la API en cada navegación al calendario (rate-limit de AniList)
        string cacheKey = $"calendario_{inicioSemana}_{finSemana}_{string.Join(',', validIds.OrderBy(i => i))}";
        if (TryGetFromCache<List<AiringEpisode>>(cacheKey, out var cached))
        {
            return cached!;
        }

        var query = @"
        query ($mediaIds: [Int], $airingAt_greater: Int, $airingAt_lesser: Int) {
          Page (page: 1, perPage: 50) {
            airingSchedules (mediaId_in: $mediaIds, airingAt_greater: $airingAt_greater, airingAt_lesser: $airingAt_lesser, sort: TIME) {
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
                mediaIds = validIds,
                airingAt_greater = (int)inicioSemana,
                airingAt_lesser = (int)finSemana
            }
        };

        try
        {
            var jsonContent = JsonSerializer.Serialize(requestBody, JsonOptions);
            var request = CrearRequest(jsonContent);
            var response = await _httpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                AppLogger.Warn("AniListTrackingService", $"Calendario de emisión falló con HTTP {(int)response.StatusCode}.");
                return [];
            }

            var jsonResponse = await response.Content.ReadAsStringAsync();
            if (jsonResponse.Contains("\"errors\""))
            {
                AppLogger.Error("AniListTrackingService", $"AniList devolvió errores GraphQL en el calendario: {Truncar(jsonResponse)}", null);
                return [];
            }

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

            if (airingList.Count > 0)
            {
                SetInCache(cacheKey, airingList, TimeSpan.FromMinutes(5));
            }
            else
            {
                // Respuesta válida pero sin emisiones esta semana: caché corta para no
                // martillar la API en cada navegación (las fallas NO se cachean)
                SetInCache(cacheKey, airingList, TimeSpan.FromSeconds(60));
            }
            return airingList;
        }
        catch (Exception ex)
        {
            AppLogger.Error("AniListTrackingService", "Error al obtener calendario de emisión", ex);
            return [];
        }
    }

    private static string Truncar(string texto) => texto.Length <= 400 ? texto : texto[..400] + "...";
}