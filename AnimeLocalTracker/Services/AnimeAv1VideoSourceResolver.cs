using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Core;

namespace AnimeLocalTracker.Services;

/// <summary>Una parte anterior del anime (PREQUEL en AniList): su MAL ID, cuántos episodios tiene y sus títulos.</summary>
public readonly record struct PrecuelaAnime(int? MalId, int Episodios, IReadOnlyList<string>? Titulos = null);

/// <summary>
/// INT-01: contrato tipado del scraping de animeav1.com — todo el parseo de HTML vive
/// aquí, aislado de la red, para poder testearse con fixtures reales.
/// </summary>
public static partial class AnimeAv1HtmlParser
{
    /// <summary>Servidor de video publicado por la página de episodio, y la pista de audio
    /// a la que pertenece ("SUB" = japonés subtitulado, "DUB" = doblaje latino, tal cual las
    /// publica el sitio — no se hardcodean más valores que esos dos por si el sitio cambia).</summary>
    public readonly record struct EmbedServidor(string Server, string Url, string Audio);

    /// <summary>
    /// Extrae los embeds de servidores de la página de episodio (SvelteKit). El sitio incrusta
    /// la lista en JSON con una clave por pista de audio: embeds:{SUB:[{server:"HLS",url:"https://..."},...],DUB:[...]}.
    /// Cada pista se extrae por separado para poder etiquetar el idioma de cada servidor.
    /// Solo se devuelven URLs https; el filtrado por host permitido lo hace UrlSeguridad.
    /// </summary>
    public static List<EmbedServidor> ExtraerEmbeds(string html)
    {
        var lista = new List<EmbedServidor>();
        if (string.IsNullOrWhiteSpace(html)) return lista;

        int inicio = html.IndexOf("embeds:{", StringComparison.Ordinal);
        if (inicio < 0) return lista;

        // Se incluye el "]}" final: hace falta el corchete de cierre de la ÚLTIMA pista para
        // que la regex de grupos (CLAVE:[...]) pueda delimitar su contenido.
        int fin = html.IndexOf("]}", inicio, StringComparison.Ordinal);
        string seccion = fin > inicio ? html[inicio..(fin + 2)] : html[inicio..];

        foreach (Match grupo in AudioGrupoRegex().Matches(seccion))
        {
            string audio = grupo.Groups[1].Value.Trim();
            string contenido = grupo.Groups[2].Value;

            foreach (Match m in EmbedServidorRegex().Matches(contenido))
            {
                string server = m.Groups[1].Value.Trim();
                string url = m.Groups[2].Value.Trim();
                if (!string.IsNullOrWhiteSpace(server) && !string.IsNullOrWhiteSpace(url))
                {
                    lista.Add(new EmbedServidor(server, url, audio));
                }
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
    /// <param name="audioPreferido">"SUB"/"DUB" (AppSettings.PreferenciaAudioAnimeAv1). Es una
    /// PREFERENCIA con fallback, no un filtro estricto: si la pista pedida no tiene ningún
    /// servidor disponible, se cae a la otra en vez de no descargar nada. Null/vacío = sin
    /// preferencia, mismo orden de siempre (solo por servidor, sin importar el idioma).</param>
    /// <param name="servidorPreferido">Servidor preferido (AppSettings.ServidorPreferidoAnimeAv1,
    /// ej. "MP4Upload"). También es PREFERENCIA con fallback: se prueba primero y, si no
    /// resuelve, se sigue con el resto en el orden de siempre. Null/vacío = sin preferencia.</param>
    public static List<EmbedServidor> OrdenarEmbedsPorPreferencia(IEnumerable<EmbedServidor> embeds, string? audioPreferido = null, string? servidorPreferido = null)
    {
        var preferenciaServidor = new[] { "MP4Upload", "HLS", "Voe", "UPNShare", "Byse" };
        if (!string.IsNullOrWhiteSpace(servidorPreferido))
        {
            preferenciaServidor = preferenciaServidor
                .Where(s => !s.Equals(servidorPreferido, StringComparison.OrdinalIgnoreCase))
                .Prepend(servidorPreferido)
                .ToArray();
        }

        List<EmbedServidor> PorServidor(IEnumerable<EmbedServidor> fuente) => preferenciaServidor
            .SelectMany((nombre, i) => fuente
                .Where(e => e.Server.Equals(nombre, StringComparison.OrdinalIgnoreCase))
                .Select(e => (e, i)))
            .OrderBy(x => x.i)
            .Select(x => x.e)
            .ToList();

        var lista = embeds as ICollection<EmbedServidor> ?? embeds.ToList();

        if (string.IsNullOrWhiteSpace(audioPreferido))
        {
            return PorServidor(lista);
        }

        var delAudioPedido = PorServidor(lista.Where(e => e.Audio.Equals(audioPreferido, StringComparison.OrdinalIgnoreCase)));
        var delOtroAudio = PorServidor(lista.Where(e => !e.Audio.Equals(audioPreferido, StringComparison.OrdinalIgnoreCase)));
        return delAudioPedido.Concat(delOtroAudio).ToList();
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

    /// <summary>Un resultado del buscador del catálogo: slug de la página y título (null si no se pudo leer).</summary>
    public readonly record struct ResultadoCatalogo(string Slug, string? Titulo);

    /// <summary>
    /// Resultados de una búsqueda del catálogo en el orden del sitio (relevancia). Se leen de la lista
    /// results:[{id,title,synopsis,categoryId,slug,...}] del payload: la página trae además los ~47
    /// GÉNEROS del filtro (slug:"accion", slug:"isekai"...) que el extractor antiguo tomaba por animes
    /// y hacía revisar como candidatos (404 seguros que gastaban el presupuesto de la búsqueda). Sin
    /// esa lista (cambio del sitio) se cae a los enlaces /media/{slug} de la página.
    /// </summary>
    public static List<ResultadoCatalogo> ExtraerResultadosCatalogo(string? html)
    {
        var lista = new List<ResultadoCatalogo>();
        if (string.IsNullOrWhiteSpace(html)) return lista;
        var vistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int inicio = html.IndexOf("results:[", StringComparison.Ordinal);
        if (inicio >= 0)
        {
            foreach (var objeto in ObjetosDelArreglo(html, inicio + "results:".Length))
            {
                var slug = SlugCampoRegex().Match(objeto);
                if (!slug.Success || !vistos.Add(slug.Groups[1].Value)) continue;
                var titulo = TituloCampoRegex().Match(objeto);
                lista.Add(new ResultadoCatalogo(slug.Groups[1].Value, titulo.Success ? titulo.Groups[1].Value.Replace("\\\"", "\"") : null));
            }
            return lista;
        }

        foreach (Match m in EnlaceMediaRegex().Matches(html))
        {
            string slug = m.Groups[1].Value;
            if (!slug.Equals("catalogo", StringComparison.OrdinalIgnoreCase) && vistos.Add(slug)) lista.Add(new ResultadoCatalogo(slug, null));
        }
        return lista;
    }

    /// <summary>Cada objeto {...} de primer nivel del arreglo que empieza en <paramref name="inicioArreglo"/> (respetando cadenas).</summary>
    private static IEnumerable<string> ObjetosDelArreglo(string texto, int inicioArreglo)
    {
        int profundidad = 0, inicioObjeto = -1;
        bool enCadena = false;
        for (int i = inicioArreglo; i < texto.Length; i++)
        {
            char c = texto[i];
            if (enCadena)
            {
                if (c == '\\') i++;
                else if (c == '"') enCadena = false;
                continue;
            }
            switch (c)
            {
                case '"':
                    enCadena = true;
                    break;
                case '[' or '{':
                    profundidad++;
                    if (c == '{' && profundidad == 2) inicioObjeto = i;
                    break;
                case ']' or '}':
                    if (c == '}' && profundidad == 2 && inicioObjeto >= 0) yield return texto[inicioObjeto..(i + 1)];
                    profundidad--;
                    if (profundidad == 0) yield break;
                    break;
            }
        }
    }

    /// <summary>Extrae slugs candidatos del catálogo (solo resultados: sin géneros ni "catalogo").</summary>
    public static IEnumerable<string> ExtraerSlugs(string html)
    {
        if (html != null && html.Contains("results:[", StringComparison.Ordinal))
        {
            return ExtraerResultadosCatalogo(html).Select(r => r.Slug);
        }

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

    // Cada pista de audio es una clave de nivel superior dentro de embeds:{...}, ej. SUB:[...],DUB:[...].
    // Como las urls/servidores no contienen '[' ni ']', el (.*?) no-goloso siempre para en el
    // corchete de cierre correcto de esa pista, sin necesitar balanceo de llaves.
    [GeneratedRegex(@"([A-Za-z]+)\s*:\s*\[(.*?)\]", RegexOptions.Singleline)]
    private static partial Regex AudioGrupoRegex();

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

    [GeneratedRegex(@"/media/([a-zA-Z0-9_-]+)")]
    private static partial Regex EnlaceMediaRegex();

    [GeneratedRegex(@"(?<![\w.])slug:""([a-zA-Z0-9_-]+)""")]
    private static partial Regex SlugCampoRegex();

    [GeneratedRegex(@"(?<![\w.])title:""((?:[^""\\]|\\.)*)""")]
    private static partial Regex TituloCampoRegex();

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
        Func<int, CancellationToken, Task<List<string>?>>? titulosDesdeAniList = null,
        IDatabaseService? database = null,
        Func<int, CancellationToken, Task<IReadOnlyList<PrecuelaAnime>>>? precuelas = null)
    {
        _httpClient = httpClient;
        _malIdResolver = malIdResolver;
        _similitudNombres = similitudNombres;
        _titulosDesdeAniList = titulosDesdeAniList;
        _database = database;
        _precuelas = precuelas;
    }

    /// <summary>
    /// Cadena de precuelas directas del anime (la más cercana primero) con su MAL ID y total de
    /// episodios: permite reconocer la página del sitio que junta varias partes en una. Null = sin datos.
    /// </summary>
    private readonly Func<int, CancellationToken, Task<IReadOnlyList<PrecuelaAnime>>>? _precuelas;

    /// <summary>Donde se guarda la página verificada de cada anime para que sobreviva al cierre de la app (null en tests).</summary>
    private readonly IDatabaseService? _database;

    public async Task<string?> BuscarUrlEpisodioAsync(IEnumerable<string> titulos, int numeroEpisodio, int? aniListId = null, string? audioPreferido = null, string? servidorPreferido = null, CancellationToken cancellationToken = default)
    {
        // FASE 1 (multi-servidor): obtener los embeds de la página del episodio.
        // El C# solo resuelve MP4Upload; los demás servidores los orquesta
        // ProveedorVideoAnimeAv1 con yt-dlp.
        var embeds = await ObtenerEmbedsEpisodioAsync(titulos, numeroEpisodio, aniListId, cancellationToken);
        if (embeds.Count == 0) return null;

        var ordenados = AnimeAv1HtmlParser.OrdenarEmbedsPorPreferencia(embeds, audioPreferido, servidorPreferido);
        var mp4 = ordenados.FirstOrDefault(e => e.Server.Equals("MP4Upload", StringComparison.OrdinalIgnoreCase));
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

        // Atajo: si ya se verificó qué página del sitio es este anime (p. ej. al bajar el episodio
        // anterior de la misma temporada, hoy o en otra sesión), se va directo a ella en vez de
        // repetir toda la búsqueda.
        string? slugYaProbado = null;
        if (aniListId.HasValue && await ObtenerMediaConocidoAsync(aniListId.Value) is MediaVerificado conocido)
        {
            var embedsConocidos = await ObtenerEmbedsDeMediaConocidoAsync(conocido, numeroEpisodio, titulosLista, aniListId, cancellationToken);
            if (embedsConocidos.Count > 0) return embedsConocidos;
            // El episodio no está en esa página (aún no publicado, o el sitio lo separó en otra): búsqueda completa.
            slugYaProbado = conocido.Slug;
        }

        if (!aniListId.HasValue) return await BuscarEmbedsCompletoAsync(titulosLista, numeroEpisodio, null, cancellationToken);

        // Una sola búsqueda completa a la vez por anime: al pedir 5 episodios de golpe de un anime aún
        // no guardado, cada uno repetía la misma búsqueda (decenas de peticiones iguales en paralelo).
        // Los demás esperan y usan la página que encuentre el primero.
        var candado = _busquedasPorAnime.GetOrAdd(aniListId.Value, _ => new SemaphoreSlim(1, 1));
        try { await candado.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) { return []; }
        try
        {
            if (await ObtenerMediaConocidoAsync(aniListId.Value) is MediaVerificado encontradoPorOtro
                && !string.Equals(encontradoPorOtro.Slug, slugYaProbado, StringComparison.OrdinalIgnoreCase))
            {
                var embeds = await ObtenerEmbedsDeMediaConocidoAsync(encontradoPorOtro, numeroEpisodio, titulosLista, aniListId, cancellationToken);
                if (embeds.Count > 0) return embeds;
            }
            return await BuscarEmbedsCompletoAsync(titulosLista, numeroEpisodio, aniListId, cancellationToken);
        }
        finally
        {
            candado.Release();
        }
    }

    /// <summary>Candado por anime para no repetir en paralelo la misma búsqueda completa.</summary>
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _busquedasPorAnime = new();

    /// <summary>Búsqueda completa en el catálogo (sin atajo): candidatos, verificación y embeds del episodio.</summary>
    private async Task<List<AnimeAv1HtmlParser.EmbedServidor>> BuscarEmbedsCompletoAsync(
        List<string> titulosLista, int numeroEpisodio, int? aniListId, CancellationToken cancellationToken)
    {
        // Títulos adicionales desde AniList (native japonés, synonyms…) — la biblioteca local puede
        // no tenerlos guardados — y el MAL ID esperado: son independientes, se piden a la vez.
        var tareaTitulosExtra = ObtenerTitulosExtraAsync(aniListId, cancellationToken);
        var tareaMalId = ObtenerMalIdEsperadoAsync(aniListId, cancellationToken);
        var tareaPrecuelas = ObtenerPrecuelasAsync(aniListId, cancellationToken);
        foreach (var t in await tareaTitulosExtra)
        {
            if (!titulosLista.Contains(t, StringComparer.OrdinalIgnoreCase)) titulosLista.Add(t);
        }

        var busqueda = new BusquedaEnCurso(titulosLista, numeroEpisodio, aniListId, await tareaMalId, await tareaPrecuelas);

        // Dos tandas: primero los términos más fiables (títulos completos y sus partes principales).
        // Casi siempre basta con ellos y los términos genéricos (primeras/últimas palabras), que son
        // los que traen ruido, ni se llegan a buscar.
        var terminos = GenerarTerminosOrdenados(titulosLista);
        var primeraTanda = terminos.Where(t => t.Prioridad <= 1).Take(MaxTerminosPrimeraTanda).ToList();
        var segundaTanda = terminos.Except(primeraTanda).Take(MaxTerminosSegundaTanda).ToList();

        foreach (var (tanda, incluirGenerados) in new[] { (primeraTanda, true), (segundaTanda, false) })
        {
            if (cancellationToken.IsCancellationRequested) return [];
            if (tanda.Count == 0 && !incluirGenerados) continue;

            var candidatos = await ObtenerCandidatosAsync(titulosLista, tanda.Select(t => t.Termino).ToList(), incluirGenerados, busqueda.Encolados, cancellationToken);
            var embeds = await ProbarCandidatosAsync(busqueda, candidatos, cancellationToken);
            if (embeds.Count > 0) return embeds;
            if (busqueda.Probes >= MaxMediaProbes) break;
        }

        AppLogger.Info("AnimeAv1VideoSourceResolver",
            $"'{titulosLista[0]}' ep {numeroEpisodio}: no está en el sitio ({busqueda.Probes} páginas revisadas).");
        return [];
    }

    /// <summary>Estado de una búsqueda completa (presupuesto de páginas compartido entre tandas).</summary>
    private sealed class BusquedaEnCurso(List<string> titulos, int numeroEpisodio, int? aniListId, int? malIdEsperado, IReadOnlyList<PrecuelaAnime> precuelas)
    {
        public List<string> Titulos { get; } = titulos;
        public int NumeroEpisodio { get; } = numeroEpisodio;
        public int? AniListId { get; } = aniListId;
        public int? MalIdEsperado { get; } = malIdEsperado;
        public IReadOnlyList<PrecuelaAnime> Precuelas { get; } = precuelas;
        public int Probes { get; set; }
        public int ProbesSinParecido { get; set; }
        public HashSet<string> Probados { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Encolados { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Task<InfoMedia?>> Paginas { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Página del sitio candidata a ser el anime buscado, con lo que se sabe de ella antes de pedirla.</summary>
    private sealed record CandidatoMedia(string Slug, bool Plausible);

    /// <summary>
    /// Media-first con crawl de relations: cuando un media se prueba, sus relations (películas/secuelas
    /// de la franquicia) se insertan INMEDIATAMENTE después de él — así dragon-ball-z (rechazado por
    /// malId) lleva directo al movie-14 sin depender del orden del catálogo. Mientras se verifica un
    /// candidato ya se piden los siguientes (precarga).
    /// </summary>
    private async Task<List<AnimeAv1HtmlParser.EmbedServidor>> ProbarCandidatosAsync(
        BusquedaEnCurso b, List<CandidatoMedia> candidatos, CancellationToken ct)
    {
        Task<InfoMedia?> PedirMedia(string s)
        {
            if (!b.Paginas.TryGetValue(s, out var tarea))
            {
                tarea = ObtenerInfoMediaAsync(s, ct);
                b.Paginas[s] = tarea;
            }
            return tarea;
        }
        bool SeProbara(CandidatoMedia c) => c.Plausible || b.ProbesSinParecido < MaxProbesSinParecido;
        void PrecargarSiguientes(int desde)
        {
            int pedidas = 0;
            for (int j = desde; j < candidatos.Count && pedidas < MediaPrecargados && b.Probes + pedidas < MaxMediaProbes; j++)
            {
                if (b.Probados.Contains(candidatos[j].Slug) || !SeProbara(candidatos[j])) continue;
                PedirMedia(candidatos[j].Slug);
                pedidas++;
            }
        }

        for (int i = 0; i < candidatos.Count && b.Probes < MaxMediaProbes; i++)
        {
            if (ct.IsCancellationRequested) return [];
            var candidato = candidatos[i];
            // Las páginas sin ningún parecido con el anime (lo que el catálogo devuelve cuando no
            // encuentra nada) solo se revisan unas pocas: antes un anime que no está en el sitio
            // gastaba las 40 páginas del presupuesto en ellas.
            if (!SeProbara(candidato) || !b.Probados.Add(candidato.Slug)) continue;

            b.Probes++;
            if (!candidato.Plausible) b.ProbesSinParecido++;
            PrecargarSiguientes(i + 1);
            var media = await PedirMedia(candidato.Slug);
            if (media == null) continue;

            // Crawl inmediato: encolar las relations del media justo detrás de él
            // (el media principal de una franquicia las lista todas)
            if (b.Probes < MaxMediaProbes)
            {
                // Una relation que ya estaba más abajo en la lista (el catálogo también la devolvió) se
                // adelanta igual: el orden por nombre no sabe que "Dragon Ball Z" lleva a su película 14.
                var nuevos = AnimeAv1HtmlParser.ExtraerRelationsDelMedia(media.Html)
                    .Where(r => !b.Probados.Contains(r.Slug))
                    .Take(MaxMediaProbes - b.Probes)
                    .Select(r => new CandidatoMedia(r.Slug, Plausible: true))
                    .ToList();
                foreach (var r in nuevos)
                {
                    b.Encolados.Add(r.Slug);
                    int pendiente = candidatos.FindIndex(i + 1, c => c.Slug.Equals(r.Slug, StringComparison.OrdinalIgnoreCase));
                    if (pendiente >= 0) candidatos.RemoveAt(pendiente);
                }
                if (nuevos.Count > 0)
                {
                    candidatos.InsertRange(i + 1, nuevos);
                    PrecargarSiguientes(i + 1);
                }
            }

            int? desfase = await VerificarMediaAsync(media, b.MalIdEsperado, b.Titulos, b.Precuelas, ct);
            if (!desfase.HasValue) continue;

            int? objetivo = ResolverNumeroEpisodio(media, b.NumeroEpisodio + desfase.Value);
            if (!objetivo.HasValue)
            {
                AppLogger.Debug("AnimeAv1VideoSourceResolver", $"Media {media.Slug} es el anime, pero aún no tiene el episodio {b.NumeroEpisodio + desfase.Value}.");
                continue;
            }

            // Con desfase la página es la de la parte anterior: su MAL ID es el de esa parte.
            int? malIdPagina = desfase.Value > 0 ? media.MalId : b.MalIdEsperado;
            var embeds = await ObtenerEmbedsDeEpisodioAsync(media.Slug, objetivo.Value, malIdPagina, ct);
            if (embeds.Count > 0)
            {
                if (b.AniListId.HasValue) await RecordarMediaAsync(b.AniListId.Value, new MediaVerificado(media.Slug, desfase.Value > 0 ? media.MalId : b.MalIdEsperado ?? media.MalId));
                return embeds;
            }
        }

        return [];
    }

    /// <summary>Página del sitio ya verificada como la de un anime (slug + MAL ID para seguir verificando).</summary>
    private readonly record struct MediaVerificado(string Slug, int? MalId);

    /// <summary>
    /// AniListId → página del sitio ya verificada: descargar los 12 episodios de una temporada hace
    /// la búsqueda completa una vez en lugar de doce. Se guarda también en la base de datos para que
    /// el primer episodio tras reiniciar la app no la repita.
    /// </summary>
    private readonly ConcurrentDictionary<int, MediaVerificado> _mediaPorAniList = new();

    private async Task<MediaVerificado?> ObtenerMediaConocidoAsync(int aniListId)
    {
        if (_mediaPorAniList.TryGetValue(aniListId, out var enMemoria)) return enMemoria;
        if (_database == null) return null;

        try
        {
            var guardado = await _database.ObtenerMediaAnimeAv1Async(aniListId);
            if (guardado == null || string.IsNullOrWhiteSpace(guardado.Slug)) return null;
            var media = new MediaVerificado(guardado.Slug, guardado.MalId);
            _mediaPorAniList[aniListId] = media;
            return media;
        }
        catch (Exception ex)
        {
            AppLogger.Debug("AnimeAv1VideoSourceResolver", $"No se pudo leer la página guardada de {aniListId}: {ex.Message}");
            return null;
        }
    }

    private async Task RecordarMediaAsync(int aniListId, MediaVerificado media)
    {
        _mediaPorAniList[aniListId] = media;
        if (_database == null) return;

        try
        {
            await _database.GuardarMediaAnimeAv1Async(new Models.MediaAnimeAv1Verificado
            {
                AniListId = aniListId,
                Slug = media.Slug,
                MalId = media.MalId,
                VerificadoUtc = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            // No guardarla solo cuesta repetir la búsqueda tras reiniciar: nunca debe romper la descarga.
            AppLogger.Debug("AnimeAv1VideoSourceResolver", $"No se pudo guardar la página de {aniListId}: {ex.Message}");
        }
    }

    /// <summary>Páginas de candidatos que se piden por adelantado mientras se verifica el actual.</summary>
    private const int MediaPrecargados = 2;

    /// <summary>Búsquedas de catálogo a la vez: bastante más rápido que en serie sin acribillar al sitio.</summary>
    private const int BusquedasCatalogoSimultaneas = 4;

    /// <summary>Presupuesto total de páginas de anime revisadas en una búsqueda completa.</summary>
    private const int MaxMediaProbes = 40;

    /// <summary>Páginas sin ningún parecido de nombre que se revisan como mucho (por si el sitio usa un nombre muy distinto).</summary>
    private const int MaxProbesSinParecido = 6;

    /// <summary>Parecido de nombre (sin mirar temporada) por debajo del cual un candidato del catálogo no se parece al anime.</summary>
    private const double UmbralParecidoCandidato = 0.4;

    /// <summary>Cuánto vale una página de la misma serie pero otra temporada/parte frente a una de la misma parte.</summary>
    private const double PesoOtraParte = 0.9;

    private const int MaxSlugsDeducidos = 2;

    private const int MaxTerminosPrimeraTanda = 8;
    private const int MaxTerminosSegundaTanda = 10;

    private async Task<List<AnimeAv1HtmlParser.EmbedServidor>> ObtenerEmbedsDeMediaConocidoAsync(
        MediaVerificado conocido, int numeroEpisodio, List<string> titulosLista, int? aniListId, CancellationToken ct)
    {
        // Mismas comprobaciones que la búsqueda completa (la página es de este anime, el episodio figura
        // en su lista y su página declara el MAL ID esperado), pero con 2 peticiones en vez de decenas.
        var media = await ObtenerInfoMediaAsync(conocido.Slug, ct);
        if (media == null) return [];

        // Mismo veredicto que la búsqueda completa: MAL ID si ambos lo tienen; si no, parecido de nombres.
        // Sin esto, una página guardada sin MAL ID que el sitio reutilizara para otra serie descargaría
        // episodios de la serie equivocada sin ningún aviso.
        bool malIdComparable = conocido.MalId.HasValue && media.MalId.HasValue;
        var titulos = titulosLista;
        if (!malIdComparable)
        {
            // Los títulos alternativos de AniList (japonés, sinónimos) son los que pudieron aceptar la
            // página en la búsqueda completa: sin ellos este atajo fallaría siempre para ese anime.
            titulos = new List<string>(titulosLista);
            foreach (var t in await ObtenerTitulosExtraAsync(aniListId, ct))
            {
                if (!titulos.Contains(t, StringComparer.OrdinalIgnoreCase)) titulos.Add(t);
            }
        }
        var precuelas = await ObtenerPrecuelasAsync(aniListId, ct);
        if (await VerificarMediaAsync(media, conocido.MalId, titulos, [], ct) is null) return [];

        // Si la página guardada es la de una parte anterior (el sitio junta las partes), el episodio va desplazado.
        int desfase = DesfasePorPrecuela(precuelas, conocido.MalId) ?? 0;
        int? objetivo = ResolverNumeroEpisodio(media, numeroEpisodio + desfase);
        if (!objetivo.HasValue) return [];
        return await ObtenerEmbedsDeEpisodioAsync(conocido.Slug, objetivo.Value, conocido.MalId, ct);
    }

    private async Task<List<string>> ObtenerTitulosExtraAsync(int? aniListId, CancellationToken ct)
    {
        if (!aniListId.HasValue || _titulosDesdeAniList == null) return [];
        try
        {
            var extra = await _titulosDesdeAniList(aniListId.Value, ct);
            return extra?.Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? [];
        }
        catch (Exception ex)
        {
            AppLogger.Debug("AnimeAv1VideoSourceResolver", $"No se pudieron obtener títulos de AniList para {aniListId}: {ex.Message}");
            return [];
        }
    }

    private async Task<IReadOnlyList<PrecuelaAnime>> ObtenerPrecuelasAsync(int? aniListId, CancellationToken ct)
    {
        if (!aniListId.HasValue || _precuelas == null) return [];
        try
        {
            return await _precuelas(aniListId.Value, ct) ?? [];
        }
        catch (Exception ex)
        {
            AppLogger.Debug("AnimeAv1VideoSourceResolver", $"No se pudieron obtener las precuelas de {aniListId}: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Episodios que hay que sumar cuando la página del sitio es la de una parte ANTERIOR del anime:
    /// el sitio junta "2nd Season" y "2nd Season Part 2" en una sola página de 25 episodios, así que el
    /// episodio 3 de la parte 2 es su episodio 16. Null si ese MAL ID no es de ninguna precuela.
    /// </summary>
    internal static int? DesfasePorPrecuela(IReadOnlyList<PrecuelaAnime> precuelas, int? malIdPagina)
    {
        if (!malIdPagina.HasValue) return null;
        int suma = 0;
        foreach (var precuela in precuelas)
        {
            if (precuela.Episodios <= 0) return null; // sin el total de esa parte no se puede calcular
            suma += precuela.Episodios;
            if (precuela.MalId == malIdPagina) return suma;
        }
        return null;
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
    /// Candidatos de una tanda: las páginas que devuelve el catálogo para cada término (con su título)
    /// y, en la primera tanda, los slugs deducidos de los títulos. Se ordenan por parecido con el
    /// anime, temporada incluida: la página de "Mushoku Tensei III" antes que la de "II", y las de
    /// otros animes (lo que el catálogo devuelve cuando no encuentra nada) al final.
    /// </summary>
    private async Task<List<CandidatoMedia>> ObtenerCandidatosAsync(
        List<string> titulosLista, List<string> terminos, bool incluirGenerados, HashSet<string> vistos, CancellationToken ct)
    {
        using var limite = new SemaphoreSlim(BusquedasCatalogoSimultaneas);
        var resultados = await Task.WhenAll(terminos.Select(t => BuscarEnCatalogoAsync(t, limite, ct)));

        var brutos = new List<(string Slug, string Nombre, bool Generado)>();
        for (int i = 0; i < terminos.Count; i++)
        {
            int nuevos = 0;
            foreach (var r in resultados[i])
            {
                if (vistos.Add(r.Slug))
                {
                    brutos.Add((r.Slug, r.Titulo ?? r.Slug.Replace('-', ' '), false));
                    nuevos++;
                }
            }
            if (nuevos > 0)
            {
                AppLogger.Debug("AnimeAv1VideoSourceResolver", $"Búsqueda de catálogo '{terminos[i]}': {nuevos} páginas nuevas.");
            }
        }

        // Los títulos del sitio están en alfabeto latino: comparar con los coreanos o chinos solo mete
        // ruido ("Re:从零…" hacía que "Re:Monster" pareciera algo).
        var titulosLatinos = titulosLista.Where(t => FirmaTitulo.EsAlfabetoBuscable(t, incluirJapones: false)).ToList();
        if (titulosLatinos.Count == 0) titulosLatinos = titulosLista;

        if (incluirGenerados)
        {
            // Slug deducido del título, como los escribe el sitio ("Re:Zero … 2nd Season" →
            // "rezero-…-2nd-season"). Solo de los dos primeros títulos: el catálogo ya encuentra casi
            // todo, y las antiguas variaciones (-2, -ii, -2nd-season…) eran decenas de 404 seguros.
            foreach (var titulo in titulosLatinos.Take(MaxSlugsDeducidos))
            {
                string slug = SlugDeTitulo(titulo);
                if (slug.Length >= 3 && vistos.Add(slug)) brutos.Add((slug, slug.Replace('-', ' '), true));
            }
        }

        // Orden: la misma temporada/parte primero; luego otra parte de la misma serie (el sitio a veces
        // las junta en una página); lo que no se parece, al final. Antes se ordenaba primero por "misma
        // temporada" aunque el parecido fuera mínimo, y "Re:Monster" (33 %) quedaba por delante de
        // "Re:Zero 2nd Season" (100 %, otra parte): la página correcta caía fuera del presupuesto.
        return brutos
            .Select((c, indice) =>
            {
                var e = FirmaTitulo.Evaluar(titulosLatinos, [c.Nombre]);
                // Un slug deducido que el catálogo no devolvió seguramente no existe: detrás de las páginas reales igual de parecidas.
                double factor = c.Generado ? 0.95 : 1.0;
                double puntuacion = Math.Max(e.MismaTemporada, e.SinImportarTemporada * PesoOtraParte) * factor;
                return (c.Slug, puntuacion, Misma: e.MismaTemporada, indice,
                        Plausible: c.Generado || e.SinImportarTemporada >= UmbralParecidoCandidato);
            })
            .OrderByDescending(x => x.puntuacion)
            .ThenByDescending(x => x.Misma)
            .ThenBy(x => x.indice)
            .Select(x => new CandidatoMedia(x.Slug, x.Plausible))
            .ToList();
    }

    /// <summary>
    /// Slugs candidatos (compatibilidad): los deducidos de los títulos y los del catálogo para todos
    /// los términos, ordenados por parecido con el anime.
    /// </summary>
    public async Task<List<string>> ObtenerSlugsCandidatosAsync(
        List<string> titulosLista, int? malIdEsperado, CancellationToken ct = default)
    {
        var terminos = GenerarTerminosOrdenados(titulosLista).Select(t => t.Termino).ToList();
        var candidatos = await ObtenerCandidatosAsync(titulosLista, terminos, incluirGenerados: true,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), ct);
        return candidatos.Select(c => c.Slug).ToList();
    }

    /// <summary>Resultados del catálogo para un término (vacío si falla: nunca lanza).</summary>
    private async Task<List<AnimeAv1HtmlParser.ResultadoCatalogo>> BuscarEnCatalogoAsync(string termino, SemaphoreSlim limite, CancellationToken ct)
    {
        bool dentro = false;
        try
        {
            await limite.WaitAsync(ct);
            dentro = true;

            var html = await ObtenerHtmlAsync($"https://animeav1.com/catalogo?search={Uri.EscapeDataString(termino)}", ct);
            return html == null ? [] : AnimeAv1HtmlParser.ExtraerResultadosCatalogo(html);
        }
        catch (Exception ex)
        {
            AppLogger.Debug("AnimeAv1VideoSourceResolver", $"Error en búsqueda de catálogo para '{termino}': {ex.Message}");
            return [];
        }
        finally
        {
            if (dentro) limite.Release();
        }
    }

    /// <summary>
    /// Descarga una página del sitio. Un fallo pasajero (el sitio tarda, 5xx, 429, conexión cortada) se
    /// reintenta una vez: sin esto, un tropiezo justo en la página correcta daba el episodio por
    /// inexistente. Un 404 no se reintenta. Null si no se pudo.
    /// </summary>
    private async Task<string?> ObtenerHtmlAsync(string url, CancellationToken ct, string? referer = null)
    {
        for (int intento = 1; ; intento++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("User-Agent", UserAgent);
                if (referer != null) req.Headers.Add("Referer", referer);

                using var res = await _httpClient.SendAsync(req, ct);
                if (res.IsSuccessStatusCode) return await res.Content.ReadAsStringAsync(ct);

                int codigo = (int)res.StatusCode;
                bool pasajero = codigo is 408 or 429 or >= 500;
                if (!pasajero || intento >= IntentosPagina) return null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return null;
            }
            catch (SinConexionException)
            {
                return null; // sin internet: repetir no sirve (la descarga ya espera a que vuelva la red)
            }
            catch (Exception ex) when (intento < IntentosPagina && ex is HttpRequestException or TaskCanceledException or System.IO.IOException)
            {
                AppLogger.Debug("AnimeAv1VideoSourceResolver", $"Fallo pasajero en {url}: {ex.Message}. Reintentando.");
            }
            catch (Exception ex)
            {
                AppLogger.Debug("AnimeAv1VideoSourceResolver", $"Error obteniendo {url}: {ex.Message}");
                return null;
            }

            try { await Task.Delay(EsperaReintentoPagina, ct); }
            catch (OperationCanceledException) { return null; }
        }
    }

    private const int IntentosPagina = 2;
    private static readonly TimeSpan EsperaReintentoPagina = TimeSpan.FromMilliseconds(700);

    private readonly record struct TerminoConPrioridad(string Termino, int Prioridad);

    /// <summary>
    /// Términos de TODOS los títulos ordenados por especificidad (FUN-018): el título completo y sus
    /// partes principales primero, sin importar de qué título vengan; las colas genéricas de 2-3
    /// palabras (propensas a traer animes sin relación) al final.
    /// </summary>
    private static List<TerminoConPrioridad> GenerarTerminosOrdenados(IEnumerable<string> titulos)
    {
        var vistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return titulos
            .SelectMany(GenerarTerminosBusquedaConPrioridad)
            .Where(t => vistos.Add(t.Termino))
            .OrderBy(t => t.Prioridad)
            .ToList();
    }

    /// <summary>
    /// Términos de búsqueda de un título, etiquetados con su especificidad (0 = más fiable, mayor =
    /// más genérico). Solo títulos en alfabeto latino: el buscador del sitio no entiende japonés,
    /// tailandés ni ruso (devuelve su listado por defecto, que solo añade candidatos basura). Las
    /// partes se separan por ':' o " - " pero NO por un guion pegado: "Kusunoki-tei" daba el término
    /// "tei" y "Kaitaku-ki" el término "ki", que traían decenas de animes sin relación.
    /// </summary>
    private static List<TerminoConPrioridad> GenerarTerminosBusquedaConPrioridad(string titulo)
    {
        var resultado = new List<TerminoConPrioridad>();
        if (!FirmaTitulo.EsAlfabetoBuscable(titulo, incluirJapones: false)) return resultado;

        void Agregar(string termino, int prioridad)
        {
            termino = AnioEntreParentesisRegex().Replace(termino, " ").Trim().Trim('-', '–', ':', '~', '(', ')', '"', ' ');
            if (termino.Count(char.IsLetterOrDigit) < 4) return;
            if (!resultado.Any(r => r.Termino.Equals(termino, StringComparison.OrdinalIgnoreCase)))
            {
                resultado.Add(new TerminoConPrioridad(termino, prioridad));
            }
        }

        // 0: título completo — el más fiable
        Agregar(titulo, 0);

        var partes = SeparadorPartesRegex().Split(titulo).Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        if (partes.Length > 1)
        {
            // 1: subtítulo tras ':' — suele ser lo más distintivo de la franquicia ("Battle of Gods")
            Agregar(partes[1], 1);
            // 1: parte principal antes de ':'
            Agregar(partes[0], 1);
        }

        // 1: sin el marcador de temporada — el sitio puede escribirla distinto ("II" frente a "2nd Season")
        if (FirmaTitulo.Interpretar(titulo).FirstOrDefault() is { Explicita: true, Base: var baseSinTemporada })
        {
            Agregar(baseSinTemporada, 1);
        }

        var palabras = titulo.Split([' '], StringSplitOptions.RemoveEmptyEntries)
                             .Where(p => p.Length > 2)
                             .ToList();

        if (palabras.Count >= 2)
        {
            // 2: primeras 2-3 palabras
            Agregar(string.Join(" ", palabras.Take(3)), 2);
        }

        if (palabras.Count >= 4)
        {
            // 3: últimas 2-3 palabras — la más genérica y propensa a falsos positivos
            Agregar(string.Join(" ", palabras.TakeLast(3)), 3);
        }

        return resultado;
    }

    [GeneratedRegex(@"[:–~(]|\s-\s")]
    private static partial Regex SeparadorPartesRegex();

    // "(2021)" / "(2021" (el año que AniList añade a algunos títulos no está en los del sitio)
    [GeneratedRegex(@"\(\s*\d{4}\s*\)?")]
    private static partial Regex AnioEntreParentesisRegex();
    /// <summary>Info del media del sitio (malId, títulos, episodios reales, HTML crudo para relations).</summary>
    public sealed record InfoMedia(string Slug, int? MalId, string? Titulo, List<string> Alternativos, List<(int Id, int Numero)> Episodios, string Html);

    /// <summary>Descarga la página del media y parsea su información.</summary>
    public async Task<InfoMedia?> ObtenerInfoMediaAsync(string slug, CancellationToken ct = default)
    {
        string mediaUrl = $"https://animeav1.com/media/{slug}";
        if (!EsDominioPermitido(mediaUrl, "animeav1.com")) return null;

        var html = await ObtenerHtmlAsync(mediaUrl, ct);
        if (html == null) return null;

        var titulos = AnimeAv1HtmlParser.ExtraerTitulosDelMedia(html);
        return new InfoMedia(
            slug,
            AnimeAv1HtmlParser.ExtraerMalIdDelMedia(html),
            titulos?.Principal,
            titulos?.Alternativos ?? new List<string>(),
            AnimeAv1HtmlParser.ExtraerEpisodiosDelMedia(html),
            html);
    }

    /// <summary>Embeds de una página de episodio con verificación de malId.</summary>
    public async Task<List<AnimeAv1HtmlParser.EmbedServidor>> ObtenerEmbedsDeEpisodioAsync(
        string slug, int numero, int? malIdEsperado, CancellationToken ct = default)
    {
        return await ObtenerEmbedsDePaginaAsync($"https://animeav1.com/media/{slug}/{numero}", malIdEsperado, ct);
    }

    /// <summary>Umbral del parecido de nombres (0..1) para aceptar una página sin MAL ID comparable.</summary>
    private const double UmbralNombreMedia = 0.75;

    /// <summary>
    /// Veredicto de identidad en cascada (el sistema riguroso). Devuelve cuántos episodios hay que
    /// sumar al número pedido (0 casi siempre) o null si la página NO es este anime:
    /// 1. MAL ID exacto (ambos conocidos) → acepta.
    /// 2. MAL ID de una precuela directa → la página junta varias partes: acepta con desfase.
    /// 3. MAL ID distinto → rechaza.
    /// 4. Sin MAL ID comparable → NOMBRES: se rechaza si es la misma serie pero OTRA temporada/parte
    ///    (rapidfuzz no distingue "Mushoku Tensei II" de "III": 98 %); si no, acepta con el mejor
    ///    parecido entre rapidfuzz (daemon Python), el C# de siempre y el de nombres sin temporada.
    /// </summary>
    private async Task<int?> VerificarMediaAsync(
        InfoMedia media, int? malIdEsperado, List<string> titulosLista, IReadOnlyList<PrecuelaAnime> precuelas, CancellationToken ct)
    {
        if (malIdEsperado.HasValue && media.MalId == malIdEsperado) return 0;

        if (DesfasePorPrecuela(precuelas, media.MalId) is int desfase)
        {
            AppLogger.Info("AnimeAv1VideoSourceResolver",
                $"Media {media.Slug} es una parte anterior (malId {media.MalId}) que el sitio junta con esta: episodios desplazados {desfase}.");
            return desfase;
        }

        if (malIdEsperado.HasValue && media.MalId.HasValue)
        {
            AppLogger.Debug("AnimeAv1VideoSourceResolver",
                $"Media {media.Slug} rechazado: malId {media.MalId} != esperado {malIdEsperado} (anime con nombre parecido).");
            return null;
        }

        // Sin malId comparable → nombres (título + aka del sitio vs títulos de la app)
        var nombresMedia = new List<string>();
        if (!string.IsNullOrWhiteSpace(media.Titulo)) nombresMedia.Add(media.Titulo);
        nombresMedia.AddRange(media.Alternativos);
        if (nombresMedia.Count == 0) return null;

        var identidad = FirmaTitulo.Evaluar(titulosLista, nombresMedia);
        if (identidad.ConflictoTemporada())
        {
            AppLogger.Warn("AnimeAv1VideoSourceResolver",
                $"Media {media.Slug} rechazado: es la misma serie pero otra temporada/parte ('{media.Titulo}').");
            return null;
        }

        double? rapidfuzz = null;
        if (_similitudNombres != null)
        {
            try { rapidfuzz = await _similitudNombres(titulosLista, nombresMedia, ct); }
            catch (Exception ex) { AppLogger.Debug("AnimeAv1VideoSourceResolver", $"Fallo rapidfuzz para {media.Slug}: {ex.Message}"); }
        }
        double csharp = titulosLista.Max(t => TituloSimilaridad.MejorSimilitud(t, nombresMedia));
        double mejor = new[] { rapidfuzz ?? 0, csharp, identidad.MismaTemporada }.Max();
        string detalle = $"rapidfuzz {(rapidfuzz.HasValue ? rapidfuzz.Value.ToString("P0") : "n/d")}, C# {csharp:P0}, con temporada {identidad.MismaTemporada:P0}";

        if (mejor < UmbralNombreMedia)
        {
            AppLogger.Debug("AnimeAv1VideoSourceResolver", $"Media {media.Slug} rechazado por nombre ({detalle}).");
            return null;
        }
        AppLogger.Info("AnimeAv1VideoSourceResolver", $"Media {media.Slug} aceptado por nombre ({detalle}).");
        return 0;
    }

    /// <summary>
    /// Número de episodio real del sitio. Coincidencia exacta; si no, y la lista del sitio NO empieza en
    /// 1, el episodio N es el N-ésimo de la lista: el sitio numera algunas entregas por su posición en la
    /// franquicia (la película "Dragon Ball Z: Kami to Kami" es su episodio 14) o sigue la numeración de
    /// la parte anterior (13–24). Si la lista empieza en 1 no se adivina nada: en una serie recién
    /// estrenada con solo el episodio 1 publicado, pedir el 2 descargaba el 1 guardado como "Episodio 02".
    /// </summary>
    internal static int? ResolverNumeroEpisodio(InfoMedia media, int numeroSolicitado)
    {
        if (media.Episodios.Count == 0) return null;
        if (media.Episodios.Any(e => e.Numero == numeroSolicitado)) return numeroSolicitado;

        var numeros = media.Episodios.Select(e => e.Numero).Distinct().OrderBy(n => n).ToList();
        if (numeros[0] > 1 && numeroSolicitado >= 1 && numeroSolicitado <= numeros.Count) return numeros[numeroSolicitado - 1];
        return null;
    }

    /// <summary>
    /// Descarga la página del episodio, verifica el MAL ID (si se espera uno) y
    /// extrae sus embeds filtrando los hosts que no están en la allowlist de
    /// servidores (seguridad INT-01/SEC-16).
    /// </summary>
    private async Task<List<AnimeAv1HtmlParser.EmbedServidor>> ObtenerEmbedsDePaginaAsync(string pageUrl, int? malIdEsperado, CancellationToken cancellationToken)
    {
        if (!EsDominioPermitido(pageUrl, "animeav1.com")) return [];

        var html = await ObtenerHtmlAsync(pageUrl, cancellationToken);
        if (html == null) return [];

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
            var html = await ObtenerHtmlAsync(pageUrl, cancellationToken);
            if (html == null) return null;

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
        else if (EsDominioPermitido(pageUrl, "mp4upload.com"))
        {
            return await ExtractFromMp4UploadAsync(pageUrl, cancellationToken);
        }

        return null;
    }

    private async Task<string?> ExtractFromMp4UploadAsync(string embedUrl, CancellationToken cancellationToken)
    {
        if (!EsDominioPermitido(embedUrl, "mp4upload.com")) return null;

        var html = await ObtenerHtmlAsync(embedUrl, cancellationToken, referer: "https://animeav1.com/");
        if (html == null) return null;

        // INT-01: parseo delegado al contrato tipado (testeable con fixtures).
        // Hardening: la URL extraída del HTML de terceros solo se acepta si es
        // https y pertenece a mp4upload.com (un proveedor comprometido no puede
        // redirigir la descarga a un servidor arbitrario).
        var url = AnimeAv1HtmlParser.ExtraerVideoDirecto(html);
        return Core.UrlSeguridad.EsUrlVideoPermitida(url) ? url : null;
    }

    /// <summary>Slug al estilo del sitio: minúsculas sin tildes ni signos, espacios a guiones ("Re:Zero 2nd Season" → "rezero-2nd-season").</summary>
    internal static string SlugDeTitulo(string titulo)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in titulo.Normalize(System.Text.NormalizationForm.FormD).ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(c);
            else if (c is ' ' or '-' or '_') sb.Append('-');
        }
        return Regex.Replace(sb.ToString(), "-{2,}", "-").Trim('-');
    }

    /// <summary>Términos de búsqueda de un título para el catálogo, del más fiable al más genérico (público para testeo).</summary>
    public static List<string> GenerarTerminosBusqueda(string titulo)
        => GenerarTerminosBusquedaConPrioridad(titulo).OrderBy(t => t.Prioridad).Select(t => t.Termino).ToList();
}
