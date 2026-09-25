using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace AnimeLocalTracker.Services;

/// <summary>
/// INT-01: parseo del RSS de Nyaa.si aislado de la red (igual que
/// <c>AnimeAv1HtmlParser</c>), para poder testearse con fixtures reales sin tocar
/// internet. Nyaa expone un feed RSS oficial y estructurado — nada de scraping de HTML.
/// </summary>
public static partial class NyaaRssParser
{
    private static readonly XNamespace NyaaNs = "https://nyaa.si/xmlns/nyaa";

    /// <summary>
    /// Extrae los candidatos de un feed RSS de búsqueda de Nyaa. XML inválido o sin
    /// items devuelve lista vacía (nunca lanza) — el llamador sigue con el siguiente
    /// término de búsqueda.
    /// </summary>
    public static List<CandidatoTorrent> ExtraerCandidatos(string? rssXml)
    {
        var lista = new List<CandidatoTorrent>();
        if (string.IsNullOrWhiteSpace(rssXml)) return lista;

        XDocument doc;
        try
        {
            doc = XDocument.Parse(rssXml);
        }
        catch (Exception)
        {
            return lista;
        }

        foreach (var item in doc.Descendants("item"))
        {
            string titulo = item.Element("title")?.Value ?? "";
            string torrentUrl = item.Element("link")?.Value ?? "";
            if (string.IsNullOrWhiteSpace(titulo) || string.IsNullOrWhiteSpace(torrentUrl)) continue;

            string infoHash = item.Element(NyaaNs + "infoHash")?.Value ?? "";
            int seeders = int.TryParse(item.Element(NyaaNs + "seeders")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0;
            long tamanoBytes = ParseTamano(item.Element(NyaaNs + "size")?.Value);

            lista.Add(new CandidatoTorrent(titulo, torrentUrl, infoHash, seeders, tamanoBytes));
        }
        return lista;
    }

    /// <summary>
    /// Número de episodio del título de un release, o null si no se pudo determinar
    /// (o si es un batch — ver <see cref="EsBatch"/>, que el llamador debe chequear
    /// primero). Prueba patrones en orden: "SxxExx", " - NN " (el más común en
    /// fansubs), "Episode NN".
    /// </summary>
    public static int? ExtraerNumeroEpisodio(string titulo)
    {
        if (string.IsNullOrWhiteSpace(titulo)) return null;

        var m = TemporadaEpisodioRegex().Match(titulo);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var epSxE)) return epSxE;

        m = GuionEpisodioRegex().Match(titulo);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var epGuion)) return epGuion;

        m = PalabraEpisodioRegex().Match(titulo);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var epPalabra)) return epPalabra;

        return null;
    }

    /// <summary>
    /// True si el título es claramente un batch (temporada completa/rango de
    /// episodios), no un release de un solo episodio. MVP no descarga estos —
    /// necesitaría descarga selectiva dentro del torrent (fase futura).
    /// </summary>
    public static bool EsBatch(string titulo)
    {
        if (string.IsNullOrWhiteSpace(titulo)) return false;
        if (PalabraBatchRegex().IsMatch(titulo)) return true;
        if (RangoEpisodiosRegex().IsMatch(titulo)) return true;
        // "S01"/"S01v2" sin "E<n>" ni "- NN" acompañante: paquete de temporada.
        if (TemporadaSolaRegex().IsMatch(titulo) && ExtraerNumeroEpisodio(titulo) is null) return true;
        return false;
    }

    /// <summary>
    /// Dos pasadas (Fase 2a): 1) el mejor release de UN solo episodio (episodio exacto,
    /// sin batch, semillas suficientes) — igual que siempre; 2) si no hay ninguno, el
    /// mejor batch/temporada completa con semillas suficientes (un batch no lleva el
    /// número de episodio en el título de la release, así que aquí no se filtra por
    /// episodio — <c>ITorrentDownloadService</c> busca el archivo correcto DENTRO del
    /// torrent). Preferencia con fallback: nunca se deja de descargar solo porque no
    /// hay release de episodio suelto. Null si ninguna pasada encuentra nada.
    /// </summary>
    /// <param name="grupoPreferido">Fase 2b: grupo de fansub preferido
    /// (AppSettings.GrupoFansubPreferidoTorrent), buscado como substring del título
    /// (sin parsing estricto — los títulos de Nyaa no son uniformes). Preferencia con
    /// fallback dentro de cada pasada: si ninguno matchea, se cae al orden por semillas
    /// de siempre.</param>
    /// <param name="resolucionPreferida">Fase 2b: resolución preferida
    /// (AppSettings.ResolucionPreferidaTorrent, ej. "1080p"), mismo criterio de
    /// substring y mismo fallback que <paramref name="grupoPreferido"/>.</param>
    public static CandidatoTorrent? ElegirMejorCandidato(
        IEnumerable<CandidatoTorrent> candidatos,
        int numeroEpisodio,
        int minimoSeeders,
        string? grupoPreferido = null,
        string? resolucionPreferida = null)
    {
        return FiltrarYOrdenarCandidatos(candidatos, numeroEpisodio, minimoSeeders, grupoPreferido, resolucionPreferida)
            .Cast<CandidatoTorrent?>()
            .FirstOrDefault();
    }

    /// <summary>
    /// Fase 2d: igual que <see cref="ElegirMejorCandidato"/> pero devuelve TODOS los
    /// candidatos válidos (no solo el mejor), para un selector manual — el usuario ve
    /// varias opciones en vez de que la app elija sola. Mismo orden: episodio suelto
    /// antes que batch, y dentro de cada grupo por preferencia (grupo de fansub,
    /// resolución) y luego semillas.
    /// </summary>
    public static List<CandidatoTorrent> FiltrarYOrdenarCandidatos(
        IEnumerable<CandidatoTorrent> candidatos,
        int numeroEpisodio,
        int minimoSeeders,
        string? grupoPreferido = null,
        string? resolucionPreferida = null)
    {
        var lista = candidatos as ICollection<CandidatoTorrent> ?? candidatos.ToList();

        var episodioSuelto = OrdenarPorPreferencia(
            lista.Where(c => !EsBatch(c.Titulo))
                 .Where(c => ExtraerNumeroEpisodio(c.Titulo) == numeroEpisodio)
                 .Where(c => c.Seeders >= minimoSeeders),
            grupoPreferido, resolucionPreferida)
            .ToList();
        if (episodioSuelto.Count > 0) return episodioSuelto;

        return OrdenarPorPreferencia(
            lista.Where(c => EsBatch(c.Titulo)).Where(c => c.Seeders >= minimoSeeders),
            grupoPreferido, resolucionPreferida)
            .Select(c => c with { EsBatch = true })
            .ToList();
    }

    /// <summary>Dentro de un conjunto ya filtrado (válido), ordena primero por si el
    /// título contiene el grupo preferido, luego si contiene la resolución preferida,
    /// y por último por semillas — así un candidato sin preferencias sigue eligiéndose
    /// por semillas exactamente igual que antes de la Fase 2b.</summary>
    private static IEnumerable<CandidatoTorrent> OrdenarPorPreferencia(IEnumerable<CandidatoTorrent> candidatos, string? grupoPreferido, string? resolucionPreferida)
    {
        return candidatos
            .OrderByDescending(c => ContienePreferencia(c.Titulo, grupoPreferido))
            .ThenByDescending(c => ContienePreferencia(c.Titulo, resolucionPreferida))
            .ThenByDescending(c => c.Seeders);
    }

    private static bool ContienePreferencia(string titulo, string? preferencia)
        => !string.IsNullOrWhiteSpace(preferencia) && titulo.Contains(preferencia, StringComparison.OrdinalIgnoreCase);

    /// <summary>Tamaños de Nyaa son binarios ("6.6 GiB", "512 MiB") — nunca decimales (GB/MB).</summary>
    private static long ParseTamano(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return 0;
        var m = TamanoRegex().Match(texto.Trim());
        if (!m.Success) return 0;
        if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var valor)) return 0;

        long multiplicador = m.Groups[2].Value.ToUpperInvariant() switch
        {
            "GIB" => 1024L * 1024 * 1024,
            "MIB" => 1024L * 1024,
            "KIB" => 1024L,
            _ => 1L,
        };
        return (long)(valor * multiplicador);
    }

    [GeneratedRegex(@"S\d{1,2}E(\d{1,3})", RegexOptions.IgnoreCase)]
    private static partial Regex TemporadaEpisodioRegex();

    // " - 13 " / " - 13[" / " - 13(" / " - 13.mkv" / " - 13" al final — el separador
    // "espacio-guion-espacio" antes del número es el estándar de fansubs, distinto de
    // un guion pegado dentro de un título compuesto.
    [GeneratedRegex(@"\s-\s0*(\d{1,4})(?:v\d+)?(?=\s|\[|\(|\.|$)")]
    private static partial Regex GuionEpisodioRegex();

    [GeneratedRegex(@"Episode\s+0*(\d{1,4})", RegexOptions.IgnoreCase)]
    private static partial Regex PalabraEpisodioRegex();

    [GeneratedRegex(@"\b(batch|complete|completo)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PalabraBatchRegex();

    // Rango sin espacios tipo "01-13", "(01-24)" — distinto del separador " - NN " de episodio único.
    [GeneratedRegex(@"\b\d{2,3}-\d{2,3}\b")]
    private static partial Regex RangoEpisodiosRegex();

    [GeneratedRegex(@"\bS\d{1,2}(v\d+)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex TemporadaSolaRegex();

    [GeneratedRegex(@"^([\d.]+)\s*(GiB|MiB|KiB|B)$", RegexOptions.IgnoreCase)]
    private static partial Regex TamanoRegex();
}

public class NyaaSourceService : INyaaSourceService
{
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
    // Categoría "Anime - English-translated" (c=1_2 en Nyaa) — el MVP no busca en
    // raw/non-English, queda para una fase futura si hace falta.
    private const string CategoriaAnimeSubEnglish = "1_2";
    private const int MinimoSeeders = 3;

    private readonly HttpClient _httpClient;

    public NyaaSourceService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<CandidatoTorrent?> BuscarEpisodioAsync(
        IEnumerable<string> titulos,
        int numeroEpisodio,
        string? grupoPreferido = null,
        string? resolucionPreferida = null,
        CancellationToken ct = default)
    {
        var candidatos = await BuscarCandidatosAsync(titulos, numeroEpisodio, grupoPreferido, resolucionPreferida, ct);
        return candidatos.Count > 0 ? candidatos[0] : null;
    }

    public async Task<List<CandidatoTorrent>> BuscarCandidatosAsync(
        IEnumerable<string> titulos,
        int numeroEpisodio,
        string? grupoPreferido = null,
        string? resolucionPreferida = null,
        CancellationToken ct = default)
    {
        foreach (var titulo in titulos.Where(t => !string.IsNullOrWhiteSpace(t)))
        {
            foreach (var termino in AnimeAv1VideoSourceResolver.GenerarTerminosBusqueda(titulo))
            {
                if (ct.IsCancellationRequested) return new List<CandidatoTorrent>();

                var rssXml = await ObtenerRssAsync(termino, ct);
                if (rssXml == null) continue;

                var candidatos = NyaaRssParser.ExtraerCandidatos(rssXml);
                var validos = NyaaRssParser.FiltrarYOrdenarCandidatos(candidatos, numeroEpisodio, MinimoSeeders, grupoPreferido, resolucionPreferida);
                if (validos.Count > 0)
                {
                    AppLogger.Info("NyaaSourceService", $"Episodio {numeroEpisodio}: {validos.Count} candidato(s) con el término '{termino}'.");
                    return validos;
                }
            }
        }
        return new List<CandidatoTorrent>();
    }

    private async Task<string?> ObtenerRssAsync(string termino, CancellationToken ct)
    {
        try
        {
            string url = $"https://nyaa.si/?page=rss&q={Uri.EscapeDataString(termino)}&c={CategoriaAnimeSubEnglish}&f=0";
            if (!Core.UrlSeguridad.EsUrlNyaaPermitida(url)) return null;

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("User-Agent", UserAgent);

            using var res = await _httpClient.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return null;

            return await res.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            AppLogger.Debug("NyaaSourceService", $"Error consultando Nyaa para '{termino}': {ex.Message}");
            return null;
        }
    }
}
