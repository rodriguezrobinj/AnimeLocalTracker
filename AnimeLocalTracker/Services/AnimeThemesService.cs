using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Resuelve OP/ED contra la API pública de AnimeThemes.moe en dos pasos:
/// 1) AniList ID → slugs de los animes en AnimeThemes (normalmente uno, a veces varios), vía su recurso
///    "resource" de mapeos externos (evita el matching frágil por título que usan otras integraciones).
/// 2) cada slug → lista de AnimeThemeDto con sus animethemeentries.videos.audio ya incluidos.
/// Solo se usa el link de "audio" (.ogg, pista sola) — nunca el .webm de video, para no gastar
/// ancho de banda en algo que el usuario solo quiere escuchar.
/// </summary>
public class AnimeThemesService : IAnimeThemesService
{
    private readonly HttpClient _httpClient;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly ConcurrentDictionary<int, CacheEntry<List<AnimeThemeInfo>>> _cache = new();
    private const int MaxCacheEntries = 300;

    public AnimeThemesService(HttpClient httpClient)
    {
        _httpClient = httpClient;

        try
        {
            if (_httpClient.Timeout.TotalSeconds >= 100)
            {
                _httpClient.Timeout = TimeSpan.FromSeconds(20);
            }

            // api.animethemes.moe está detrás de Cloudflare y bloquea con 403 cualquier petición
            // sin User-Agent (HttpClient no manda uno por defecto) — mismo motivo por el que
            // AniSkipService ya necesita fijar el suyo.
            if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
            {
                _httpClient.DefaultRequestHeaders.Add("User-Agent", "AnimeLocalTracker/1.0");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("AnimeThemesService", $"No se pudo configurar el HttpClient: {ex.Message}");
        }
    }

    public async Task<List<AnimeThemeInfo>> ObtenerTemasAsync(int aniListId, CancellationToken ct = default)
    {
        if (aniListId <= 0) return [];

        if (_cache.TryGetValue(aniListId, out var cached) && DateTime.UtcNow < cached.Expiration)
        {
            return cached.Data;
        }

        try
        {
            var slugs = await ResolverSlugsAsync(aniListId, ct);
            if (slugs.Count == 0)
            {
                // Sin coincidencia en AnimeThemes: se cachea vacío por más tiempo (no va a aparecer solo).
                BoundedCache.Insert(_cache, aniListId, [], MaxCacheEntries, TimeSpan.FromHours(6));
                return [];
            }

            var animes = new List<AnimeThemesAnimeDto>();
            bool huboFallos = false;
            foreach (string slug in slugs)
            {
                var anime = await ObtenerAnimeAsync(slug, ct);
                if (anime == null) huboFallos = true;
                else animes.Add(anime);
            }

            var temas = MapearVarios(animes);

            // Si algún anime falló, se cachea poco para reintentar pronto en vez de esconderlo 6 horas.
            BoundedCache.Insert(_cache, aniListId, temas, MaxCacheEntries,
                huboFallos ? TimeSpan.FromMinutes(30) : TimeSpan.FromHours(6));
            return temas;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelado por el llamador (se cambió de ficha antes de terminar): silencioso, es normal.
            return [];
        }
        catch (OperationCanceledException ex)
        {
            // El token del llamador NO pidió cancelar: fue el timeout propio del HttpClient (20s).
            // Antes esto se confundía con una cancelación normal y quedaba invisible en el log.
            AppLogger.Debug("AnimeThemesService", $"Timeout consultando AnimeThemes.moe para AniListId {aniListId}: {ex.Message}");
            return [];
        }
        catch (Exception ex)
        {
            AppLogger.Debug("AnimeThemesService", $"Error obteniendo openings/endings para AniListId {aniListId}: {ex.Message}");
            return [];
        }
    }

    private async Task<List<string>> ResolverSlugsAsync(int aniListId, CancellationToken ct)
    {
        string url = $"https://api.animethemes.moe/resource?filter[site]=AniList&filter[external_id]={aniListId}&include=anime";
        var respuesta = await _httpClient.GetAsync(url, ct);
        if (!respuesta.IsSuccessStatusCode) return [];

        var json = await respuesta.Content.ReadAsStringAsync(ct);
        var dto = JsonSerializer.Deserialize<AnimeThemesResourceResponse>(json, JsonOptions);
        return ExtraerSlugs(dto);
    }

    /// <summary>
    /// Todos los animes de AnimeThemes enlazados a un ID de AniList, en el orden en que los devuelve la API.
    /// Un mismo ID puede tener varios "resources" (Re:Zero OVAs → 2 OVAs distintos) y alguno puede venir
    /// sin anime (Grand Blue S3: el primero vacío y el segundo con el anime); leer solo el primero
    /// perdía sus openings/endings.
    /// </summary>
    internal static List<string> ExtraerSlugs(AnimeThemesResourceResponse? respuesta)
    {
        return (respuesta?.Resource ?? [])
            .SelectMany(r => r.Anime ?? [])
            .Select(a => a.Slug)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Descarga un anime con sus temas. Null si la API falla (se reintenta sin artistas antes de rendirse).</summary>
    private async Task<AnimeThemesAnimeDto?> ObtenerAnimeAsync(string slug, CancellationToken ct)
    {
        string urlBase = "https://api.animethemes.moe/anime/" + Uri.EscapeDataString(slug) +
                         "?include=animethemes.animethemeentries.videos.audio,animethemes.song";

        var respuesta = await _httpClient.GetAsync(urlBase + ".performances.artist", ct);
        if (!respuesta.IsSuccessStatusCode)
        {
            // Si la API falla al expandir artistas (ya pasó con "song.artists" → 500), los temas
            // siguen siendo útiles sin ellos: se reintenta sin esa relación en vez de dejar vacío.
            AppLogger.Debug("AnimeThemesService", $"Include de artistas falló (HTTP {(int)respuesta.StatusCode}) para '{slug}'; reintentando sin artistas.");
            respuesta = await _httpClient.GetAsync(urlBase, ct);
        }
        if (!respuesta.IsSuccessStatusCode) return null;

        var json = await respuesta.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<AnimeThemesAnimeResponse>(json, JsonOptions)?.Anime;
    }

    /// <summary>
    /// Une los temas de varios animes de AnimeThemes que comparten un mismo ID de AniList. El primero
    /// conserva sus slugs (los mp3 ya descargados siguen valiendo); a los siguientes se les añade un sufijo
    /// ("ED1-2") porque el nombre del archivo local se arma con tipo+slug+versión+rango y dos OVAs con "ED1"
    /// compartirían archivo: descargar uno marcaría el otro como descargado y lo pisaría.
    /// </summary>
    internal static List<AnimeThemeInfo> MapearVarios(IReadOnlyList<AnimeThemesAnimeDto> animes)
    {
        var resultado = new List<AnimeThemeInfo>();
        for (int i = 0; i < animes.Count; i++)
        {
            resultado.AddRange(MapearTemas(animes[i], i == 0 ? null : $"-{i + 1}"));
        }
        return resultado;
    }

    internal static List<AnimeThemeInfo> MapearTemas(AnimeThemesAnimeDto? anime, string? sufijoSlug = null)
    {
        var resultado = new List<AnimeThemeInfo>();
        if (anime?.AnimeThemes == null) return resultado;

        foreach (var tema in anime.AnimeThemes)
        {
            if (string.IsNullOrWhiteSpace(tema.Slug) || string.IsNullOrWhiteSpace(tema.Type)) continue;

            var nombresArtistas = (tema.Song?.Performances ?? [])
                .Select(p => p.Artist?.Name)
                .Concat((tema.Song?.Artists ?? []).Select(a => a.Name))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            string artistas = string.Join(", ", nombresArtistas);

            foreach (var entrada in tema.AnimeThemeEntries)
            {
                string? audioUrl = entrada.Videos?.FirstOrDefault(v => v.Audio != null && !string.IsNullOrWhiteSpace(v.Audio.Link))?.Audio?.Link;
                if (string.IsNullOrWhiteSpace(audioUrl)) continue;

                resultado.Add(new AnimeThemeInfo
                {
                    Slug = tema.Slug + sufijoSlug,
                    Tipo = tema.Type,
                    Version = entrada.Version ?? 1,
                    TituloCancion = tema.Song?.Title ?? string.Empty,
                    Artistas = artistas,
                    RangoEpisodios = entrada.Episodes,
                    EsSpoiler = entrada.Spoiler,
                    AudioUrlOgg = audioUrl
                });
            }
        }
        return resultado;
    }
}
