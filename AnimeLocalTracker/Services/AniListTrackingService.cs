// 2. La Implementación (AniListService.cs)
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

public class AniListTrackingService(HttpClient httpClient) : IAnimeTrackingService
{
    private readonly HttpClient _httpClient = httpClient;
    
    public async Task<AniListMedia?> ObtenerAnimePorIdAsync(int id)
    {
        try
        {

            var query = @"
            query ($id: Int) {
                Media(id: $id, type: ANIME) {
                    id
                    title { romaji }
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
            
            var jsonContent = System.Text.Json.JsonSerializer.Serialize(payload);
            var requestContent = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("https://graphql.anilist.co", requestContent);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var result = System.Text.Json.JsonSerializer.Deserialize<AniListResponse>(content);
                return result?.Data?.Media; // Fíjate que devolvemos un solo objeto, no una lista
            }
            return null;
        }
        catch { return null; }
    }

    public async Task<List<AniListMedia>> BuscarAnimePorTituloAsync(string titulo)
    {
        // Cambiamos perPage: 1 a perPage: 5
        var query = @"
            query ($search: String) {
                Page(page: 1, perPage: 5) { 
                    media(search: $search, type: ANIME) {
                        id
                        title { romaji }
                        coverImage { extraLarge }
                        description(asHtml: false) 
                        genres
                        episodes
                        nextAiringEpisode { episode } 
                    }
                }
            }";

        var requestBody = new { query, variables = new { search = titulo } };
        var jsonContent = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

        try
        {
            var response = await _httpClient.PostAsync("https://graphql.anilist.co", jsonContent);
            response.EnsureSuccessStatusCode();

            var jsonResponse = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<AniListResponse>(jsonResponse);

            // Devolvemos la lista completa, o una lista vacía si viene nulo
            return result?.Data?.Page?.Media ?? []; 
        }
        catch (HttpRequestException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error de red: {ex.Message}");
            return [];
        }
    }
    
    public async Task<bool> ActualizarProgresoAsync(int mediaId, int episodio, string token)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "https://graphql.anilist.co");
            
            // LA CLAVE DE LA CIBERSEGURIDAD: Nos identificamos con el token interceptado
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

            var jsonContent = System.Text.Json.JsonSerializer.Serialize(payload);
            request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            
            // Si el servidor responde con código 200 (OK), la nube fue actualizada
            return response.IsSuccessStatusCode; 
        }
        catch
        {
            return false;
        }
    }
    
    public async Task<AniListMediaList?> ObtenerSeguimientoUsuarioAsync(int mediaId, string token)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "https://graphql.anilist.co");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            // CORRECCIÓN: Le preguntamos al Anime por TU entrada específica (mediaListEntry)
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
            var jsonContent = System.Text.Json.JsonSerializer.Serialize(payload);
            var requestContent = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");

            request.Content = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var result = System.Text.Json.JsonSerializer.Deserialize<AniListResponse>(content);
                
                // Extraemos la información de la nueva ruta
                return result?.Data?.Media?.MediaListEntry; 
            }
            return null;
        }
        catch { return null; }
    }

    public async Task<bool> GuardarSeguimientoUsuarioAsync(int mediaId, string estado, int progreso, float puntaje, System.DateTime? fechaInicio, System.DateTime? fechaFin, string token)
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
            var jsonContent = System.Text.Json.JsonSerializer.Serialize(payload);
            var requestContent = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");

            request.Content = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            
            // === EL ANTÍDOTO CONTRA EL FALSO POSITIVO ===
            var content = await response.Content.ReadAsStringAsync();
            
            // Si GraphQL detecta un error de validación, incrusta un arreglo llamado "errors"
            if (content.Contains("\"errors\""))
            {
                // Extraemos el mensaje crudo para que veas EXACTAMENTE qué regla rompiste
                System.Windows.MessageBox.Show($"AniList rechazó los datos.\n\nRespuesta del servidor:\n{content}", "Fallo de Validación", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                return false;
            }

            return response.IsSuccessStatusCode;
        }
        catch 
        { 
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
            var jsonContent = System.Text.Json.JsonSerializer.Serialize(payload);
            var requestContent = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");

            request.Content = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var result = System.Text.Json.JsonSerializer.Deserialize<AniListResponse>(content);
                return result?.Data?.Viewer;
            }
            return null;
        }
        catch { return null; }
    }
    
    public async Task<List<AniListMedia>> BuscarAnimesEnVivoAsync(string busqueda)
    {
        try
        {

            // Petición GraphQL optimizada para el buscador
            var query = @"
            query ($search: String) {
                Page (page: 1, perPage: 8) {
                    media (search: $search, type: ANIME) {
                        id
                        title { romaji }
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
            var jsonContent = System.Text.Json.JsonSerializer.Serialize(payload);
            var requestContent = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("https://graphql.anilist.co", requestContent);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var result = System.Text.Json.JsonSerializer.Deserialize<AniListResponse>(content);
                return result?.Data?.Page?.Media ?? [];
            }
            return [];
        }
        catch { return []; }
    }
}