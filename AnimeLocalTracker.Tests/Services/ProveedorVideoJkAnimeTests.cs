using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Core;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Python;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// JKAnime como segundo sitio: busca el anime por nombre (su página no trae MAL ID), lee los servidores del episodio
/// (URL del embed en base64) y los resuelve con los mismos servidores que AnimeAV1. Sin red real: servidor falso.
/// </summary>
public class ProveedorVideoJkAnimeTests
{
    private static string B64(string url) => Convert.ToBase64String(Encoding.UTF8.GetBytes(url + "\n"));

    // Con el espacio en blanco del sitio real: saltos de línea y sangría, incluso entre "<a" y "href".
    private static string Item(string slug, string titulo, string tipo = "Serie") => $$"""
        <div class="col-lg-2 col-md-6 col-sm-6" data-g="MjgyMw==">
            <div class="anime__item">
                <a
                  href="https://jkanime.net/{{slug}}/"><div class="g-0 anime__item__pic set-bg" data-setbg="https://cdn.jkdesa.com/{{slug}}.jpg">
                </div></a>
                <div class="anime__item__text"><ul><li>Concluido</li>
                    <li class="anime">{{tipo}}</li>
                </ul>
                    <h5><a
                       href="https://jkanime.net/{{slug}}/">
                      {{titulo}}
                     </a></h5>
                </div>
            </div>
        </div>
        """;

    private static string Busqueda(params (string Slug, string Titulo)[] resultados) =>
        $"""<html><div class="row page_directorio"> {string.Join(" ", resultados.Select(r => Item(r.Slug, r.Titulo)))} </div></html>""";

    private static string Episodio(int lang, params (string Servidor, string Url)[] servidores)
    {
        var json = string.Join(",", servidores.Select(s => $$"""{"slug":"x{{s.Servidor}}","server":"{{s.Servidor}}","lang":{{lang}},"size":"398 MB","append":0,"remote":"{{B64(s.Url)}}"}"""));
        return $"<html><title>Episodio</title><script>var video = []; var servers = [{json}];\nvar tabs = 1;</script></html>";
    }

    private const string Mp4Embed = "https://www.mp4upload.com/embed-iaswjnh6jjo8.html";
    private const string PlayerMp4 = """<html><script>var c = { src: "https://cdn.mp4upload.com/iaswjnh6jjo8/video.mp4" };</script></html>""";
    private const string SlugSanten = "tokyo-revengers-santen-sensou-hen";

    private static readonly (string, string)[] TresServidores =
    [
        ("Mediafire", "https://mediafire.com/file/4f7fyuv9kcrn0em/"),
        ("Mega", "https://mega.nz/embed/hulg0IqR#pvlurD-G3zofhzrXRNqRqymob0eO3rMnch9IYW7rBAA"),
        ("VOE", "https://voe.sx/e/zsvukgb6dmko"),
        ("Mp4upload", Mp4Embed),
    ];

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<string> Peticiones { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Peticiones.Add($"{request.Method} {request.RequestUri!.AbsoluteUri}");
            return Task.FromResult(responder(request));
        }
    }

    private static HttpResponseMessage Ok(string cuerpo) => new(HttpStatusCode.OK) { Content = new StringContent(cuerpo) };

    private static (ProveedorVideoJkAnime Proveedor, Mock<IPythonBridgeService> Bridge, StubHandler Http) Crear(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var http = new StubHandler(responder);
        var cliente = new HttpClient(http);
        var bridge = new Mock<IPythonBridgeService>();
        bridge.Setup(b => b.IsAvailableAsync()).ReturnsAsync(true);
        return (new ProveedorVideoJkAnime(bridge.Object, new AnimeAv1VideoSourceResolver(cliente), new JkAnimeClient(cliente)), bridge, http);
    }

    /// <summary>El sitio: busca "tokyo revengers" (3 series), cada episodio publicado con sus servidores y el reproductor de MP4Upload.</summary>
    private static HttpResponseMessage Sitio(HttpRequestMessage req, Func<string, HttpResponseMessage?>? episodio = null)
    {
        string url = req.RequestUri!.AbsoluteUri;
        if (req.RequestUri.Host.Contains("mp4upload.com")) return Ok(PlayerMp4);
        if (url.Contains("/buscar/"))
            return Ok(Busqueda(("tokyo-revengers", "Tokyo Revengers"), (SlugSanten, "Tokyo Revengers: Santen Sensou-hen"), ("tokyo-revengers-seiya-kessen-hen", "Tokyo Revengers: Seiya Kessen-hen")));
        if (episodio?.Invoke(url) is { } personalizada) return personalizada;
        if (url.StartsWith($"https://jkanime.net/{SlugSanten}/1/")) return Ok(Episodio(1, TresServidores));
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static readonly string[] Titulos = ["Tokyo Revengers: Santen Sensou-hen"];

    // === Lectura de la búsqueda ===

    [Fact]
    public void ExtraerResultadosBusqueda_LeeSlugTituloYTipo()
    {
        var resultados = JkAnimeHtmlParser.ExtraerResultadosBusqueda(Busqueda(("tokyo-revengers", "Tokyo Revengers"), (SlugSanten, "Tokyo Revengers: Santen Sensou-hen")));

        resultados.Select(r => r.Slug).Should().Equal("tokyo-revengers", SlugSanten);
        resultados[1].Titulo.Should().Be("Tokyo Revengers: Santen Sensou-hen");
        resultados[0].Tipo.Should().Be("Serie");
    }

    [Fact]
    public void ExtraerResultadosBusqueda_DecodificaEntidadesHtmlYNoRepiteSlugs()
    {
        var html = Busqueda(("one-piece", "One Piece"), ("grand-blue", "Grand &amp; Blue&#039;s"), ("one-piece", "One Piece"));

        var resultados = JkAnimeHtmlParser.ExtraerResultadosBusqueda(html);

        resultados.Should().HaveCount(2);
        resultados[1].Titulo.Should().Be("Grand & Blue's");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<html>sin resultados</html>")]
    public void ExtraerResultadosBusqueda_SinResultados_DevuelveVacio(string? html) =>
        JkAnimeHtmlParser.ExtraerResultadosBusqueda(html).Should().BeEmpty();

    // === Lectura de los servidores del episodio ===

    [Fact]
    public void ExtraerServidores_DecodificaLaUrlDelEmbedYNormalizaLosNombres()
    {
        var servidores = JkAnimeHtmlParser.ExtraerServidores(Episodio(1, TresServidores));

        servidores.Select(s => s.Server).Should().Equal("Mediafire", "Mega", "Voe", "MP4Upload");
        servidores.Single(s => s.Server == "Voe").Url.Should().Be("https://voe.sx/e/zsvukgb6dmko", "sin el salto de línea final del base64");
        servidores.Single(s => s.Server == "MP4Upload").Url.Should().Be(Mp4Embed);
    }

    [Theory]
    [InlineData(1, "SUB")]
    [InlineData(3, "DUB")]
    [InlineData(2, "LANG2")]
    public void ExtraerServidores_TraduceElIdiomaDelSitio(int lang, string esperado) =>
        JkAnimeHtmlParser.ExtraerServidores(Episodio(lang, ("VOE", "https://voe.sx/e/abc"))).Single().Audio.Should().Be(esperado);

    [Fact]
    public void ExtraerServidores_IgnoraLosQueNoTienenUrlHttpsValida()
    {
        string html = """<script>var servers = [{"server":"A","lang":1,"remote":"%%%no-es-base64"},{"server":"B","lang":1,"remote":"aHR0cDovL3ZvZS5zeC9lL2FiYw=="},{"server":"VOE","lang":1,"remote":"aHR0cHM6Ly92b2Uuc3gvZS9hYmM="}];</script>""";

        JkAnimeHtmlParser.ExtraerServidores(html).Select(s => s.Server).Should().Equal("Voe");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("<html>sin servidores</html>")]
    [InlineData("<script>var servers = [ esto no es json ];</script>")]
    public void ExtraerServidores_SinListaValida_DevuelveVacio(string? html) =>
        JkAnimeHtmlParser.ExtraerServidores(html).Should().BeEmpty();

    // === Cliente ===

    [Fact]
    public async Task ElClienteBuscaEnLaRutaDelBuscadorConElTerminoEscapado()
    {
        var http = new StubHandler(_ => Ok(Busqueda(("tokyo-revengers", "Tokyo Revengers"))));

        var r = await new JkAnimeClient(new HttpClient(http)).BuscarAsync("Tokyo Revengers", CancellationToken.None);

        http.Peticiones.Should().Equal("GET https://jkanime.net/buscar/Tokyo%20Revengers/");
        r.Should().ContainSingle();
    }

    [Fact]
    public async Task ElClienteDevuelveNuloSiElEpisodioNoExiste()
    {
        var http = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        (await new JkAnimeClient(new HttpClient(http)).ObtenerServidoresAsync(SlugSanten, 99, CancellationToken.None)).Should().BeNull();
        http.Peticiones.Should().HaveCount(1, "un 404 no se reintenta");
    }

    [Fact]
    public async Task ElClienteReintentaUnaVezUnFalloPasajero()
    {
        int llamadas = 0;
        var http = new StubHandler(_ => ++llamadas == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Ok(Episodio(1, TresServidores)));

        var r = await new JkAnimeClient(new HttpClient(http)).ObtenerServidoresAsync(SlugSanten, 1, CancellationToken.None);

        r.Should().NotBeNullOrEmpty();
        llamadas.Should().Be(2);
    }

    [Theory]
    [InlineData("../admin")]
    [InlineData("a/b")]
    [InlineData("")]
    public async Task ElClienteRechazaSlugsConCaracteresRaros(string slug)
    {
        var http = new StubHandler(_ => Ok("x"));

        (await new JkAnimeClient(new HttpClient(http)).ObtenerServidoresAsync(slug, 1, CancellationToken.None)).Should().BeNull();
        http.Peticiones.Should().BeEmpty();
    }

    // === Proveedor ===

    [Fact]
    public async Task ResuelveElEpisodioConMp4UploadYLoLlamaJkAnime()
    {
        var (proveedor, bridge, _) = Crear(req => Sitio(req));

        var url = await proveedor.BuscarUrlEpisodioAsync(Titulos, 1);

        proveedor.Nombre.Should().Be("JKAnime");
        url.Should().Be("https://cdn.mp4upload.com/iaswjnh6jjo8/video.mp4");
        bridge.Verify(b => b.ExecuteCommandOneShotAsync<object, ProveedorVideoAnimeAv1.StreamResult>(
            It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ElegirLaPaginaDeLaTemporadaExacta_NoLaDeLaSerieDeNombreParecido()
    {
        var (proveedor, _, http) = Crear(req => Sitio(req));

        await proveedor.BuscarUrlEpisodioAsync(Titulos, 1);

        http.Peticiones.Should().Contain($"GET https://jkanime.net/{SlugSanten}/1/");
        http.Peticiones.Should().NotContain(p => p.Contains("/tokyo-revengers/1/") || p.Contains("seiya-kessen-hen/1/"));
    }

    [Fact]
    public async Task SiElEpisodioAunNoEstaPublicado_NoSeCaeALaPaginaDeOtraSerie()
    {
        // El episodio 5 de la temporada buscada todavía no existe (404): NO debe bajarse el episodio 5 de "Tokyo Revengers" a secas.
        var (proveedor, _, http) = Crear(req => Sitio(req));

        var url = await proveedor.BuscarUrlEpisodioAsync(Titulos, 5);

        url.Should().BeNull();
        http.Peticiones.Should().NotContain(p => p.Contains("/tokyo-revengers/5/"));
    }

    [Fact]
    public async Task SiNingunResultadoSeParece_DevuelveNuloSinPedirEpisodios()
    {
        var (proveedor, _, http) = Crear(req => req.RequestUri!.AbsoluteUri.Contains("/buscar/")
            ? Ok(Busqueda(("naruto", "Naruto"), ("bleach", "Bleach")))
            : new HttpResponseMessage(HttpStatusCode.NotFound));

        (await proveedor.BuscarUrlEpisodioAsync(Titulos, 1)).Should().BeNull();
        http.Peticiones.Should().OnlyContain(p => p.Contains("/buscar/"));
    }

    [Fact]
    public async Task ConOtraTemporadaDelMismoAnime_NoLaConfunde()
    {
        // Piden "Grand Blue Season 3"; el sitio devuelve la 1ª, 2ª y 3ª: debe ir a la 3ª.
        var (proveedor, _, http) = Crear(req =>
        {
            string url = req.RequestUri!.AbsoluteUri;
            if (req.RequestUri.Host.Contains("mp4upload.com")) return Ok(PlayerMp4);
            if (url.Contains("/buscar/")) return Ok(Busqueda(("grand-blue", "Grand Blue"), ("grand-blue-season-2", "Grand Blue Season 2"), ("grand-blue-season-3", "Grand Blue Season 3")));
            if (url.Contains("/grand-blue-season-3/9/")) return Ok(Episodio(1, TresServidores));
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var resultado = await proveedor.BuscarUrlEpisodioAsync(["Grand Blue Season 3"], 9);

        resultado.Should().NotBeNull();
        http.Peticiones.Should().NotContain(p => p.Contains("/grand-blue/9/") || p.Contains("/grand-blue-season-2/9/"));
    }

    [Fact]
    public async Task RecuerdaLaPaginaDeCadaAnime_ElSegundoEpisodioNoVuelveABuscar()
    {
        var (proveedor, _, http) = Crear(req => Sitio(req, url => url.StartsWith($"https://jkanime.net/{SlugSanten}/2/") ? Ok(Episodio(1, TresServidores)) : null));

        await proveedor.BuscarUrlEpisodioAsync(Titulos, 1, aniListId: 178083);
        await proveedor.BuscarUrlEpisodioAsync(Titulos, 2, aniListId: 178083);

        http.Peticiones.Count(p => p.Contains("/buscar/")).Should().Be(1);
        http.Peticiones.Should().Contain($"GET https://jkanime.net/{SlugSanten}/2/");
    }

    [Fact]
    public async Task SiMp4UploadNoResuelve_SigueConMegaYLaUrlLlevaLaClaveDelEnlaceDeJkAnime()
    {
        var (proveedor, _, _) = Crear(req =>
        {
            string host = req.RequestUri!.Host;
            if (host.Contains("mp4upload.com")) return Ok("<html>sin player</html>");
            if (host.StartsWith("g.api.mega")) return Ok("""[{"s":417000000,"g":"https://gfs9.userstorage.mega.co.nz/dl/token"}]""");
            if (host.EndsWith("userstorage.mega.co.nz")) return new HttpResponseMessage(HttpStatusCode.PartialContent);
            return Sitio(req);
        });

        var url = await proveedor.BuscarUrlEpisodioAsync(Titulos, 1, servidorPreferido: "Mega");

        url.Should().StartWith("https://gfs9.userstorage.mega.co.nz/dl/token#mega=");
        MegaTransferIt.ClaveDeUrl(url).Should().Equal(MegaTransferIt.ParsearEnlaceMega("https://mega.nz/file/hulg0IqR#pvlurD-G3zofhzrXRNqRqymob0eO3rMnch9IYW7rBAA")!.Value.Clave);
    }

    [Fact]
    public async Task ConElOrdenDeServidoresDelUsuario_VoeVaPrimeroYSeResuelveConElDaemon()
    {
        var (proveedor, bridge, _) = Crear(req => Sitio(req));
        bridge.Setup(b => b.ExecuteCommandOneShotAsync<object, ProveedorVideoAnimeAv1.StreamResult>(
                "resolve-stream", It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProveedorVideoAnimeAv1.StreamResult { Success = true, DirectUrl = "https://cdn.voe.example.com/video.mp4" });

        var url = await proveedor.BuscarUrlEpisodioAsync(Titulos, 1, servidorPreferido: "Voe");

        url.Should().Be("https://cdn.voe.example.com/video.mp4");
    }

    [Fact]
    public async Task IgnoraLosServidoresQueLaAppNoSabeResolver()
    {
        var (proveedor, bridge, _) = Crear(req => Sitio(req, url => url.Contains("/1/")
            ? Ok(Episodio(1, ("Mixdrop", "https://mixdrop.top/e/x"), ("Filemoon", "https://bysekoze.com/e/abc"), ("1Fichier", "https://1fichier.com/?x"), ("Streamtape", "https://streamtape.com/e/x/")))
            : null));

        (await proveedor.BuscarUrlEpisodioAsync(Titulos, 1)).Should().BeNull();
        bridge.Verify(b => b.ExecuteCommandOneShotAsync<object, ProveedorVideoAnimeAv1.StreamResult>(
            It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SinTitulosUtilizables_DevuelveNulo()
    {
        var (proveedor, _, http) = Crear(req => Sitio(req));

        (await proveedor.BuscarUrlEpisodioAsync(["東京リベンジャーズ"], 1)).Should().BeNull();
        http.Peticiones.Should().BeEmpty();
    }

    [Fact]
    public async Task GetVideoUrlAsync_NoLoSoporta() =>
        (await Crear(req => Sitio(req)).Proveedor.GetVideoUrlAsync("https://jkanime.net/x/1/")).Should().BeNull();
}
