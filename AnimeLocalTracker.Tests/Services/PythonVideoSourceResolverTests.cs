using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Python;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// Orquestación multi-servidor (Fase 1): la página del episodio publica embeds de
/// HLS/UPNShare/Voe/Byse/Mega/MP4Upload. Se prueban por preferencia: MP4Upload con
/// el extractor C# y el resto vía yt-dlp (daemon mockeado, sin red real).
/// </summary>
public class PythonVideoSourceResolverTests
{
    private const string FixturePaginaEpisodio = """
        <html><body>
        <script>{__sveltekit_1p4gm49 = {data: [{type:"data",data:{episode:{number:9},
        embeds:{SUB:[
          {server:"HLS",url:"https://player.zilla-networks.com/play/02a6a12077b53fa28f4b0e4d08ab3e08"},
          {server:"UPNShare",url:"https://animeav1.uns.bio/#xpzikv"},
          {server:"Voe",url:"https://voe.sx/e/xkwrsnscgvze"},
          {server:"Byse",url:"https://byselapuix.com/e/ollcejudwkem"},
          {server:"Mega",url:"https://mega.nz/embed/ntxzURRJ#7CCEoGhFf0QplFo-W-toyUq8a6B5JR3NQu-m9EJtBzE"},
          {server:"MP4Upload",url:"https://www.mp4upload.com/embed-r0xdfbvme2yy.html"}
        ]}}]}}</script>
        </body></html>
        """;

    private const string FixturePlayerMp4Upload = """
        <html><script>var config = { src: "https://cdn.mp4upload.com/r0xdfbvme2yy/720p/video.mp4", type: "video/mp4" };</script></html>
        """;

    private static readonly string[] Titulos = { "Grand Blue Season 3" };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }

    private static (PythonVideoSourceResolver Resolver, Mock<IPythonBridgeService> Bridge) Crear(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StubHandler(responder);
        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(handler));

        var bridge = new Mock<IPythonBridgeService>();
        bridge.Setup(b => b.IsAvailableAsync()).ReturnsAsync(true);

        return (new PythonVideoSourceResolver(bridge.Object, httpFactory.Object), bridge);
    }

    private static HttpResponseMessage Ok(string html) =>
        new(HttpStatusCode.OK) { Content = new StringContent(html) };

    [Fact]
    public async Task BuscarUrlEpisodioAsync_Mp4UploadDisponible_DeberiaUsarElExtractorDirecto()
    {
        // Arrange: la página del episodio responde con los 6 embeds y el player de
        // mp4upload con src directo. El daemon NO debe intervenir.
        var (resolver, bridge) = Crear(req =>
        {
            if (req.RequestUri!.Host.Contains("animeav1.com")) return Ok(FixturePaginaEpisodio);
            if (req.RequestUri!.Host.Contains("mp4upload.com")) return Ok(FixturePlayerMp4Upload);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        // Act
        var url = await resolver.BuscarUrlEpisodioAsync(Titulos, 9);

        // Assert: URL directa del CDN de mp4upload, validada por la política
        url.Should().Be("https://cdn.mp4upload.com/r0xdfbvme2yy/720p/video.mp4");
        bridge.Verify(b => b.ExecuteCommandAsync<object, PythonVideoSourceResolver.StreamResult>(
            It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task BuscarUrlEpisodioAsync_Mp4UploadRoto_DeberiaCaerAlSiguienteServidorViaYtDlp()
    {
        // Arrange: mp4upload sin src (extracción falla) → Voe se resuelve con el daemon
        var (resolver, bridge) = Crear(req =>
        {
            if (req.RequestUri!.Host.Contains("animeav1.com")) return Ok(FixturePaginaEpisodio);
            if (req.RequestUri!.Host.Contains("mp4upload.com")) return Ok("<html>sin player</html>");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        bridge.Setup(b => b.ExecuteCommandAsync<object, PythonVideoSourceResolver.StreamResult>(
                "resolve-stream", It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PythonVideoSourceResolver.StreamResult { Success = true, DirectUrl = "https://cdn.voe.example.com/video.mp4" });

        // Act
        var url = await resolver.BuscarUrlEpisodioAsync(Titulos, 9);

        // Assert: resuelto por yt-dlp con el servidor Voe
        url.Should().Be("https://cdn.voe.example.com/video.mp4");
        bridge.Verify(b => b.ExecuteCommandAsync<object, PythonVideoSourceResolver.StreamResult>(
            "resolve-stream", It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BuscarUrlEpisodioAsync_ManifiestoHlsDeYtDlp_DeberiaOmitirseEnEstaFase()
    {
        // Arrange: yt-dlp devuelve un manifiesto m3u8 (HLS segmentado) → se omite
        var (resolver, bridge) = Crear(req =>
        {
            if (req.RequestUri!.Host.Contains("animeav1.com")) return Ok(FixturePaginaEpisodio);
            if (req.RequestUri!.Host.Contains("mp4upload.com")) return Ok("<html>sin player</html>");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        bridge.Setup(b => b.ExecuteCommandAsync<object, PythonVideoSourceResolver.StreamResult>(
                "resolve-stream", It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PythonVideoSourceResolver.StreamResult { Success = true, DirectUrl = "https://cdn.example.com/master.m3u8" });

        // Act
        var url = await resolver.BuscarUrlEpisodioAsync(Titulos, 9);

        // Assert: sin URL descargable en esta fase (el descargador no maneja segmentos)
        url.Should().BeNull();
    }

    [Fact]
    public void OrdenarEmbedsPorPreferencia_DeberiaPonerMp4UploadPrimeroYOmitirMega()
    {
        // Arrange: lista en el orden del sitio (HLS, UPNShare, Voe, Byse, Mega, MP4Upload)
        var embeds = AnimeAv1HtmlParser.ExtraerEmbeds(FixturePaginaEpisodio);
        embeds.Should().HaveCount(6, "el fixture publica los 6 servidores");

        // Act
        var ordenados = AnimeAv1HtmlParser.OrdenarEmbedsPorPreferencia(embeds);

        // Assert: MP4Upload primero, luego los resolubles con yt-dlp; Mega excluido
        ordenados.Select(e => e.Server).Should().Equal("MP4Upload", "Voe", "UPNShare", "HLS", "Byse");
    }
}
