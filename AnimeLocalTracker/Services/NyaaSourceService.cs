using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using AnimeLocalTracker.Core;

namespace AnimeLocalTracker.Services;

/// <summary>
/// INT-01: parseo del RSS de Nyaa.si aislado de la red (igual que
/// <c>AnimeAv1HtmlParser</c>), para poder testearse con fixtures reales sin tocar
/// internet. Nyaa expone un feed RSS oficial y estructurado — nada de scraping de HTML.
/// </summary>
public static partial class NyaaRssParser
{
    private static readonly XNamespace NyaaNs = "https://nyaa.si/xmlns/nyaa";

    /// <summary>Parecido mínimo (sin contar la temporada, que debe coincidir aparte) entre el nombre
    /// de la serie en el release y alguno de los títulos del anime.</summary>
    public const double UmbralNombre = 0.8;

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
    /// Número de episodio del título de un release (o de un archivo dentro de un pack), o null si
    /// no se pudo determinar (o si es un batch — ver <see cref="EsBatch"/>, que el llamador debe
    /// chequear primero). Prueba patrones en orden: "SxxExx", " - NN " (el más común en fansubs),
    /// "Episode NN", "E12"/"EP12". Los episodios decimales ("- 12.5", recopilatorios) no cuentan
    /// como el 12.
    /// </summary>
    public static int? ExtraerNumeroEpisodio(string titulo)
    {
        if (string.IsNullOrWhiteSpace(titulo)) return null;

        var m = TemporadaEpisodioRegex().Match(titulo);
        if (m.Success && int.TryParse(m.Groups[2].Value, out var epSxE)) return epSxE;

        m = GuionEpisodioRegex().Match(titulo);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var epGuion)) return epGuion;

        m = PalabraEpisodioRegex().Match(titulo);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var epPalabra)) return epPalabra;

        m = EpisodioCortoRegex().Match(titulo);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var epCorto)) return epCorto;

        return null;
    }

    /// <summary>
    /// True si el título es claramente un batch (temporada completa/rango de
    /// episodios), no un release de un solo episodio.
    /// </summary>
    public static bool EsBatch(string titulo)
    {
        if (string.IsNullOrWhiteSpace(titulo)) return false;
        if (PalabraBatchRegex().IsMatch(titulo)) return true;
        if (BuscarRango(titulo) != null) return true;
        // "S01"/"S01v2" sin "E<n>" ni "- NN" acompañante: paquete de temporada.
        if (TemporadaSolaRegex().IsMatch(titulo) && ExtraerNumeroEpisodio(titulo) is null) return true;
        return false;
    }

    /// <summary>Lo que dice el título de un release: de qué serie y temporada es, qué episodio(s) trae y en qué idioma.</summary>
    /// <param name="Nombres">Nombre de la serie (con su temporada) y los alternativos que el release pone
    /// entre paréntesis o tras '|'. Los alternativos heredan la temporada del release.</param>
    /// <param name="VariasTemporadas">Pack de varias temporadas ("S1+S2+S3"): el número de episodio no
    /// identifica un archivo (hay un "- 05" por temporada).</param>
    /// <param name="SoloDoblaje">Solo audio doblado (inglés), sin la pista japonesa.</param>
    public sealed record ReleaseNyaa(string? Grupo, List<string> Nombres, int? Episodio, bool EsBatch, int? RangoDesde, int? RangoHasta, bool VariasTemporadas, bool SoloDoblaje);

    /// <summary>Analiza el título de un release de Nyaa (nunca lanza).</summary>
    public static ReleaseNyaa AnalizarRelease(string titulo)
    {
        string t = ExtensionRegex().Replace(titulo ?? "", "").Trim();

        string? grupo = null;
        Match etiqueta;
        while ((etiqueta = EtiquetaInicialRegex().Match(t)).Success)
        {
            grupo ??= etiqueta.Groups[1].Value.Trim();
            t = t[etiqueta.Length..];
        }

        var piezas = t.Split('|');
        string principal = piezas[0];

        var rango = BuscarRango(principal);
        int? episodio = null;
        int? temporadaSxE = null;
        string nombre;

        var sxe = TemporadaEpisodioRegex().Match(principal);
        Match? marcador = null;
        if (rango == null)
        {
            if (sxe.Success) marcador = sxe;
            else if (GuionEpisodioRegex().Match(principal) is { Success: true } g) marcador = g;
            else if (PalabraEpisodioRegex().Match(principal) is { Success: true } w) marcador = w;
            else if (EpisodioCortoRegex().Match(principal) is { Success: true } e) marcador = e;
        }

        if (marcador != null)
        {
            episodio = int.Parse(marcador == sxe ? sxe.Groups[2].Value : marcador.Groups[1].Value, CultureInfo.InvariantCulture);
            if (sxe.Success) temporadaSxE = int.Parse(sxe.Groups[1].Value, CultureInfo.InvariantCulture);
            nombre = principal[..marcador.Index];
        }
        else
        {
            int corte = principal.Length;
            if (rango != null) corte = Math.Min(corte, rango.Value.Indice);
            if (CorteNombreBatchRegex().Match(principal) is { Success: true } c) corte = Math.Min(corte, c.Index);
            nombre = principal[..corte];
        }

        nombre = LimpiarNombre(nombre);

        // Temporada del release: escrita en el nombre, en "SxxEyy", o en otro sitio del título ("(Season 02)").
        int? temporada = temporadaSxE;
        if (temporada == null && FirmaTitulo.Interpretar(nombre).FirstOrDefault() is { Explicita: true } lectura) temporada = lectura.Temporada;
        if (temporada == null && TemporadaEnTituloRegex().Match(principal) is { Success: true } st)
        {
            temporada = int.Parse(st.Groups[1].Success ? st.Groups[1].Value : st.Groups[2].Success ? st.Groups[2].Value : st.Groups[3].Value, CultureInfo.InvariantCulture);
        }

        var nombres = new List<string>();
        void Agregar(string candidato)
        {
            candidato = LimpiarNombre(candidato);
            if (candidato.Length < 3 || SoloMarcadorRegex().IsMatch(candidato) || EtiquetaTecnicaRegex().IsMatch(candidato)) return;
            if (!candidato.Any(char.IsLetter)) return;
            var lecturaCandidato = FirmaTitulo.Interpretar(candidato).FirstOrDefault();
            if (temporada is int s && nombres.Count > 0)
            {
                // Un nombre sin temporada escrita hereda la del release ("The Angel Next Door S02E12 (Otonari no…)").
                if (!lecturaCandidato.Explicita) candidato += $" S{s}";
                // Un nombre alternativo que dice OTRA temporada es una etiqueta equivocada del uploader
                // ("… S03E01 (Re:Zero … 2nd Season Part 2)"): no se usa para identificar el release.
                else if (lecturaCandidato.Temporada != s) return;
            }
            else if (temporada is int s1 && !lecturaCandidato.Explicita)
            {
                candidato += $" S{s1}";
            }
            if (!nombres.Contains(candidato, StringComparer.OrdinalIgnoreCase)) nombres.Add(candidato);
        }

        Agregar(nombre);
        foreach (Match p in ParentesisRegex().Matches(principal))
        {
            Agregar(p.Groups[1].Value.Split(',')[0]);
        }
        foreach (var pieza in piezas.Skip(1))
        {
            Agregar(CorchetesRegex().Replace(pieza, " ").Split(',')[0]);
        }

        bool esBatch = rango != null || (episodio == null && (PalabraBatchRegex().IsMatch(t) || TemporadaSolaRegex().IsMatch(principal) || TemporadaEnTituloRegex().IsMatch(principal)));
        bool variasTemporadas = VariasTemporadasRegex().IsMatch(t);
        bool soloDoblaje = DoblajeRegex().IsMatch(t) && !AudioOriginalRegex().IsMatch(t);

        return new ReleaseNyaa(grupo, nombres, episodio, esBatch, rango?.Desde, rango?.Hasta, variasTemporadas, soloDoblaje);
    }

    /// <summary>True si el release es de este anime y de esta temporada (compara con todos sus títulos).</summary>
    public static bool EsDelAnime(ReleaseNyaa release, IEnumerable<string> titulos)
        => release.Nombres.Count > 0 && FirmaTitulo.Evaluar(titulos, release.Nombres).MismaTemporada >= UmbralNombre;

    /// <summary>
    /// Dos pasadas (Fase 2a): 1) el mejor release de UN solo episodio (episodio exacto,
    /// sin batch, semillas suficientes); 2) si no hay ninguno, el mejor batch/temporada
    /// completa con semillas suficientes. Sin títulos no se verifica de qué anime es cada
    /// release (solo para compatibilidad: la búsqueda real siempre pasa los títulos).
    /// </summary>
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

    /// <summary>Igual que la sobrecarga con títulos, sin verificar de qué anime es cada release.</summary>
    public static List<CandidatoTorrent> FiltrarYOrdenarCandidatos(
        IEnumerable<CandidatoTorrent> candidatos,
        int numeroEpisodio,
        int minimoSeeders,
        string? grupoPreferido = null,
        string? resolucionPreferida = null)
        => FiltrarYOrdenarCandidatos(candidatos, null, numeroEpisodio, minimoSeeders, grupoPreferido, resolucionPreferida);

    /// <summary>
    /// Todos los candidatos válidos, mejor primero. Válido = es de este anime y temporada (si se pasan
    /// títulos), trae el episodio pedido (release suelto con ese número, o pack cuyo rango lo incluye) y
    /// tiene semillas suficientes. Los packs de varias temporadas se descartan. Orden: episodio suelto
    /// antes que pack (los packs solo si no hay ningún suelto), audio original antes que solo doblaje,
    /// y luego grupo preferido, resolución preferida y semillas.
    /// </summary>
    /// <param name="incluirDudosos">Para el selector manual: añade al final, marcados como
    /// <see cref="CandidatoTorrent.Dudoso"/>, los releases con el episodio correcto cuyo nombre no se pudo
    /// confirmar como este anime — el usuario decide.</param>
    /// <param name="precuelas">Partes anteriores del anime (la más cercana primero): un release de una de
    /// ellas vale si su número sigue la numeración continua (ver <see cref="EsEpisodioContinuo"/>).</param>
    public static List<CandidatoTorrent> FiltrarYOrdenarCandidatos(
        IEnumerable<CandidatoTorrent> candidatos,
        IReadOnlyCollection<string>? titulos,
        int numeroEpisodio,
        int minimoSeeders,
        string? grupoPreferido = null,
        string? resolucionPreferida = null,
        bool incluirDudosos = false,
        IReadOnlyList<PrecuelaAnime>? precuelas = null)
    {
        var analizados = candidatos
            .Where(c => c.Seeders >= minimoSeeders)
            .Select(c => (Candidato: c, Release: AnalizarRelease(c.Titulo)))
            .Select(x => (x.Candidato, x.Release, DelAnime: titulos == null || EsDelAnime(x.Release, titulos)))
            .ToList();

        // Releases de una parte anterior con numeración continua ("2nd Season - 14" = ep 1 de la parte 2).
        var continuos = analizados
            .Where(x => !x.DelAnime && EsEpisodioContinuo(x.Release, numeroEpisodio, precuelas))
            .ToList();

        bool EsSuelto(ReleaseNyaa r) => !r.EsBatch && r.Episodio == numeroEpisodio;
        bool EsSinNumero(ReleaseNyaa r) => !r.EsBatch && r.Episodio == null && !r.VariasTemporadas;
        bool EsPackQueLoIncluye(ReleaseNyaa r) => r.EsBatch && !r.VariasTemporadas
            && (r.RangoDesde == null || (numeroEpisodio >= r.RangoDesde && numeroEpisodio <= r.RangoHasta));

        IEnumerable<CandidatoTorrent> Ordenar(IEnumerable<(CandidatoTorrent Candidato, ReleaseNyaa Release, bool DelAnime)> lista) => lista
            .OrderBy(x => x.Release.SoloDoblaje)
            .ThenByDescending(x => ContienePreferencia(x.Candidato.Titulo, grupoPreferido))
            .ThenByDescending(x => ContienePreferencia(x.Candidato.Titulo, resolucionPreferida))
            .ThenByDescending(x => x.Release.RangoDesde != null) // un pack que declara el rango es más fiable
            .ThenByDescending(x => x.Candidato.Seeders)
            .Select(x => x.Candidato with { EsBatch = x.Release.EsBatch, Dudoso = !x.DelAnime });

        // Los de numeración continua cuentan como del anime: ya se verificó que son de su parte anterior.
        var sueltos = Ordenar(analizados.Where(x => x.DelAnime && EsSuelto(x.Release))
                .Concat(continuos.Select(x => (x.Candidato, x.Release, DelAnime: true))))
            .ToList();
        // Episodio 1 de algo sin numeración (una película, un especial): releases del anime sin número de
        // episodio ni rango ("Dragon Ball Z Battle of Gods (2013) [BD 1080p]"). Dentro del torrent se
        // elige el archivo del episodio 1 o, si trae un solo video, ese.
        var packs = Ordenar(analizados.Where(x => x.DelAnime && EsPackQueLoIncluye(x.Release)))
            .Concat(numeroEpisodio == 1 ? Ordenar(analizados.Where(x => x.DelAnime && EsSinNumero(x.Release))) : [])
            .ToList();

        if (!incluirDudosos) return sueltos.Count > 0 ? sueltos : packs;

        return sueltos
            .Concat(packs)
            .Concat(Ordenar(analizados.Where(x => !x.DelAnime && EsSuelto(x.Release) && !continuos.Contains(x))))
            .Concat(Ordenar(analizados.Where(x => !x.DelAnime && EsPackQueLoIncluye(x.Release))))
            .ToList();
    }

    /// <summary>
    /// Episodio suelto de una parte anterior numerado de forma continua: su número es exactamente el pedido
    /// más los episodios de las partes que hay entre medias (y por tanto mayor que los de esa parte, así
    /// que no puede ser uno de sus propios episodios). Cubre "2nd Season - 14" y también la numeración
    /// absoluta ("Re:Zero - 39" = temporada 1 de 25 + parte 1 de 13 + 1).
    /// </summary>
    public static bool EsEpisodioContinuo(ReleaseNyaa release, int numeroEpisodio, IReadOnlyList<PrecuelaAnime>? precuelas)
    {
        if (precuelas == null || release.EsBatch || release.Episodio is not int episodio) return false;
        int desfase = 0;
        foreach (var precuela in precuelas)
        {
            if (precuela.Episodios <= 0) return false; // sin el total de esa parte no se puede calcular
            desfase += precuela.Episodios;
            if (episodio == numeroEpisodio + desfase && precuela.Titulos is { Count: > 0 } titulos && EsDelAnime(release, titulos))
                return true;
        }
        return false;
    }

    /// <summary>Números con los que una parte anterior publicaría el episodio pedido (numeración continua).</summary>
    public static IEnumerable<int> NumerosContinuos(int numeroEpisodio, IReadOnlyList<PrecuelaAnime>? precuelas)
    {
        int desfase = 0;
        foreach (var precuela in precuelas ?? [])
        {
            if (precuela.Episodios <= 0) yield break;
            desfase += precuela.Episodios;
            yield return numeroEpisodio + desfase;
        }
    }

    private static bool ContienePreferencia(string titulo, string? preferencia)
        => !string.IsNullOrWhiteSpace(preferencia) && titulo.Contains(preferencia, StringComparison.OrdinalIgnoreCase);

    private static string LimpiarNombre(string texto)
    {
        texto = CorchetesRegex().Replace(texto, " ");
        texto = ParentesisRegex().Replace(texto, " ");
        texto = texto.Replace('_', ' ').Replace('.', ' ');
        return BordesRegex().Replace(texto, "").Trim();
    }

    /// <summary>Rango de episodios de un pack: "(01-12)", "01 ~ 12", "Episode 1089-1100", "1100 & 1101".</summary>
    private static (int Desde, int Hasta, int Indice)? BuscarRango(string titulo)
    {
        foreach (var regex in new[] { RangoEpisodiosRegex(), RangoTildeRegex(), RangoPalabraRegex(), RangoAmpersandRegex() })
        {
            var m = regex.Match(titulo);
            if (!m.Success) continue;
            int desde = int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture);
            int hasta = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
            if (hasta > desde) return (desde, hasta, m.Index);
        }
        return null;
    }

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

    [GeneratedRegex(@"\bS(\d{1,2})\s?E(\d{1,4})(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex TemporadaEpisodioRegex();

    // " - 13 " / " - 13[" / " - 13(" / " - 13.mkv" / " - 13" al final — el separador
    // "espacio-guion-espacio" antes del número es el estándar de fansubs, distinto de
    // un guion pegado dentro de un título compuesto. "- 12.5" (recopilatorio) no es el 12.
    [GeneratedRegex(@"\s-\s0*(\d{1,4})(?:v\d+)?(?=\s|\[|\(|\.(?!\d)|$)")]
    private static partial Regex GuionEpisodioRegex();

    [GeneratedRegex(@"\bEpisode\s+0*(\d{1,4})(?![\d.])", RegexOptions.IgnoreCase)]
    private static partial Regex PalabraEpisodioRegex();

    // "E12", "EP12", "Ep 12" (la E en mayúscula: evita confundir palabras normales)
    [GeneratedRegex(@"\bE[Pp]?\s?0*(\d{1,4})(?:v\d+)?\b(?!\.\d)")]
    private static partial Regex EpisodioCortoRegex();

    [GeneratedRegex(@"\b(batch|complete|completo)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PalabraBatchRegex();

    // Rango sin espacios tipo "01-13", "(01-24)" — distinto del separador " - NN " de episodio único.
    [GeneratedRegex(@"(?<![\w.])(?<d>\d{1,4})-(?<h>\d{1,4})(?![\w.])")]
    private static partial Regex RangoEpisodiosRegex();

    // "01 ~ 12", "01~12"
    [GeneratedRegex(@"(?<![\w.])(?<d>\d{1,4})\s?~\s?(?<h>\d{1,4})(?![\w.])")]
    private static partial Regex RangoTildeRegex();

    // "Episode 1089-1100", "Episodes 1 - 12"
    [GeneratedRegex(@"\bEpisodes?\s+(?<d>\d{1,4})\s?-\s?(?<h>\d{1,4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex RangoPalabraRegex();

    // "1100 & 1101" (dos episodios en un release)
    [GeneratedRegex(@"(?<![\w.])(?<d>\d{1,4})\s?&\s?(?<h>\d{1,4})(?![\w.])")]
    private static partial Regex RangoAmpersandRegex();

    [GeneratedRegex(@"\bS\d{1,2}(v\d+)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex TemporadaSolaRegex();

    // "(Season 02)", "Season 2", "2nd Season", "S02" en cualquier parte del título
    [GeneratedRegex(@"\bSeason\s?0*(\d{1,2})\b|\b(\d{1,2})(?:st|nd|rd|th)\s+Season\b|\bS0*(\d{1,2})(?:v\d+)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex TemporadaEnTituloRegex();

    // "S1+S2", "S01-S03", "Season 1-3", "Seasons 1 & 2", "Complete Series"
    [GeneratedRegex(@"\bS\d{1,2}\s?[+&]\s?S?\d{1,2}\b|\bS\d{1,2}\s?-\s?S\d{1,2}\b|\bSeasons?\s?\d{1,2}\s?[-~&+]\s?\d{1,2}\b|\bSeasons\b|\bComplete\s+Series\b", RegexOptions.IgnoreCase)]
    private static partial Regex VariasTemporadasRegex();

    [GeneratedRegex(@"\b(dub|dubbed|dublado|doblaje)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DoblajeRegex();

    [GeneratedRegex(@"\b(dual|multi[- ]?audio|japanese|jpn|jap)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AudioOriginalRegex();

    [GeneratedRegex(@"\.(mkv|mp4|avi)$", RegexOptions.IgnoreCase)]
    private static partial Regex ExtensionRegex();

    [GeneratedRegex(@"^\s*\[([^\]]*)\]\s*")]
    private static partial Regex EtiquetaInicialRegex();

    [GeneratedRegex(@"\[[^\]]*\]")]
    private static partial Regex CorchetesRegex();

    [GeneratedRegex(@"\(([^()]*)\)")]
    private static partial Regex ParentesisRegex();

    [GeneratedRegex(@"^[\s\-–:~|,]+|[\s\-–:~|,]+$")]
    private static partial Regex BordesRegex();

    // Dónde termina el nombre de la serie en un pack sin número de episodio
    [GeneratedRegex(@"[\(\[]|\b(?:batch|complete|\d{3,4}p|BD|BDRip|BluRay|WEB|WEB-?DL|WEBRip)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CorteNombreBatchRegex();

    [GeneratedRegex(@"^(S\d{1,2}|Season\s?\d{1,2}|\d{1,2}(st|nd|rd|th)\s+Season)$", RegexOptions.IgnoreCase)]
    private static partial Regex SoloMarcadorRegex();

    // Paréntesis que no son nombres: "(1080p)", "(Weekly)", "(Dual Audio, Multi Subs)", "(01-12)"...
    [GeneratedRegex(@"^(\d{3,4}p|web|web-?dl|webrip|bd|bdrip|bluray|hevc|x26[45]|h\.?26[45]|aac|opus|flac|dual|multi|multi-?subs?|sub|subs|dub|batch|weekly|uncensored|english|eng|vostfr|raw|end|final|unofficial batch|\d+(\s?[-~]\s?\d+)?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex EtiquetaTecnicaRegex();

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

    /// <summary>Títulos distintos con los que se consulta Nyaa (el resto solo sirve para verificar).</summary>
    private const int MaxTitulosConsultados = 4;

    /// <summary>Títulos que se consultan a la vez (2 consultas por título).</summary>
    private const int TitulosPorTanda = 2;

    private readonly HttpClient _httpClient;

    /// <summary>Partes anteriores del anime (numeración continua). Null en tests o sin datos.</summary>
    private readonly Func<int, CancellationToken, Task<IReadOnlyList<PrecuelaAnime>>>? _precuelas;

    public NyaaSourceService(HttpClient httpClient, Func<int, CancellationToken, Task<IReadOnlyList<PrecuelaAnime>>>? precuelas = null)
    {
        _httpClient = httpClient;
        _precuelas = precuelas;
    }

    public async Task<CandidatoTorrent?> BuscarEpisodioAsync(
        IEnumerable<string> titulos,
        int numeroEpisodio,
        string? grupoPreferido = null,
        string? resolucionPreferida = null,
        CancellationToken ct = default)
    {
        var candidatos = await BuscarCandidatosAsync(titulos, numeroEpisodio, grupoPreferido, resolucionPreferida, null, ct);
        return candidatos.Count > 0 ? candidatos[0] : null;
    }

    public Task<List<CandidatoTorrent>> BuscarCandidatosAsync(
        IEnumerable<string> titulos,
        int numeroEpisodio,
        string? grupoPreferido = null,
        string? resolucionPreferida = null,
        int? aniListId = null,
        CancellationToken ct = default)
        => BuscarAsync(titulos, numeroEpisodio, grupoPreferido, resolucionPreferida, incluirDudosos: false, aniListId, ct);

    public Task<List<CandidatoTorrent>> BuscarCandidatosParaElegirAsync(
        IEnumerable<string> titulos,
        int numeroEpisodio,
        string? grupoPreferido = null,
        string? resolucionPreferida = null,
        int? aniListId = null,
        CancellationToken ct = default)
        => BuscarAsync(titulos, numeroEpisodio, grupoPreferido, resolucionPreferida, incluirDudosos: true, aniListId, ct);

    /// <summary>
    /// Por cada título (en tandas de <see cref="TitulosPorTanda"/>, en paralelo) se hacen dos consultas:
    /// "nombre + número de episodio" — encuentra episodios antiguos que ya no salen entre los 75
    /// resultados más recientes que da el RSS (ej. One Piece 1100) — y "nombre" solo (releases con
    /// "S02E05", que no contienen el número suelto, y packs). Los resultados se juntan y cada release
    /// se verifica contra TODOS los títulos del anime, temporada incluida. Se para en cuanto hay un
    /// episodio suelto verificado.
    /// </summary>
    private async Task<List<CandidatoTorrent>> BuscarAsync(
        IEnumerable<string> titulos, int numeroEpisodio, string? grupoPreferido, string? resolucionPreferida, bool incluirDudosos, int? aniListId, CancellationToken ct)
    {
        var todos = titulos.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var consultas = ConsultasPorTitulo(todos);
        var encontrados = new Dictionary<string, CandidatoTorrent>(StringComparer.OrdinalIgnoreCase);
        string numero = Numero(numeroEpisodio);
        var precuelas = await ObtenerPrecuelasAsync(aniListId, ct);

        List<CandidatoTorrent> Filtrar(bool dudosos) => NyaaRssParser.FiltrarYOrdenarCandidatos(
            encontrados.Values, todos, numeroEpisodio, MinimoSeeders, grupoPreferido, resolucionPreferida, dudosos, precuelas);

        for (int i = 0; i < consultas.Count; i += TitulosPorTanda)
        {
            if (ct.IsCancellationRequested) return new List<CandidatoTorrent>();

            var tanda = consultas.Skip(i).Take(TitulosPorTanda).SelectMany(c => new[] { $"{c} {numero}", c }).ToList();
            // Temporada o parte escrita: también el título tal cual ("… 2nd Season Part 2"). Sin marcador
            // la consulta trae todas las temporadas y un pack antiguo de esta parte queda fuera de los
            // 75 resultados del RSS.
            if (i == 0 && ConsultaConTemporada(todos) is string conTemporada) tanda.Add(conTemporada);
            // Numeración continua: la parte anterior publica este episodio con otro número ("… 14").
            if (i == 0 && consultas.Count > 0)
                tanda.AddRange(NyaaRssParser.NumerosContinuos(numeroEpisodio, precuelas).Take(MaxConsultasContinuas).Select(n => $"{consultas[0]} {Numero(n)}"));
            var respuestas = await Task.WhenAll(tanda.Select(q => ObtenerRssAsync(q, ct)));
            foreach (var candidato in respuestas.SelectMany(NyaaRssParser.ExtraerCandidatos))
            {
                encontrados.TryAdd(candidato.TorrentUrl, candidato);
            }

            var validos = Filtrar(dudosos: false);
            if (validos.Any(v => !v.EsBatch))
            {
                AppLogger.Info("NyaaSourceService", $"Episodio {numeroEpisodio}: {validos.Count} candidato(s) verificados de {encontrados.Count} resultados.");
                return incluirDudosos ? Filtrar(dudosos: true) : validos;
            }
        }

        var finales = Filtrar(incluirDudosos);
        AppLogger.Info("NyaaSourceService", finales.Count > 0
            ? $"Episodio {numeroEpisodio}: solo packs o releases sin confirmar ({finales.Count}) de {encontrados.Count} resultados."
            : $"Episodio {numeroEpisodio}: ningún release de este anime y temporada entre {encontrados.Count} resultados.");
        return finales;
    }

    /// <summary>
    /// Qué se escribe en el buscador de Nyaa por cada título: solo alfabeto latino (los releases en
    /// inglés no usan tailandés, ruso ni japonés), sin siglas cortas como "OP" (en One Piece traía
    /// packs de otros animes), sin signos que Nyaa interpreta como operadores ("-palabra" EXCLUYE esa
    /// palabra: "Re:ZERO -Starting Life…" no encontraba nada) y sin el marcador de temporada escrito
    /// ("2nd Season" frente a "S2": la temporada se verifica después en cada release).
    /// </summary>
    public static List<string> ConsultasPorTitulo(IEnumerable<string> titulos)
    {
        var consultas = new List<string>();
        foreach (var titulo in titulos)
        {
            if (!FirmaTitulo.EsAlfabetoBuscable(titulo, incluirJapones: false)) continue;
            string consulta = FirmaTitulo.Interpretar(titulo).FirstOrDefault().Base ?? "";
            if (consulta.Replace(" ", "").Length < 4) continue;
            if (!consultas.Contains(consulta)) consultas.Add(consulta);
            if (consultas.Count >= MaxTitulosConsultados) break;
        }
        return consultas;
    }

    private const int MaxConsultasContinuas = 2;

    private static string Numero(int n) => n.ToString(n < 10 ? "D2" : "D", CultureInfo.InvariantCulture);

    private async Task<IReadOnlyList<PrecuelaAnime>> ObtenerPrecuelasAsync(int? aniListId, CancellationToken ct)
    {
        if (!aniListId.HasValue || _precuelas == null) return [];
        try
        {
            return await _precuelas(aniListId.Value, ct) ?? [];
        }
        catch (Exception ex)
        {
            AppLogger.Debug("NyaaSourceService", $"No se pudieron obtener las precuelas de {aniListId}: {ex.Message}");
            return [];
        }
    }

    /// <summary>El primer título latino con temporada o parte escrita, normalizado (sin signos que Nyaa interprete), o null.</summary>
    private static string? ConsultaConTemporada(IEnumerable<string> titulos)
    {
        var titulo = titulos.FirstOrDefault(t => FirmaTitulo.EsAlfabetoBuscable(t, incluirJapones: false));
        if (titulo == null) return null;
        var lectura = FirmaTitulo.Interpretar(titulo).FirstOrDefault();
        return lectura.Explicita || lectura.Parte > 1 ? FirmaTitulo.Normalizar(titulo) : null;
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
