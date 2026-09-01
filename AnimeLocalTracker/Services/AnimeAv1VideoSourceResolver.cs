using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Services;

/// <summary>
/// INT-01: contrato tipado del scraping de animeav1.com — todo el parseo de HTML vive
/// aquí, aislado de la red, para poder testearse con fixtures reales.
/// </summary>
public static partial class AnimeAv1HtmlParser
{
    /// <summary>Servidor de video publicado por la página de episodio.</summary>
    public readonly record struct EmbedServidor(string Server, string Url);

    /// <summary>
    /// Extrae los embeds de servidores de la página de episodio (SvelteKit). El sitio
    /// incrusta la lista en JSON: embeds:{SUB:[{server:"HLS",url:"https://..."},...]}.
    /// Solo se devuelven URLs https; el filtrado por host permitido lo hace UrlSeguridad.
    /// </summary>
    public static List<EmbedServidor> ExtraerEmbeds(string html)
    {
        var lista = new List<EmbedServidor>();
        if (string.IsNullOrWhiteSpace(html)) return lista;

        int inicio = html.IndexOf("embeds:{", StringComparison.Ordinal);
        if (inicio < 0) return lista;

        int fin = html.IndexOf("]}", inicio, StringComparison.Ordinal);
        string seccion = fin > inicio ? html[inicio..fin] : html[inicio..];

        foreach (Match m in EmbedServidorRegex().Matches(seccion))
        {
            string server = m.Groups[1].Value.Trim();
            string url = m.Groups[2].Value.Trim();
            if (!string.IsNullOrWhiteSpace(server) && !string.IsNullOrWhiteSpace(url))
            {
                lista.Add(new EmbedServidor(server, url));
            }
        }
        return lista;
    }

    /// <summary>
    /// Orden de preferencia de servidores para la resolución (Fase 1):
    /// MP4Upload directo primero, luego los que resuelve yt-dlp (Voe, UPNShare, HLS,
    /// Byse). Mega se excluye (requiere API propia de mega.nz, fuera de esta fase).
    /// </summary>
    public static List<EmbedServidor> OrdenarEmbedsPorPreferencia(IEnumerable<EmbedServidor> embeds)
    {
        var preferencia = new[] { "MP4Upload", "Voe", "UPNShare", "HLS", "Byse" };
        return preferencia
            .SelectMany((nombre, i) => embeds
                .Where(e => e.Server.Equals(nombre, StringComparison.OrdinalIgnoreCase))
                .Select(e => (e, i)))
            .OrderBy(x => x.i)
            .Select(x => x.e)
            .ToList();
    }

    /// <summary>Extrae el ID de un embed de MP4Upload desde una página de animeav1.com.</summary>
    public static string? ExtraerMp4UploadId(string html)
    {
        var match = Mp4UploadRegex().Match(html ?? string.Empty);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Extrae la URL directa .mp4/.mkv de player.src en una página de MP4Upload.</summary>
    public static string? ExtraerVideoDirecto(string html)
    {
        var match = DirectVideoSrcRegex().Match(html ?? string.Empty);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Extrae el MAL ID del anime desde la página del episodio. El payload de
    /// SvelteKit expone media:{...slug:"x",malId:62542,...}; el par slug+malId es
    /// inequívoco SIEMPRE QUE se busque tras votes: — los géneros también tienen
    /// pares slug:"fantasia",malId:10 ANTES del media y contaminarían la primera
    /// coincidencia. votes solo existe en el media.
    /// </summary>
    public static int? ExtraerMalIdDelMedia(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        int inicio = html.IndexOf("votes:", StringComparison.Ordinal);
        string seccion = inicio >= 0 ? html[inicio..] : html;

        var m = MalIdMediaRegex().Match(seccion);
        return m.Success && int.TryParse(m.Groups[1].Value, out var id) ? id : null;
    }

    /// <summary>
    /// Extrae los episodios reales del media desde el payload: episodes:[{id:21013,number:14},...].
    /// La numeración del sitio puede diferir de la de la app (películas numeradas por
    /// posición en el catálogo) — esto permite resolver el número correcto.
    /// </summary>
    public static List<(int Id, int Numero)> ExtraerEpisodiosDelMedia(string html)
    {
        var lista = new List<(int, int)>();
        if (string.IsNullOrWhiteSpace(html)) return lista;

        int inicio = html.IndexOf("episodes:[", StringComparison.Ordinal);
        if (inicio < 0) return lista;
        int fin = html.IndexOf(']', inicio);
        if (fin <= inicio) return lista;

        foreach (Match m in EpisodioMediaRegex().Matches(html.Substring(inicio, fin - inicio)))
        {
            if (int.TryParse(m.Groups[1].Value, out var id) && int.TryParse(m.Groups[2].Value, out var numero))
            {
                lista.Add((id, numero));
            }
        }
        return lista;
    }

    /// <summary>Extrae slugs candidatos de /media/{slug} o slug:"{slug}" (sin "catalogo").</summary>
    public static IEnumerable<string> ExtraerSlugs(string html)
    {
        var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in SlugsRegex().Matches(html ?? string.Empty))
        {
            string slug = match.Groups[1].Value.Trim('"', '\'');
            if (!string.IsNullOrWhiteSpace(slug) && !slug.Equals("catalogo", StringComparison.OrdinalIgnoreCase))
            {
                slugs.Add(slug);
            }
        }
        return slugs;
    }

    // El JSON del sitio usa claves SIN comillas: {server:"HLS",url:"https://..."}
    [GeneratedRegex(@"server\s*:\s*""([^""]+)""\s*,\s*url\s*:\s*""(https?://[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex EmbedServidorRegex();

    [GeneratedRegex(@"\{id:(\d+),number:(\d+)\}")]
    private static partial Regex EpisodioMediaRegex();

    [GeneratedRegex(@"slug:""[^""]*"",malId:(\d+)")]
    private static partial Regex MalIdMediaRegex();

    [GeneratedRegex(@"(?:/media/|slug:\s*""?)([a-zA-Z0-9_-]+)")]
    private static partial Regex SlugsRegex();

    [GeneratedRegex(@"https?://(?:www\.)?mp4upload\.com/(?:embed-)?([a-zA-Z0-9]+)(?:\.html)?")]
    private static partial Regex Mp4UploadRegex();

    [GeneratedRegex(@"src:\s*""(https?://[^""]+?\.(?:mp4|mkv)[^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex DirectVideoSrcRegex();
}

public partial class AnimeAv1VideoSourceResolver : IVideoSourceResolver
{
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    private readonly HttpClient _httpClient;

    /// <summary>
    /// Resuelve AniListId → MAL ID (para verificar que la página encontrada es el
    /// anime correcto y no otro con nombre parecido). Nullable: sin resolver, la
    /// coincidencia queda solo en la heurística de slugs.
    /// </summary>
    private readonly Func<int, CancellationToken, Task<int?>>? _malIdResolver;

    public AnimeAv1VideoSourceResolver(
        HttpClient httpClient,
        Func<int, CancellationToken, Task<int?>>? malIdResolver = null)
    {
        _httpClient = httpClient;
        _malIdResolver = malIdResolver;
    }

    public async Task<string?> BuscarUrlEpisodioAsync(IEnumerable<string> titulos, int numeroEpisodio, int? aniListId = null, CancellationToken cancellationToken = default)
    {
        // FASE 1 (multi-servidor): obtener los embeds de la página del episodio.
        // El C# solo resuelve MP4Upload; los demás servidores los orquesta
        // ProveedorVideoAnimeAv1 con yt-dlp.
        var embeds = await ObtenerEmbedsEpisodioAsync(titulos, numeroEpisodio, aniListId, cancellationToken);
        if (embeds.Count == 0) return null;

        var mp4 = embeds.FirstOrDefault(e => e.Server.Equals("MP4Upload", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(mp4.Url)) return null;

        return await GetVideoUrlAsync(mp4.Url, cancellationToken);
    }

    /// <summary>
    /// Devuelve los embeds de servidores de la página del episodio (contrato tipado,
    /// Fase 1 multi-servidor), en el orden en que los publica el sitio y solo con
    /// hosts permitidos por la política de seguridad.
    /// </summary>
    public async Task<List<AnimeAv1HtmlParser.EmbedServidor>> ObtenerEmbedsEpisodioAsync(
        IEnumerable<string> titulos, int numeroEpisodio, int? aniListId = null, CancellationToken cancellationToken = default)
    {
        var titulosLista = titulos.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        if (titulosLista.Count == 0) return [];

        // Verificación anti-confusión: si conocemos el AniListId, resolvemos su MAL ID
        // y solo aceptamos páginas cuyo malId coincida. Con malId conocido, la
        // heurística de slugs se relaja (el malId es la verificación autoritativa:
        // los slugs de películas "movie-N-" se rechazaban aunque fueran correctos).
        int? malIdEsperado = null;
        if (aniListId.HasValue && _malIdResolver != null)
        {
            try { malIdEsperado = await _malIdResolver(aniListId.Value, cancellationToken); }
            catch (Exception ex) { AppLogger.Debug("AnimeAv1VideoSourceResolver", $"No se pudo resolver MAL ID de {aniListId}: {ex.Message}"); }
        }

        // Recolectar slugs candidatos en orden de prioridad
        var slugs = new List<string>();
        var vistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // FASE 1: slugs generados de los títulos (heurística estricta siempre)
        foreach (var titulo in titulosLista)
        {
            foreach (var slug in GenerarVariacionesSlug(titulo))
            {
                if (vistos.Add(slug) && EsSlugCompatible(slug, titulosLista)) slugs.Add(slug);
            }
        }

        // FASE 2: slugs del catálogo (con malId conocido se relaja la heurística)
        foreach (var titulo in titulosLista)
        {
            foreach (var termino in GenerarTerminosBusqueda(titulo))
            {
                try
                {
                    string searchUrl = $"https://animeav1.com/catalogo?search={Uri.EscapeDataString(termino)}";
                    using var req = new HttpRequestMessage(HttpMethod.Get, searchUrl);
                    req.Headers.Add("User-Agent", UserAgent);

                    using var res = await _httpClient.SendAsync(req, cancellationToken);
                    if (!res.IsSuccessStatusCode) continue;

                    var html = await res.Content.ReadAsStringAsync(cancellationToken);
                    foreach (string discoveredSlug in AnimeAv1HtmlParser.ExtraerSlugs(html))
                    {
                        if (vistos.Add(discoveredSlug) &&
                            (malIdEsperado.HasValue || EsSlugCompatible(discoveredSlug, titulosLista)))
                        {
                            slugs.Add(discoveredSlug);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AnimeAv1VideoSourceResolver] Error en búsqueda de catálogo para '{termino}': {ex.Message}");
                }
            }
        }

        // PRUEBA 1: página del episodio con el número solicitado
        foreach (var slug in slugs)
        {
            if (cancellationToken.IsCancellationRequested) return [];

            var embeds = await ObtenerEmbedsDePaginaAsync($"https://animeav1.com/media/{slug}/{numeroEpisodio}", malIdEsperado, cancellationToken);
            if (embeds.Count > 0) return embeds;
        }

        // PRUEBA 2 (FASE 3): la numeración del sitio puede diferir de la app
        // (películas = "Ep N" de posición en el catálogo). Página del media →
        // verificar malId → episodios reales → número objetivo → embeds.
        foreach (var slug in slugs)
        {
            if (cancellationToken.IsCancellationRequested) return [];

            int? objetivo = await ObtenerNumeroEpisodioDelMediaAsync(slug, numeroEpisodio, malIdEsperado, cancellationToken);
            if (!objetivo.HasValue || objetivo.Value == numeroEpisodio) continue;

            var embeds = await ObtenerEmbedsDePaginaAsync($"https://animeav1.com/media/{slug}/{objetivo.Value}", malIdEsperado, cancellationToken);
            if (embeds.Count > 0) return embeds;
        }

        return [];
    }

    /// <summary>
    /// Resuelve el número de episodio real del sitio desde la página del media.
    /// Coincidencia exacta si existe; para películas/especiales (1 solo episodio
    /// en el sitio) se usa ese número aunque la app lo registre como episodio 1.
    /// Verifica el malId antes de confiar en la página.
    /// </summary>
    private async Task<int?> ObtenerNumeroEpisodioDelMediaAsync(string slug, int numeroSolicitado, int? malIdEsperado, CancellationToken ct)
    {
        try
        {
            string mediaUrl = $"https://animeav1.com/media/{slug}";
            if (!EsDominioPermitido(mediaUrl, "animeav1.com")) return null;

            using var req = new HttpRequestMessage(HttpMethod.Get, mediaUrl);
            req.Headers.Add("User-Agent", UserAgent);

            using var res = await _httpClient.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return null;

            var html = await res.Content.ReadAsStringAsync(ct);

            // Anti-confusión: el media debe ser el anime esperado
            var malIdPagina = AnimeAv1HtmlParser.ExtraerMalIdDelMedia(html);
            if (malIdEsperado.HasValue && malIdPagina.HasValue && malIdPagina.Value != malIdEsperado.Value) return null;

            var episodios = AnimeAv1HtmlParser.ExtraerEpisodiosDelMedia(html);
            if (episodios.Count == 0) return null;

            if (episodios.Any(e => e.Numero == numeroSolicitado)) return numeroSolicitado;

            // Película/especial: el sitio numera la media como "Ep N" del catálogo
            // pero la app la registra como un solo episodio → usar el único disponible
            if (episodios.Count == 1) return episodios[0].Numero;

            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AnimeAv1VideoSourceResolver] Error en página del media {slug}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Descarga la página del episodio, verifica el MAL ID (si se espera uno) y
    /// extrae sus embeds filtrando los hosts que no están en la allowlist de
    /// servidores (seguridad INT-01/SEC-16).
    /// </summary>
    private async Task<List<AnimeAv1HtmlParser.EmbedServidor>> ObtenerEmbedsDePaginaAsync(string pageUrl, int? malIdEsperado, CancellationToken cancellationToken)
    {
        try
        {
            if (!EsDominioPermitido(pageUrl, "animeav1.com")) return [];

            using var req = new HttpRequestMessage(HttpMethod.Get, pageUrl);
            req.Headers.Add("User-Agent", UserAgent);

            using var res = await _httpClient.SendAsync(req, cancellationToken);
            if (!res.IsSuccessStatusCode) return [];

            var html = await res.Content.ReadAsStringAsync(cancellationToken);

            // Anti-confusión: si la página declara un malId distinto del esperado,
            // es OTRO anime con nombre parecido → rechazar (siguiente slug).
            var malIdPagina = AnimeAv1HtmlParser.ExtraerMalIdDelMedia(html);
            if (malIdEsperado.HasValue && malIdPagina.HasValue && malIdPagina.Value != malIdEsperado.Value)
            {
                AppLogger.Warn("AnimeAv1VideoSourceResolver",
                    $"Página {pageUrl} rechazada: malId {malIdPagina} != esperado {malIdEsperado} (anime con nombre parecido).");
                return [];
            }

            return AnimeAv1HtmlParser.ExtraerEmbeds(html)
                .Where(e => Core.UrlSeguridad.EsUrlEmbedPermitida(e.Url))
                .ToList();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AnimeAv1VideoSourceResolver] Error obteniendo embeds de {pageUrl}: {ex.Message}");
            return [];
        }
    }

    private static bool EsDominioPermitido(string url, string dominioEsperado)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        return uri.Host.Equals(dominioEsperado, StringComparison.OrdinalIgnoreCase) ||
               uri.Host.EndsWith("." + dominioEsperado, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string?> GetVideoUrlAsync(string pageUrl, CancellationToken cancellationToken = default)
    {
        if (EsDominioPermitido(pageUrl, "animeav1.com"))
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, pageUrl);
                req.Headers.Add("User-Agent", UserAgent);

                using var res = await _httpClient.SendAsync(req, cancellationToken);
                if (!res.IsSuccessStatusCode) return null;

                var html = await res.Content.ReadAsStringAsync(cancellationToken);

                // INT-01: parseo delegado al contrato tipado (testeable con fixtures)
                var mp4UploadId = AnimeAv1HtmlParser.ExtraerMp4UploadId(html);
                if (!string.IsNullOrEmpty(mp4UploadId))
                {
                    var directMp4 = await ExtractFromMp4UploadAsync($"https://www.mp4upload.com/embed-{mp4UploadId}.html", cancellationToken);
                    if (!string.IsNullOrEmpty(directMp4))
                    {
                        return directMp4;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error fetching animeav1 page: {ex.Message}");
                return null;
            }
        }
        else if (EsDominioPermitido(pageUrl, "mp4upload.com"))
        {
            return await ExtractFromMp4UploadAsync(pageUrl, cancellationToken);
        }

        return null;
    }

    private async Task<string?> ExtractFromMp4UploadAsync(string embedUrl, CancellationToken cancellationToken)
    {
        if (!EsDominioPermitido(embedUrl, "mp4upload.com")) return null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, embedUrl);
            req.Headers.Add("User-Agent", UserAgent);
            req.Headers.Add("Referer", "https://animeav1.com/");

            using var res = await _httpClient.SendAsync(req, cancellationToken);
            if (!res.IsSuccessStatusCode) return null;

            var html = await res.Content.ReadAsStringAsync(cancellationToken);

            // INT-01: parseo delegado al contrato tipado (testeable con fixtures).
            // Hardening: la URL extraída del HTML de terceros solo se acepta si es
            // https y pertenece a mp4upload.com (un proveedor comprometido no puede
            // redirigir la descarga a un servidor arbitrario).
            var url = AnimeAv1HtmlParser.ExtraerVideoDirecto(html);
            return Core.UrlSeguridad.EsUrlVideoPermitida(url) ? url : null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MP4Upload extraction error: {ex.Message}");
        }

        return null;
    }

    private static bool EsSlugCompatible(string slug, IEnumerable<string> titulos)
    {
        string s = slug.ToLower();

        foreach (var tit in titulos)
        {
            string t = tit.ToLower();

            // 1. Validar temporadas
            bool tEsTemp2 = t.Contains(" 2nd season") || t.Contains(" season 2") || t.Contains(" 2da temporada") || t.Contains(" 2ª temporada") || t.Contains(" ii") || t.EndsWith(" 2") || t.Contains(" 2:") || t.Contains("part 2") || t.Contains("part ii");
            bool tEsTemp3 = t.Contains(" 3rd season") || t.Contains(" season 3") || t.Contains(" 3ra temporada") || t.Contains(" 3ª temporada") || t.Contains(" iii") || t.EndsWith(" 3") || t.Contains(" 3:") || t.Contains("part 3") || t.Contains("part iii");
            bool tEsTemp4 = t.Contains(" 4th season") || t.Contains(" season 4") || t.Contains(" iv") || t.EndsWith(" 4") || t.Contains(" 4:");
            bool tEsPelicula = t.Contains("movie") || t.Contains("pelicula") || t.Contains("película") || t.Contains("film") || t.Contains("gekijouban") || t.Contains("zankyou-hen") || t.Contains("zankyou");

            bool sEsTemp2 = s.Contains("-2nd-season") || s.Contains("-season-2") || s.Contains("-ii") || s.EndsWith("-2") || s.Contains("-2-") || s.Contains("-part-2");
            bool sEsTemp3 = s.Contains("-3rd-season") || s.Contains("-season-3") || s.Contains("-iii") || s.EndsWith("-3") || s.Contains("-3-") || s.Contains("-part-3");
            bool sEsTemp4 = s.Contains("-4th-season") || s.Contains("-season-4") || s.Contains("-iv") || s.EndsWith("-4") || s.Contains("-4-");
            bool sEsPelicula = s.Contains("-movie") || s.Contains("-pelicula") || s.Contains("-film") || s.Contains("-gekijouban") || s.Contains("zankyou");

            // Si el título es temporada 2, el slug NO puede ser temporada 1 ni temporada 3
            if (tEsTemp2 && !sEsTemp2) continue;
            if (tEsTemp3 && !sEsTemp3) continue;
            if (tEsTemp4 && !sEsTemp4) continue;
            if (tEsPelicula && !sEsPelicula) continue;

            // Si el título es temporada 1 pura (sin temporada ni película), el slug NO debe ser temporada 2/3/película
            bool tEsTemp1Pura = !tEsTemp2 && !tEsTemp3 && !tEsTemp4 && !tEsPelicula;
            if (tEsTemp1Pura && (sEsTemp2 || sEsTemp3 || sEsTemp4 || sEsPelicula)) continue;

            // 2. Validar subtítulos distintivos (ej: ": Zankyou-hen", ": Yuukaku-hen", ": Mugen Ressha-hen")
            var partesSub = tit.Split([':', '–'], StringSplitOptions.RemoveEmptyEntries);
            if (partesSub.Length > 1)
            {
                string subtitulo = partesSub[1].Trim().ToLower();
                var palabrasSub = subtitulo.Split([' ', '-', '!', '?', '.', ',', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
                                           .Where(w => w.Length > 3 && !w.Equals("season", StringComparison.OrdinalIgnoreCase) && !w.Equals("hen", StringComparison.OrdinalIgnoreCase))
                                           .ToList();

                if (palabrasSub.Count > 0)
                {
                    // Si el título original tiene palabras clave en su subtítulo (ej: "zankyou"), al menos una debe estar en el slug
                    bool contienePalabraSub = palabrasSub.Any(w => s.Contains(w));
                    if (!contienePalabraSub) continue;
                }
            }

            return true;
        }

        return false;
    }

    private static List<string> GenerarVariacionesSlug(string titulo)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(titulo)) return list;

        string BaseSlug(string input)
        {
            string clean = Regex.Replace(input.ToLower(), @"[^\w\s-]", " ");
            clean = Regex.Replace(clean, @"\s+", "-").Trim('-');
            return clean;
        }

        string rawSlug = BaseSlug(titulo);
        if (!string.IsNullOrEmpty(rawSlug)) list.Add(rawSlug);

        // Variaciones de temporadas y números romanos
        (string, string)[] conversiones = [
            (" 2nd season", "-2nd-season"), (" 2nd season", "-2"), (" 2nd season", "-ii"),
            (" 3rd season", "-3rd-season"), (" 3rd season", "-3"), (" 3rd season", "-iii"),
            (" 4th season", "-4th-season"), (" 4th season", "-4"), (" 4th season", "-iv"),
            (" ii", "-ii"), (" ii", "-2"), (" ii", "-2nd-season"),
            (" iii", "-iii"), (" iii", "-3"), (" iii", "-3rd-season"),
            (" 2", "-2"), (" 2", "-ii"), (" 2", "-2nd-season"),
            (" 3", "-3"), (" 3", "-iii"), (" 3", "-3rd-season"),
            (" season 2", "-2"), (" season 2", "-2nd-season"),
            (" season 3", "-3"), (" season 3", "-3rd-season")
        ];

        string tLower = titulo.ToLower();
        foreach (var (patron, sufijo) in conversiones)
        {
            if (tLower.Contains(patron))
            {
                string baseName = BaseSlug(tLower.Replace(patron, ""));
                string candidate = $"{baseName}{sufijo}";
                if (!list.Contains(candidate)) list.Add(candidate);
            }
        }

        return list;
    }

    /// <summary>Términos de búsqueda para el catálogo (público para testeo).</summary>
    public static List<string> GenerarTerminosBusqueda(string titulo)
    {
        var terminos = new List<string>();
        if (string.IsNullOrWhiteSpace(titulo)) return terminos;

        // 1. Título completo limpio
        terminos.Add(titulo.Trim());

        // 2. Parte antes de dos puntos / subtítulo + EL SUBTÍTULO TRAS ':' — a menudo
        //    lo más distintivo ("Battle of Gods" frente a cientos de "Dragon Ball Z"
        //    que el catálogo pagina y deja fuera la película buscada)
        var partes = titulo.Split([':', '-', '–', '~', '('], StringSplitOptions.RemoveEmptyEntries);
        if (partes.Length > 1)
        {
            string mainPart = partes[0].Trim();
            if (!string.IsNullOrWhiteSpace(mainPart) && !terminos.Contains(mainPart, StringComparer.OrdinalIgnoreCase))
            {
                terminos.Add(mainPart);
            }

            string subPart = partes[1].Trim();
            if (!string.IsNullOrWhiteSpace(subPart) && !terminos.Contains(subPart, StringComparer.OrdinalIgnoreCase))
            {
                terminos.Add(subPart);
            }
        }

        var palabras = titulo.Split([' '], StringSplitOptions.RemoveEmptyEntries)
                             .Where(p => p.Length > 2)
                             .ToList();

        // 3. Primeras 2-3 palabras significativas
        if (palabras.Count > 0)
        {
            string shortTerm = string.Join(" ", palabras.Take(3));
            if (!terminos.Contains(shortTerm, StringComparer.OrdinalIgnoreCase))
            {
                terminos.Add(shortTerm);
            }
        }

        // 4. Últimas 2-3 palabras significativas (cola del título — colas distintivas)
        if (palabras.Count >= 3)
        {
            string tailTerm = string.Join(" ", palabras.TakeLast(3));
            if (!terminos.Contains(tailTerm, StringComparer.OrdinalIgnoreCase))
            {
                terminos.Add(tailTerm);
            }
        }

        return terminos;
    }
}
