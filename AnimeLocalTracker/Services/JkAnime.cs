using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Lectura de jkanime.net (ver docs/investigacion-jkanime.md). Todo el parseo vive aquí, aislado de la red, para poder
/// probarse con fixtures. La página NO trae MAL ID ni AniList ID, así que el anime se identifica por nombre.
/// </summary>
public static partial class JkAnimeHtmlParser
{
    /// <summary>Un anime del buscador: slug de su página, título y tipo ("Serie", "Película"…).</summary>
    public readonly record struct ResultadoBusqueda(string Slug, string Titulo, string? Tipo);

    // El HTML real trae saltos de línea y sangría hasta dentro de las etiquetas ("<a" y "href" en líneas distintas): de ahí los \s.
    [GeneratedRegex(@"<div class=""anime__item""\s*>(.*?)<h5>\s*<a\s[^>]*?href=""https://jkanime\.net/([a-z0-9\-]+)/""[^>]*>\s*([^<]+?)\s*</a>", RegexOptions.Singleline)]
    private static partial Regex ResultadoRegex();

    [GeneratedRegex(@"<li class=""anime"">\s*([^<]+?)\s*</li>")]
    private static partial Regex TipoRegex();

    [GeneratedRegex(@"var servers\s*=\s*(\[.*?\])\s*;", RegexOptions.Singleline)]
    private static partial Regex ServidoresRegex();

    /// <summary>Resultados de <c>/buscar/&lt;término&gt;/</c> en el orden del sitio (sin repetir páginas).</summary>
    public static List<ResultadoBusqueda> ExtraerResultadosBusqueda(string? html)
    {
        var lista = new List<ResultadoBusqueda>();
        if (string.IsNullOrWhiteSpace(html)) return lista;

        var vistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in ResultadoRegex().Matches(html))
        {
            string slug = m.Groups[2].Value;
            if (!vistos.Add(slug)) continue;
            var tipo = TipoRegex().Match(m.Groups[1].Value);
            lista.Add(new ResultadoBusqueda(slug, WebUtility.HtmlDecode(m.Groups[3].Value).Trim(), tipo.Success ? tipo.Groups[1].Value.Trim() : null));
        }
        return lista;
    }

    /// <summary>
    /// Servidores de la página de un episodio: <c>var servers = [{server, lang, remote}]</c>, donde <c>remote</c> es la URL del
    /// embed en base64 simple. Se devuelven con los nombres y pistas de audio de la app (el mismo formato que AnimeAV1) y solo los
    /// que tienen una URL https válida; qué hosts se admiten lo decide <c>UrlSeguridad.EsUrlEmbedPermitida</c>.
    /// </summary>
    public static List<AnimeAv1HtmlParser.EmbedServidor> ExtraerServidores(string? html)
    {
        var lista = new List<AnimeAv1HtmlParser.EmbedServidor>();
        var m = ServidoresRegex().Match(html ?? string.Empty);
        if (!m.Success) return lista;

        try
        {
            using var doc = JsonDocument.Parse(m.Groups[1].Value);
            foreach (var s in doc.RootElement.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Object
                    || !s.TryGetProperty("server", out var servidor) || servidor.ValueKind != JsonValueKind.String
                    || !s.TryGetProperty("remote", out var remoto) || remoto.ValueKind != JsonValueKind.String) continue;

                string? url = DecodificarUrl(remoto.GetString());
                if (url == null) continue;
                int lang = s.TryGetProperty("lang", out var l) && l.TryGetInt32(out int n) ? n : 0;
                lista.Add(new AnimeAv1HtmlParser.EmbedServidor(NormalizarServidor(servidor.GetString()!), url, AudioDeLang(lang)));
            }
        }
        catch (JsonException)
        {
            lista.Clear();
        }
        return lista;
    }

    private static string? DecodificarUrl(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;
        try
        {
            string url = Encoding.UTF8.GetString(Convert.FromBase64String(base64.Trim() + new string('=', (4 - base64.Trim().Length % 4) % 4))).Trim();
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? url : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Nombres como los llama la app (los que el orden de servidores del usuario reconoce).</summary>
    private static string NormalizarServidor(string nombre) => nombre.Trim().ToLowerInvariant() switch
    {
        "mp4upload" => "MP4Upload",
        "voe" => "Voe",
        "mega" => "Mega",
        "transferit" => "TransferIt",
        "mediafire" => "Mediafire",
        "vidhide" => "Vidhide",
        "streamwish" => "Streamwish",
        _ => nombre.Trim()
    };

    /// <summary>El sitio marca el idioma con un número: 1 = japonés subtitulado, 3 = español latino.</summary>
    private static string AudioDeLang(int lang) => lang switch { 1 => "SUB", 3 => "DUB", _ => $"LANG{lang}" };
}

/// <summary>Peticiones a jkanime.net (búsqueda y página de episodio). Las páginas se piden sin sesión ni token.</summary>
public sealed partial class JkAnimeClient(HttpClient http)
{
    private const string Base = "https://jkanime.net";
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    [GeneratedRegex(@"^[a-z0-9\-]+$")]
    private static partial Regex SlugValidoRegex();

    /// <summary>Páginas de anime que devuelve el buscador para un término (vacío si falla).</summary>
    public async Task<List<JkAnimeHtmlParser.ResultadoBusqueda>> BuscarAsync(string termino, CancellationToken ct)
    {
        var (_, html) = await PedirAsync($"{Base}/buscar/{Uri.EscapeDataString(termino)}/", ct);
        return JkAnimeHtmlParser.ExtraerResultadosBusqueda(html);
    }

    /// <summary>Servidores del episodio <paramref name="numero"/> de un anime. Null si el episodio no existe (404) o falla.</summary>
    public async Task<List<AnimeAv1HtmlParser.EmbedServidor>?> ObtenerServidoresAsync(string slug, int numero, CancellationToken ct)
    {
        if (numero < 1 || !SlugValidoRegex().IsMatch(slug ?? string.Empty)) return null;

        var (_, html) = await PedirAsync($"{Base}/{slug}/{numero}/", ct);
        return html == null ? null : JkAnimeHtmlParser.ExtraerServidores(html);
    }

    /// <summary>GET con un reintento ante fallos pasajeros (el sitio tarda, 429, 5xx). Un 404 no se reintenta. Nunca lanza.</summary>
    private async Task<(HttpStatusCode? Estado, string? Html)> PedirAsync(string url, CancellationToken ct)
    {
        for (int intento = 1; ; intento++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("User-Agent", UserAgent);
                using var res = await http.SendAsync(req, ct);
                if (res.IsSuccessStatusCode) return (res.StatusCode, await res.Content.ReadAsStringAsync(ct));

                int codigo = (int)res.StatusCode;
                if (!(codigo is 408 or 429 or >= 500) || intento >= 2) return (res.StatusCode, null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return (null, null);
            }
            catch (SinConexionException)
            {
                return (null, null); // sin internet repetir no sirve: la descarga ya espera a que vuelva la red
            }
            catch (Exception ex) when (intento < 2 && ex is HttpRequestException or TaskCanceledException or System.IO.IOException)
            {
                AppLogger.Debug("JkAnimeClient", $"Fallo pasajero en {url}: {ex.Message}. Reintentando.");
            }
            catch (Exception ex)
            {
                AppLogger.Debug("JkAnimeClient", $"Error pidiendo {url}: {ex.Message}");
                return (null, null);
            }

            try { await Task.Delay(TimeSpan.FromMilliseconds(400), ct); }
            catch (OperationCanceledException) { return (null, null); }
        }
    }
}
