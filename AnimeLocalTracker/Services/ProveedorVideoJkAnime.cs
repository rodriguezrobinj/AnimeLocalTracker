using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Core;
using AnimeLocalTracker.Services.Python;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Segundo sitio de video (jkanime.net, ver docs/investigacion-jkanime.md). Busca el anime POR NOMBRE (su página no trae MAL ID),
/// lee los servidores del episodio y los resuelve con el mismo <see cref="ResolvedorServidoresVideo"/> que AnimeAV1: MP4Upload, Mega
/// y Voe. Sus archivos son H.264 1080p (el doble de grandes que el AV1 de AnimeAV1, pero que cualquier tarjeta gráfica decodifica).
/// </summary>
public class ProveedorVideoJkAnime : IProveedorVideo
{
    /// <summary>Parecido mínimo del título de la página con los del anime (como el umbral de nombres de AnimeAV1).</summary>
    private const double UmbralNombre = 0.75;

    /// <summary>Cuánto puede quedar por debajo de la mejor coincidencia otra página para probarse igual (duplicados del sitio).</summary>
    private const double EmpateMaximo = 0.02;

    private const int MaxTerminos = 6;
    private const int MaxCandidatos = 2;
    private const string Referer = "https://jkanime.net/";

    private readonly JkAnimeClient _cliente;
    private readonly ResolvedorServidoresVideo _servidores;
    private readonly ConcurrentDictionary<int, string> _paginaPorAniList = new();
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _busquedasPorAnime = new();

    public string Nombre => "JKAnime";

    public ProveedorVideoJkAnime(IPythonBridgeService pythonBridge, AnimeAv1VideoSourceResolver resolverHttp, JkAnimeClient cliente)
    {
        _cliente = cliente;
        _servidores = new ResolvedorServidoresVideo(pythonBridge, resolverHttp);
    }

    public async Task<string?> BuscarUrlEpisodioAsync(IEnumerable<string> titulos, int numeroEpisodio, int? aniListId = null, string? audioPreferido = null, string? servidorPreferido = null, CancellationToken ct = default)
    {
        // El buscador del sitio no entiende japonés ni otros alfabetos: solo se busca con títulos en latino.
        var latinos = titulos.Where(t => !string.IsNullOrWhiteSpace(t) && FirmaTitulo.EsAlfabetoBuscable(t, incluirJapones: false))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (latinos.Count == 0) return null;

        // Atajo: la página que ya se verificó para este anime (p. ej. al bajar el episodio anterior de la misma temporada).
        string? yaProbada = null;
        if (aniListId.HasValue && _paginaPorAniList.TryGetValue(aniListId.Value, out var conocida))
        {
            var url = await ProbarPaginaAsync(conocida, numeroEpisodio, audioPreferido, servidorPreferido, ct);
            if (url != null) return url;
            yaProbada = conocida;
        }

        if (!aniListId.HasValue) return await BuscarYResolverAsync(latinos, numeroEpisodio, null, yaProbada, audioPreferido, servidorPreferido, ct);

        // Una sola búsqueda a la vez por anime: al pedir varios episodios de golpe, todos esperan y usan la página que encuentre el primero.
        var candado = _busquedasPorAnime.GetOrAdd(aniListId.Value, _ => new SemaphoreSlim(1, 1));
        try { await candado.WaitAsync(ct); }
        catch (OperationCanceledException) { return null; }
        try
        {
            if (_paginaPorAniList.TryGetValue(aniListId.Value, out var encontradaPorOtro) && !string.Equals(encontradaPorOtro, yaProbada, StringComparison.OrdinalIgnoreCase))
            {
                var url = await ProbarPaginaAsync(encontradaPorOtro, numeroEpisodio, audioPreferido, servidorPreferido, ct);
                if (url != null) return url;
            }
            return await BuscarYResolverAsync(latinos, numeroEpisodio, aniListId, yaProbada, audioPreferido, servidorPreferido, ct);
        }
        finally
        {
            candado.Release();
        }
    }

    /// <summary>Este proveedor no resuelve páginas sueltas: solo episodios de la biblioteca.</summary>
    public Task<string?> GetVideoUrlAsync(string pageUrl, CancellationToken ct = default) => Task.FromResult<string?>(null);

    private async Task<string?> BuscarYResolverAsync(List<string> titulos, int numeroEpisodio, int? aniListId, string? yaProbada, string? audioPreferido, string? servidorPreferido, CancellationToken ct)
    {
        foreach (var slug in await BuscarPaginasAsync(titulos, ct))
        {
            if (string.Equals(slug, yaProbada, StringComparison.OrdinalIgnoreCase)) continue;

            var url = await ProbarPaginaAsync(slug, numeroEpisodio, audioPreferido, servidorPreferido, ct);
            if (url == null) continue;

            if (aniListId.HasValue) _paginaPorAniList[aniListId.Value] = slug;
            return url;
        }

        AppLogger.Info("ProveedorVideoJkAnime", $"'{titulos[0]}' ep {numeroEpisodio}: no está en JKAnime.");
        return null;
    }

    /// <summary>
    /// Páginas del sitio que son este anime, la mejor primero. Se busca con cada título hasta dar con una coincidencia fiable (casi siempre
    /// basta con el título completo). Solo se prueban las que empatan con la mejor: si el episodio pedido aún no existe en la temporada
    /// correcta, NO se baja el de otra serie de nombre parecido.
    /// </summary>
    private async Task<List<string>> BuscarPaginasAsync(List<string> titulos, CancellationToken ct)
    {
        var terminos = titulos.SelectMany(AnimeAv1VideoSourceResolver.GenerarTerminosBusqueda)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxTerminos).ToList();
        var vistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var puntuadas = new List<(string Slug, double Puntuacion)>();

        foreach (var termino in terminos)
        {
            if (ct.IsCancellationRequested) break;

            foreach (var r in await _cliente.BuscarAsync(termino, ct))
            {
                if (!vistos.Add(r.Slug)) continue;
                if (Puntuar(titulos, r.Titulo) is double p) puntuadas.Add((r.Slug, p));
            }
            if (puntuadas.Count > 0) break;
        }

        if (puntuadas.Count == 0) return [];
        double mejor = puntuadas.Max(p => p.Puntuacion);
        return puntuadas.Where(p => p.Puntuacion >= mejor - EmpateMaximo)
            .OrderByDescending(p => p.Puntuacion).Select(p => p.Slug).Take(MaxCandidatos).ToList();
    }

    /// <summary>Parecido (0..1) del título de la página con los del anime, o null si no es este anime (otra temporada, otro nombre).</summary>
    private static double? Puntuar(List<string> titulos, string tituloPagina)
    {
        var identidad = FirmaTitulo.Evaluar(titulos, [tituloPagina]);
        if (identidad.ConflictoTemporada()) return null; // la misma serie pero otra temporada/parte

        double csharp = titulos.Max(t => TituloSimilaridad.MejorSimilitud(t, [tituloPagina]));
        double mejor = Math.Max(identidad.MismaTemporada, csharp);
        return mejor >= UmbralNombre ? mejor : null;
    }

    private async Task<string?> ProbarPaginaAsync(string slug, int numeroEpisodio, string? audioPreferido, string? servidorPreferido, CancellationToken ct)
    {
        var servidores = await _cliente.ObtenerServidoresAsync(slug, numeroEpisodio, ct);
        if (servidores == null || servidores.Count == 0) return null; // el episodio no existe (aún) en esta página

        var ordenados = AnimeAv1HtmlParser.OrdenarEmbedsPorPreferencia(servidores.Where(s => UrlSeguridad.EsEmbedDeServidorPermitido(s.Server, s.Url)), audioPreferido, servidorPreferido);
        return ordenados.Count == 0 ? null : await _servidores.ResolverPrimeroAsync(ordenados, Referer, ct);
    }
}
