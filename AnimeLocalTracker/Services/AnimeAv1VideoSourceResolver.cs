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

    public AnimeAv1VideoSourceResolver(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<string?> BuscarUrlEpisodioAsync(IEnumerable<string> titulos, int numeroEpisodio, CancellationToken cancellationToken = default)
    {
        var titulosLista = titulos.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        var slugsProbados = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // FASE 1: Probar slugs generados directamente a partir de todos los títulos válidos
        foreach (var titulo in titulosLista)
        {
            var variaciones = GenerarVariacionesSlug(titulo);
            foreach (var slug in variaciones)
            {
                if (slugsProbados.Add(slug) && EsSlugCompatible(slug, titulosLista))
                {
                    string pageUrl = $"https://animeav1.com/media/{slug}/{numeroEpisodio}";
                    string? videoUrl = await GetVideoUrlAsync(pageUrl, cancellationToken);
                    if (!string.IsNullOrEmpty(videoUrl))
                    {
                        return videoUrl;
                    }
                }
            }
        }

        // FASE 2: Si no coincidió directamente, buscar en el catálogo de AnimeAV1 pero con validación estricta
        foreach (var titulo in titulosLista)
        {
            var terminosBusqueda = GenerarTerminosBusqueda(titulo);
            foreach (var termino in terminosBusqueda)
            {
                try
                {
                    string searchUrl = $"https://animeav1.com/catalogo?search={Uri.EscapeDataString(termino)}";
                    using var req = new HttpRequestMessage(HttpMethod.Get, searchUrl);
                    req.Headers.Add("User-Agent", UserAgent);

                    using var res = await _httpClient.SendAsync(req, cancellationToken);
                    if (res.IsSuccessStatusCode)
                    {
                        var html = await res.Content.ReadAsStringAsync(cancellationToken);

                        // INT-01: parseo delegado al contrato tipado (testeable con fixtures)
                        foreach (string discoveredSlug in AnimeAv1HtmlParser.ExtraerSlugs(html))
                        {
                            if (slugsProbados.Add(discoveredSlug))
                            {
                                // VALIDACIÓN CRÍTICA: Asegurar que el slug encontrado corresponde a la temporada/secuela exacta solicitada
                                if (EsSlugCompatible(discoveredSlug, titulosLista))
                                {
                                    string pageUrl = $"https://animeav1.com/media/{discoveredSlug}/{numeroEpisodio}";
                                    string? videoUrl = await GetVideoUrlAsync(pageUrl, cancellationToken);
                                    if (!string.IsNullOrEmpty(videoUrl))
                                    {
                                        return videoUrl;
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AnimeAv1VideoSourceResolver] Error en búsqueda de catálogo para '{termino}': {ex.Message}");
                }
            }
        }

        return null;
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

            // INT-01: parseo delegado al contrato tipado (testeable con fixtures)
            return AnimeAv1HtmlParser.ExtraerVideoDirecto(html);
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

    private static List<string> GenerarTerminosBusqueda(string titulo)
    {
        var terminos = new List<string>();
        if (string.IsNullOrWhiteSpace(titulo)) return terminos;

        // 1. Título completo limpio
        terminos.Add(titulo.Trim());

        // 2. Parte antes de dos puntos / subtítulo
        var partes = titulo.Split([':', '-', '–', '~', '('], StringSplitOptions.RemoveEmptyEntries);
        if (partes.Length > 1)
        {
            string mainPart = partes[0].Trim();
            if (!string.IsNullOrWhiteSpace(mainPart) && !terminos.Contains(mainPart, StringComparer.OrdinalIgnoreCase))
            {
                terminos.Add(mainPart);
            }
        }

        // 3. Primeras 2 o 3 palabras significativas
        var palabras = titulo.Split([' '], StringSplitOptions.RemoveEmptyEntries)
                             .Where(p => p.Length > 2)
                             .Take(3)
                             .ToList();
        if (palabras.Count > 0)
        {
            string shortTerm = string.Join(" ", palabras);
            if (!terminos.Contains(shortTerm, StringComparer.OrdinalIgnoreCase))
            {
                terminos.Add(shortTerm);
            }
        }

        return terminos;
    }
}
