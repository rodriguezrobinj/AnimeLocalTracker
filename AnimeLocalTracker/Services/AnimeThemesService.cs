using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using Polly;
using Polly.Extensions.Http;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Resuelve OP/ED contra la API pública de AnimeThemes.moe en dos pasos:
/// 1) AniList ID → slugs de los animes en AnimeThemes (normalmente uno, a veces varios), vía su recurso
///    "resource" de mapeos externos (evita el matching frágil por título que usan otras integraciones).
/// 2) cada slug → lista de AnimeThemeDto con sus animethemeentries.videos.audio ya incluidos.
/// Solo se usa el link de "audio" (.ogg, pista sola) — nunca el .webm de video, para no gastar
/// ancho de banda en algo que el usuario solo quiere escuchar.
/// Se piden solo los campos que se usan (fields[...]) y comprimidos: One Piece pasa de 72 KB a ~3 KB. Cada lista se guarda
/// además en disco: al abrir la ficha otra vez sale al instante y, si AnimeThemes no responde, se usa la última conocida.
/// </summary>
public class AnimeThemesService : IAnimeThemesService
{
    private readonly HttpClient _httpClient;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly ConcurrentDictionary<int, CacheEntry<List<AnimeThemeInfo>>> _cache = new();
    private const int MaxCacheEntries = 300;

    /// <summary>Una lista guardada en disco más reciente que esto se usa tal cual, sin preguntar a AnimeThemes.</summary>
    internal static readonly TimeSpan FrescuraCatalogoEnDisco = TimeSpan.FromHours(12);

    /// <summary>Solo los campos que se leen (ver AnimeThemeModels). Los que no se piden no viajan.</summary>
    internal const string CamposAnime =
        "&fields[anime]=slug,name&fields[animetheme]=slug,type&fields[animethemeentry]=version,episodes,spoiler,notes" +
        "&fields[video]=basename&fields[audio]=link,size&fields[song]=title&fields[performance]=id&fields[artist]=name";

    /// <summary>Formato del JSON guardado; si cambia, los archivos viejos se ignoran (se vuelven a pedir).</summary>
    internal const int FormatoCatalogo = 2; // 2: tamaño, notas y nombre del anime

    private readonly string? _carpetaCatalogo;

    /// <summary>Un Retry-After más largo que esto no se espera: se da el intento por fallido y se vuelve a pedir la próxima vez.</summary>
    internal static readonly TimeSpan EsperaMaximaPedidaPorServidor = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Reintentos propios de AnimeThemes (2 reintentos, 1 s y 2 s). NO se usa la política de AniList: esa espera 60 s entre
    /// intentos y el HttpClient de este servicio corta a los 20 s, así que ante un error 500 el reintento nunca llegaba, la
    /// ficha se quedaba 20 s sin música y el respaldo "sin artistas" de <see cref="ObtenerAnimeAsync"/> no se ejecutaba jamás.
    /// </summary>
    /// <param name="esperaBase">Solo para pruebas: espera del primer reintento (el segundo espera el doble).</param>
    public static IAsyncPolicy<HttpResponseMessage> CrearPoliticaReintentos(TimeSpan? esperaBase = null)
    {
        var espera = esperaBase ?? TimeSpan.FromSeconds(1);
        return PoliticasHttp.ErroresPasajeros() // sin internet no se reintenta
            .OrResult(EsLimiteDePeticionesReintentable)
            .WaitAndRetryAsync(
                retryCount: 2,
                sleepDurationProvider: (intento, resultado, _) =>
                    EsperaPedida(resultado?.Result) is { } pedida && pedida <= EsperaMaximaPedidaPorServidor ? pedida : espera * intento,
                onRetryAsync: (resultado, tiempo, intento, _) =>
                {
                    string motivo = resultado.Result != null ? $"HTTP {(int)resultado.Result.StatusCode}" : resultado.Exception?.Message ?? "?";
                    AppLogger.Debug("AnimeThemesService", $"Reintento {intento} en {tiempo.TotalSeconds:0.#} s ({motivo}).");
                    return Task.CompletedTask;
                });
    }

    /// <summary>Un 429 (límite de 90 peticiones/min de AnimeThemes) se reintenta solo si el servidor no pide esperar demasiado.</summary>
    internal static bool EsLimiteDePeticionesReintentable(HttpResponseMessage respuesta) =>
        respuesta.StatusCode == HttpStatusCode.TooManyRequests
        && (EsperaPedida(respuesta) is not { } pedida || pedida <= EsperaMaximaPedidaPorServidor);

    /// <summary>Lo que el servidor pide esperar (cabecera Retry-After), si lo dice.</summary>
    internal static TimeSpan? EsperaPedida(HttpResponseMessage? respuesta)
    {
        var retryAfter = respuesta?.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta) return delta;
        if (retryAfter?.Date is { } fecha) return fecha - DateTimeOffset.UtcNow;
        return null;
    }

    /// <param name="carpetaCatalogo">Dónde guardar las listas en disco; null = no se guardan (lo normal en pruebas, que nunca deben
    /// escribir en la carpeta de datos del usuario). La app pasa <see cref="AppDataPaths.AnimeThemesCatalogDir"/>.</param>
    public AnimeThemesService(HttpClient httpClient, string? carpetaCatalogo = null)
    {
        _httpClient = httpClient;
        _carpetaCatalogo = carpetaCatalogo;

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

        var guardado = LeerCatalogoGuardado(aniListId);
        var edad = guardado == null ? TimeSpan.MaxValue : DateTime.UtcNow - guardado.GuardadoUtc;
        if (guardado != null && edad >= TimeSpan.Zero && edad < FrescuraCatalogoEnDisco)
        {
            BoundedCache.Insert(_cache, aniListId, guardado.Temas, MaxCacheEntries, FrescuraCatalogoEnDisco - edad);
            return guardado.Temas;
        }

        var (temas, completo) = await ConsultarApiAsync(aniListId, ct);
        if (ct.IsCancellationRequested) return [];

        if (temas == null || (!completo && guardado != null && guardado.Temas.Count > temas.Count))
        {
            // AnimeThemes no respondió (o respondió a medias): mejor la última lista conocida que nada. Se recuerda poco
            // tiempo para volver a intentarlo pronto.
            if (guardado != null)
            {
                AppLogger.Debug("AnimeThemesService", $"Usando la lista guardada de AniListId {aniListId} ({guardado.GuardadoUtc:yyyy-MM-dd HH:mm} UTC): AnimeThemes no respondió bien.");
                BoundedCache.Insert(_cache, aniListId, guardado.Temas, MaxCacheEntries, TimeSpan.FromMinutes(5));
                return guardado.Temas;
            }
            if (temas == null) return [];
        }

        if (completo)
        {
            BoundedCache.Insert(_cache, aniListId, temas, MaxCacheEntries, TimeSpan.FromHours(6));
            GuardarCatalogo(aniListId, temas);
        }
        else
        {
            // Si algún anime falló, se cachea poco para reintentar pronto en vez de esconderlo 6 horas.
            BoundedCache.Insert(_cache, aniListId, temas, MaxCacheEntries, TimeSpan.FromMinutes(5));
        }
        return temas;
    }

    /// <summary>
    /// Pregunta a AnimeThemes. Temas null = la API falló (no se sabe nada); Completo false = faltó alguno de los animes enlazados.
    /// Un anime que no está en AnimeThemes es una respuesta completa con lista vacía.
    /// </summary>
    private async Task<(List<AnimeThemeInfo>? Temas, bool Completo)> ConsultarApiAsync(int aniListId, CancellationToken ct)
    {
        try
        {
            var slugs = await ResolverSlugsAsync(aniListId, ct);
            if (slugs == null)
            {
                // La API respondió con error (Cloudflare 403, 429, 5xx): NO es "este anime no tiene temas".
                return (null, false);
            }
            if (slugs.Count == 0) return ([], true);

            var animes = new List<AnimeThemesAnimeDto>();
            bool huboFallos = false;
            foreach (string slug in slugs)
            {
                var anime = await ObtenerAnimeAsync(slug, ct);
                if (anime == null) huboFallos = true;
                else animes.Add(anime);
            }

            // Fallaron todos: igual que arriba.
            if (animes.Count == 0) return (null, false);

            return (MapearVarios(animes), !huboFallos);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelado por el llamador (se cambió de ficha antes de terminar): silencioso, es normal.
            return (null, false);
        }
        catch (OperationCanceledException ex)
        {
            // El token del llamador NO pidió cancelar: fue el timeout propio del HttpClient (20s).
            AppLogger.Debug("AnimeThemesService", $"Timeout consultando AnimeThemes.moe para AniListId {aniListId}: {ex.Message}");
            return (null, false);
        }
        catch (Exception ex)
        {
            AppLogger.Debug("AnimeThemesService", $"Error obteniendo openings/endings para AniListId {aniListId}: {ex.Message}");
            return (null, false);
        }
    }

    internal sealed record CatalogoGuardado(int Formato, DateTime GuardadoUtc, List<AnimeThemeInfo> Temas);

    private string? RutaCatalogo(int aniListId) =>
        _carpetaCatalogo == null ? null : Path.Combine(_carpetaCatalogo, aniListId + ".json");

    private CatalogoGuardado? LeerCatalogoGuardado(int aniListId)
    {
        string? ruta = RutaCatalogo(aniListId);
        if (ruta == null || !File.Exists(ruta)) return null;

        try
        {
            var guardado = JsonSerializer.Deserialize<CatalogoGuardado>(File.ReadAllText(ruta), JsonOptions);
            return guardado is { Formato: FormatoCatalogo, Temas: not null } ? guardado : null;
        }
        catch (Exception ex)
        {
            AppLogger.Debug("AnimeThemesService", $"Lista guardada ilegible para AniListId {aniListId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Escribe primero a un temporal y lo mueve: un cierre a mitad nunca deja un JSON cortado.</summary>
    private void GuardarCatalogo(int aniListId, List<AnimeThemeInfo> temas)
    {
        string? ruta = RutaCatalogo(aniListId);
        if (ruta == null) return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
            string temporal = ruta + ".tmp";
            File.WriteAllText(temporal, JsonSerializer.Serialize(new CatalogoGuardado(FormatoCatalogo, DateTime.UtcNow, temas)));
            File.Move(temporal, ruta, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLogger.Debug("AnimeThemesService", $"No se pudo guardar la lista de AniListId {aniListId}: {ex.Message}");
        }
    }

    /// <summary>Slugs del anime en AnimeThemes: lista vacía = no está en su catálogo; null = la API falló (no se sabe).</summary>
    private async Task<List<string>?> ResolverSlugsAsync(int aniListId, CancellationToken ct)
    {
        string url = $"https://api.animethemes.moe/resource?filter[site]=AniList&filter[external_id]={aniListId}&include=anime&fields[resource]=id&fields[anime]=slug";
        using var respuesta = await _httpClient.GetAsync(url, ct);
        if (!respuesta.IsSuccessStatusCode)
        {
            AppLogger.Debug("AnimeThemesService", $"AnimeThemes respondió HTTP {(int)respuesta.StatusCode} al buscar AniListId {aniListId}.");
            return null;
        }

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

        var respuesta = await _httpClient.GetAsync(urlBase + ".performances.artist" + CamposAnime, ct);
        if (!respuesta.IsSuccessStatusCode)
        {
            // Si la API falla al expandir artistas (ya pasó con "song.artists" → 500), los temas
            // siguen siendo útiles sin ellos: se reintenta sin esa relación en vez de dejar vacío.
            AppLogger.Debug("AnimeThemesService", $"Include de artistas falló (HTTP {(int)respuesta.StatusCode}) para '{slug}'; reintentando sin artistas.");
            respuesta = await _httpClient.GetAsync(urlBase + CamposAnime, ct);
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
                var audio = entrada.Videos?.FirstOrDefault(v => v.Audio != null && !string.IsNullOrWhiteSpace(v.Audio.Link))?.Audio;
                string? audioUrl = audio?.Link;
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
                    AudioUrlOgg = audioUrl,
                    TamanoBytes = audio?.Size is > 0 ? audio.Size : null,
                    Notas = string.IsNullOrWhiteSpace(entrada.Notes) ? null : entrada.Notes.Trim(),
                    NombreAnime = string.IsNullOrWhiteSpace(anime.Name) ? null : anime.Name
                });
            }
        }
        return resultado;
    }
}
