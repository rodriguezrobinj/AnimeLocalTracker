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
    /// Orden de preferencia de servidores: MP4Upload primero — es la ÚNICA fuente
    /// fiable hoy (el player HLS de zilla está tras Cloudflare anti-bot que ni la
    /// impersonación de yt-dlp pasa; Voe/UPNShare/Byse no tienen extractor).
    /// HLS se conserva como intento (403 limpio en el log) por si el sitio
    /// relaja Cloudflare. Mega se excluye.
    /// </summary>
    public static List<EmbedServidor> OrdenarEmbedsPorPreferencia(IEnumerable<EmbedServidor> embeds)
    {
        var preferencia = new[] { "MP4Upload", "HLS", "Voe", "UPNShare", "Byse" };
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

    /// <summary>Títulos de un media del sitio: principal + nombres alternativos (aka).</summary>
    public sealed record TitulosMedia(string Principal, List<string> Alternativos);

    /// <summary>
    /// Extrae el título principal y los aka del media (payload SvelteKit):
    /// media:{id,title:"...",aka:{"en-us":"...","ja-jp":"...","es-419":"..."},...}.
    /// </summary>
    public static TitulosMedia? ExtraerTitulosDelMedia(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        var m = TituloMediaRegex().Match(html);
        if (!m.Success) return null;

        var alternativos = new List<string>();
        var aka = AkaMediaRegex().Match(html);
        if (aka.Success)
        {
            foreach (Match v in ValorAkaRegex().Matches(aka.Groups[1].Value))
            {
                string valor = v.Groups[1].Value.Trim();
                if (!string.IsNullOrWhiteSpace(valor)) alternativos.Add(valor);
            }
        }
        return new TitulosMedia(m.Groups[1].Value, alternativos);
    }

    /// <summary>
    /// Títulos del media relacionados (relations): [{slug, title}] — permite
    /// descubrir películas/secuelas de una franquicia de forma determinista.
    /// </summary>
    public static List<(string Slug, string Titulo)> ExtraerRelationsDelMedia(string html)
    {
        var lista = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(html)) return lista;

        foreach (Match m in RelationMediaRegex().Matches(html))
        {
            string slug = m.Groups[1].Value.Trim();
            string titulo = m.Groups[2].Value.Trim();
            if (!string.IsNullOrWhiteSpace(slug) && !string.IsNullOrWhiteSpace(titulo))
            {
                lista.Add((slug, titulo));
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

    [GeneratedRegex(@"destination:\{id:\d+,slug:""([^""]+)"",title:""([^""]+)""")]
    private static partial Regex RelationMediaRegex();

    [GeneratedRegex(@"media:\{[^}]*?title:""([^""]+)""")]
    private static partial Regex TituloMediaRegex();

    [GeneratedRegex(@"aka:\{([^}]*)\}")]
    private static partial Regex AkaMediaRegex();

    [GeneratedRegex(@":""([^""]+)""")]
    private static partial Regex ValorAkaRegex();

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
    /// coincidencia queda en nombres (Python rapidfuzz o fallback C#).
    /// </summary>
    private readonly Func<int, CancellationToken, Task<int?>>? _malIdResolver;

    /// <summary>
    /// Similitud de nombres (0..1) vía daemon Python (rapidfuzz) sobre títulos +
    /// aka del media. Null si el daemon no está disponible → fallback C#.
    /// </summary>
    private readonly Func<List<string>, List<string>, CancellationToken, Task<double?>>? _similitudNombres;

    /// <summary>
    /// Títulos adicionales desde AniList (romaji, english, native, userPreferred y
    /// synonyms) para ampliar la búsqueda aunque la biblioteca local no los tenga
    /// guardados. Null (resultado) si no está disponible.
    /// </summary>
    private readonly Func<int, CancellationToken, Task<List<string>?>>? _titulosDesdeAniList;

    public AnimeAv1VideoSourceResolver(
        HttpClient httpClient,
        Func<int, CancellationToken, Task<int?>>? malIdResolver = null,
        Func<List<string>, List<string>, CancellationToken, Task<double?>>? similitudNombres = null,
        Func<int, CancellationToken, Task<List<string>?>>? titulosDesdeAniList = null)
    {
        _httpClient = httpClient;
        _malIdResolver = malIdResolver;
        _similitudNombres = similitudNombres;
        _titulosDesdeAniList = titulosDesdeAniList;
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
    /// FLUJO RIGUROSO (media-first): por cada candidato se consulta la página del
    /// media (una petición, no spam de 404 de episodios), se verifica la identidad
    /// con veredicto en cascada — MAL ID exacto → acepta; MAL ID no comparable →
    /// coincidencia de nombre (rapidfuzz en el daemon Python o fallback C#) contra
    /// título + aka — y se resuelve el número de episodio real del sitio.
    /// </summary>
    public async Task<List<AnimeAv1HtmlParser.EmbedServidor>> ObtenerEmbedsEpisodioAsync(
        IEnumerable<string> titulos, int numeroEpisodio, int? aniListId = null, CancellationToken cancellationToken = default)
    {
        var titulosLista = titulos.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        if (titulosLista.Count == 0) return [];

        // Títulos adicionales desde AniList (native japonés, synonyms…) — la
        // biblioteca local puede no tenerlos guardados (refresh pendiente del detalle)
        if (aniListId.HasValue && _titulosDesdeAniList != null)
        {
            try
            {
                var extra = await _titulosDesdeAniList(aniListId.Value, cancellationToken);
                if (extra != null)
                {
                    foreach (var t in extra)
                    {
                        if (!string.IsNullOrWhiteSpace(t) &&
                            !titulosLista.Contains(t, StringComparer.OrdinalIgnoreCase))
                        {
                            titulosLista.Add(t);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug("AnimeAv1VideoSourceResolver", $"No se pudieron obtener títulos de AniList para {aniListId}: {ex.Message}");
            }
        }

        var malIdEsperado = await ObtenerMalIdEsperadoAsync(aniListId, cancellationToken);
        var slugs = await ObtenerSlugsCandidatosAsync(titulosLista, malIdEsperado, cancellationToken);

        // Media-first con crawl de relations: cuando un media se prueba, sus
        // relations (películas/secuelas de la franquicia) se insertan INMEDIATAMENTE
        // después de él — así dragon-ball-z (rechazado por malId) lleva directo al
        // movie-14 sin depender del orden/limite del catálogo. Presupuesto total:
        // MaxMediaProbes peticiones de media.
        const int MaxMediaProbes = 25;
        int probes = 0;
        var probados = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var encolados = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < slugs.Count && probes < MaxMediaProbes; i++)
        {
            if (cancellationToken.IsCancellationRequested) return [];
            string slug = slugs[i];
            if (!probados.Add(slug)) continue;

            probes++;
            var media = await ObtenerInfoMediaAsync(slug, cancellationToken);
            if (media == null) continue;

            // Crawl inmediato: encolar las relations del media justo detrás de él
            // (el media principal de una franquicia las lista todas)
            if (probes < MaxMediaProbes)
            {
                var relations = AnimeAv1HtmlParser.ExtraerRelationsDelMedia(media.Html);
                int restantes = MaxMediaProbes - probes;
                var nuevos = new List<string>();
                foreach (var r in relations.Take(restantes))
                {
                    if (encolados.Add(r.Slug) && !probados.Contains(r.Slug)) nuevos.Add(r.Slug);
                }
                if (nuevos.Count > 0) slugs.InsertRange(i + 1, nuevos);
            }

            if (!await EsMediaCoincidenteAsync(media, malIdEsperado, titulosLista, cancellationToken))
            {
                continue;
            }

            int? objetivo = ResolverNumeroEpisodio(media, numeroEpisodio);
            if (!objetivo.HasValue) continue;

            var embeds = await ObtenerEmbedsDeEpisodioAsync(slug, objetivo.Value, malIdEsperado, cancellationToken);
            if (embeds.Count > 0) return embeds;
        }

        return [];
    }

    /// <summary>Resuelve el MAL ID esperado del anime (si hay AniListId y resolver).</summary>
    public async Task<int?> ObtenerMalIdEsperadoAsync(int? aniListId, CancellationToken ct = default)
    {
        if (aniListId.HasValue && _malIdResolver != null)
        {
            try { return await _malIdResolver(aniListId.Value, ct); }
            catch (Exception ex) { AppLogger.Debug("AnimeAv1VideoSourceResolver", $"No se pudo resolver MAL ID de {aniListId}: {ex.Message}"); }
        }
        return null;
    }

    /// <summary>
    /// Recolecta slugs candidatos: generados de los títulos (heurística estricta)
    /// y descubiertos en el catálogo (heurística relajada si el malId es conocido,
    /// porque el malId de la página es la verificación autoritativa).
    /// </summary>
    public async Task<List<string>> ObtenerSlugsCandidatosAsync(
        List<string> titulosLista, int? malIdEsperado, CancellationToken ct = default)
    {
        var slugs = new List<string>();
        var vistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var titulo in titulosLista)
        {
            foreach (var slug in GenerarVariacionesSlug(titulo))
            {
                if (vistos.Add(slug) && EsSlugCompatible(slug, titulosLista)) slugs.Add(slug);
            }
        }

        foreach (var titulo in titulosLista)
        {
            foreach (var termino in GenerarTerminosBusqueda(titulo))
            {
                try
                {
                    string searchUrl = $"https://animeav1.com/catalogo?search={Uri.EscapeDataString(termino)}";
                    using var req = new HttpRequestMessage(HttpMethod.Get, searchUrl);
                    req.Headers.Add("User-Agent", UserAgent);

                    using var res = await _httpClient.SendAsync(req, ct);
                    if (!res.IsSuccessStatusCode) continue;

                    var html = await res.Content.ReadAsStringAsync(ct);
                    // Sin gate de heurística aquí: el veredicto (malId exacto o
                    // nombres con rapidfuzz/C#) decide por cada media. La heurística
                    // de slugs rechazaba películas correctas ("movie-N-") cuando el
                    // malId no era comparable.
                    int nuevos = 0;
                    foreach (string discoveredSlug in AnimeAv1HtmlParser.ExtraerSlugs(html))
                    {
                        if (vistos.Add(discoveredSlug))
                        {
                            slugs.Add(discoveredSlug);
                            nuevos++;
                        }
                    }
                    // Diagnóstico: solo se loguea cuando un término aporta slugs nuevos
                    // (los 0s por variante inundaban el log: ~40 líneas por anime).
                    if (nuevos > 0)
                    {
                        AppLogger.Debug("AnimeAv1VideoSourceResolver",
                            $"Búsqueda de catálogo '{termino}': {nuevos} slugs nuevos ({slugs.Count} totales).");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AnimeAv1VideoSourceResolver] Error en búsqueda de catálogo para '{termino}': {ex.Message}");
                }
            }
        }

        return slugs;
    }

    /// <summary>Info del media del sitio (malId, títulos, episodios reales, HTML crudo para relations).</summary>
    public sealed record InfoMedia(string Slug, int? MalId, string? Titulo, List<string> Alternativos, List<(int Id, int Numero)> Episodios, string Html);

    /// <summary>Descarga la página del media y parsea su información.</summary>
    public async Task<InfoMedia?> ObtenerInfoMediaAsync(string slug, CancellationToken ct = default)
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
            var titulos = AnimeAv1HtmlParser.ExtraerTitulosDelMedia(html);
            return new InfoMedia(
                slug,
                AnimeAv1HtmlParser.ExtraerMalIdDelMedia(html),
                titulos?.Principal,
                titulos?.Alternativos ?? new List<string>(),
                AnimeAv1HtmlParser.ExtraerEpisodiosDelMedia(html),
                html);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AnimeAv1VideoSourceResolver] Error obteniendo media {slug}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Embeds de una página de episodio con verificación de malId.</summary>
    public async Task<List<AnimeAv1HtmlParser.EmbedServidor>> ObtenerEmbedsDeEpisodioAsync(
        string slug, int numero, int? malIdEsperado, CancellationToken ct = default)
    {
        return await ObtenerEmbedsDePaginaAsync($"https://animeav1.com/media/{slug}/{numero}", malIdEsperado, ct);
    }

    /// <summary>
    /// Veredicto de identidad en cascada (el sistema riguroso):
    /// 1. MAL ID exacto (ambos conocidos) → coinciden = acepta, difieren = rechaza.
    /// 2. MAL ID no comparable → coincidencia de NOMBRES: rapidfuzz del daemon
    ///    (Python) sobre título + aka, con fallback C# (TituloSimilaridad).
    /// </summary>
    private async Task<bool> EsMediaCoincidenteAsync(
        InfoMedia media, int? malIdEsperado, List<string> titulosLista, CancellationToken ct)
    {
        if (malIdEsperado.HasValue && media.MalId.HasValue)
        {
            if (media.MalId.Value == malIdEsperado.Value) return true;

            AppLogger.Warn("AnimeAv1VideoSourceResolver",
                $"Media {media.Slug} rechazado: malId {media.MalId} != esperado {malIdEsperado} (anime con nombre parecido).");
            return false;
        }

        // Sin malId comparable → nombres (título + aka del sitio vs títulos de la app)
        var nombresMedia = new List<string>();
        if (!string.IsNullOrWhiteSpace(media.Titulo)) nombresMedia.Add(media.Titulo);
        nombresMedia.AddRange(media.Alternativos);
        if (nombresMedia.Count == 0) return false;

        double score;
        string fuente;
        if (_similitudNombres != null)
        {
            try
            {
                var s = await _similitudNombres(titulosLista, nombresMedia, ct);
                if (s.HasValue)
                {
                    score = s.Value;
                    fuente = "rapidfuzz (Python)";
                    if (score < 0.75)
                    {
                        AppLogger.Warn("AnimeAv1VideoSourceResolver",
                            $"Media {media.Slug} rechazado por nombre: {score:P0} < 75% ({fuente}).");
                        return false;
                    }
                    AppLogger.Info("AnimeAv1VideoSourceResolver",
                        $"Media {media.Slug} aceptado por nombre: {score:P0} ({fuente}).");
                    return true;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug("AnimeAv1VideoSourceResolver", $"Fallo rapidfuzz para {media.Slug}: {ex.Message}");
            }
        }

        double mejor = titulosLista.Max(t => Core.TituloSimilaridad.MejorSimilitud(t, nombresMedia));
        fuente = "fallback C#";
        if (mejor < 0.70)
        {
            AppLogger.Warn("AnimeAv1VideoSourceResolver",
                $"Media {media.Slug} rechazado por nombre: {mejor:P0} < 70% ({fuente}).");
            return false;
        }
        AppLogger.Info("AnimeAv1VideoSourceResolver",
            $"Media {media.Slug} aceptado por nombre: {mejor:P0} ({fuente}).");
        return true;
    }

    /// <summary>
    /// Número de episodio real del sitio: coincidencia exacta; para películas/
    /// especiales (1 solo episodio en el sitio) se usa ese número aunque la app
    /// lo registre como episodio 1.
    /// </summary>
    private static int? ResolverNumeroEpisodio(InfoMedia media, int numeroSolicitado)
    {
        if (media.Episodios.Count == 0) return null;
        if (media.Episodios.Any(e => e.Numero == numeroSolicitado)) return numeroSolicitado;
        if (media.Episodios.Count == 1) return media.Episodios[0].Numero;
        return null;
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
