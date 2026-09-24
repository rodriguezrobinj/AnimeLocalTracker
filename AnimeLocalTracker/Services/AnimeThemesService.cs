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
/// 1) AniList ID → slug del anime en AnimeThemes, vía su recurso "resource" de mapeos externos
///    (evita el matching frágil por título que usan otras integraciones de este proyecto).
/// 2) slug → lista de AnimeThemeDto con sus animethemeentries.videos.audio ya incluidos.
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
            string? slug = await ResolverSlugAsync(aniListId, ct);
            if (string.IsNullOrWhiteSpace(slug))
            {
                // Sin coincidencia en AnimeThemes: se cachea vacío por más tiempo (no va a aparecer solo).
                BoundedCache.Insert(_cache, aniListId, [], MaxCacheEntries, TimeSpan.FromHours(6));
                return [];
            }

            string url = "https://api.animethemes.moe/anime/" + Uri.EscapeDataString(slug) +
                         "?include=animethemes.animethemeentries.videos.audio,animethemes.song.artists";

            var respuesta = await _httpClient.GetAsync(url, ct);
            if (!respuesta.IsSuccessStatusCode)
            {
                BoundedCache.Insert(_cache, aniListId, [], MaxCacheEntries, TimeSpan.FromMinutes(30));
                return [];
            }

            var json = await respuesta.Content.ReadAsStringAsync(ct);
            var dto = JsonSerializer.Deserialize<AnimeThemesAnimeResponse>(json, JsonOptions);
            var temas = MapearTemas(dto?.Anime);

            BoundedCache.Insert(_cache, aniListId, temas, MaxCacheEntries, TimeSpan.FromHours(6));
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

    private async Task<string?> ResolverSlugAsync(int aniListId, CancellationToken ct)
    {
        string url = $"https://api.animethemes.moe/resource?filter[site]=AniList&filter[external_id]={aniListId}&include=anime";
        var respuesta = await _httpClient.GetAsync(url, ct);
        if (!respuesta.IsSuccessStatusCode) return null;

        var json = await respuesta.Content.ReadAsStringAsync(ct);
        var dto = JsonSerializer.Deserialize<AnimeThemesResourceResponse>(json, JsonOptions);
        return dto?.Resource?.FirstOrDefault()?.Anime?.FirstOrDefault()?.Slug;
    }

    internal static List<AnimeThemeInfo> MapearTemas(AnimeThemesAnimeDto? anime)
    {
        var resultado = new List<AnimeThemeInfo>();
        if (anime?.AnimeThemes == null) return resultado;

        foreach (var tema in anime.AnimeThemes)
        {
            if (string.IsNullOrWhiteSpace(tema.Slug) || string.IsNullOrWhiteSpace(tema.Type)) continue;

            string artistas = string.Join(", ", tema.Song?.Artists?.Select(a => a.Name).Where(n => !string.IsNullOrWhiteSpace(n)) ?? []);

            foreach (var entrada in tema.AnimeThemeEntries)
            {
                string? audioUrl = entrada.Videos?.FirstOrDefault(v => v.Audio != null && !string.IsNullOrWhiteSpace(v.Audio.Link))?.Audio?.Link;
                if (string.IsNullOrWhiteSpace(audioUrl)) continue;

                resultado.Add(new AnimeThemeInfo
                {
                    Slug = tema.Slug,
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
