using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
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
/// TransferIt y Mega como servidores de respaldo: salen del bloque <c>downloads</c> de la página del episodio (la app solo
/// leía <c>embeds</c>) y se resuelven con la API de MEGA, sin red real (servidor falso).
/// </summary>
public class ProveedorVideoMegaTransferItTests
{
    private const string EnlaceMegaSub = "https://mega.nz/file/vvpnGa5K#nwPBwVSws3oTGESA3rDz-JvMVil1txGQFSzs4nPO2e8";
    private const string UrlTransferIt = "https://gfs1.userstorage.mega.co.nz/dl/transferit-token";
    private const string UrlMega = "https://gfs2.userstorage.mega.co.nz/dl/mega-token";

    private const string PaginaEpisodio = """
        <html><body>
        <script>{__sveltekit_1p4gm49 = {data: [{type:"data",data:{media:{id:4408,title:"Tokyo Revengers",slug:"tokyo-revengers",malId:42249,episodes:[{id:1,number:1}]},episode:{number:1},
        embeds:{SUB:[{server:"Voe",url:"https://voe.sx/e/etwdkh9ahbaj"},{server:"MP4Upload",url:"https://www.mp4upload.com/embed-c745mxa8a2lq.html"}],DUB:[{server:"MP4Upload",url:"https://www.mp4upload.com/embed-b0l2ss1u5l06.html"}]},downloads:{SUB:[{server:"TransferIt",url:"https://transfer.it/t/LWBgKbxnyZgK"},{server:"Mega",url:"https://mega.nz/file/vvpnGa5K#nwPBwVSws3oTGESA3rDz-JvMVil1txGQFSzs4nPO2e8"},{server:"1Fichier",url:"https://1fichier.com/?um6101xe9te21vq416fg"},{server:"MP4Upload",url:"https://www.mp4upload.com/c745mxa8a2lq"}],DUB:[{server:"TransferIt",url:"https://transfer.it/t/2HezjXxB3Twa"},{server:"Mega",url:"https://mega.nz/file/irIxSKSC#zFeShmordecpJs4F6Nmz9aajPHPqAmTW0KZRKizMjfc"},{server:"1Fichier",url:"https://1fichier.com/?maqlgk9aindgpyv10a8r"},{server:"MP4Upload",url:"https://www.mp4upload.com/b0l2ss1u5l06"}]}},uses:{params:["number","slug"]}}]}}</script>
        </body></html>
        """;

    private const string PlayerMp4Upload = """<html><script>var c = { src: "https://cdn.mp4upload.com/c745mxa8a2lq/video.mp4" };</script></html>""";

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<string> Peticiones { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Peticiones.Add($"{request.Method} {request.RequestUri}");
            return Task.FromResult(responder(request));
        }
    }

    private static HttpResponseMessage Ok(string cuerpo) => new(HttpStatusCode.OK) { Content = new StringContent(cuerpo) };

    private static string Cuerpo(HttpRequestMessage req) => req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";

    /// <summary>Servidor falso: la página del episodio con MP4Upload roto, la API de MEGA y los servidores de descarga.</summary>
    private static HttpResponseMessage Responder(HttpRequestMessage req, HttpStatusCode estadoDescarga = HttpStatusCode.PartialContent,
        string? respuestaTransferIt = null, string? respuestaMega = null, string player = "<html>sin player</html>")
    {
        string host = req.RequestUri!.Host;
        if (host.Contains("animeav1.com")) return Ok(PaginaEpisodio);
        if (host.Contains("mp4upload.com")) return Ok(player);
        if (host.StartsWith("bt7.api.mega"))
        {
            string cuerpo = Cuerpo(req);
            if (cuerpo.Contains("\"a\":\"f\""))
                return Ok("""[{"f":[{"h":"bTpn2ZQC","p":"","t":1},{"h":"XHp1FJ5S","p":"bTpn2ZQC","t":0,"s":206358939}]}]""");
            return Ok(respuestaTransferIt ?? $$"""[{"s":206358939,"g":"{{UrlTransferIt}}"}]""");
        }
        if (host.StartsWith("g.api.mega")) return Ok(respuestaMega ?? $$"""[{"s":206358939,"g":"{{UrlMega}}"}]""");
        if (host.EndsWith("userstorage.mega.co.nz")) return new HttpResponseMessage(estadoDescarga);
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static (ProveedorVideoAnimeAv1 Proveedor, Mock<IPythonBridgeService> Bridge, StubHandler Http) Crear(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var http = new StubHandler(responder);
        var resolver = new AnimeAv1VideoSourceResolver(new HttpClient(http));
        var bridge = new Mock<IPythonBridgeService>();
        bridge.Setup(b => b.IsAvailableAsync()).ReturnsAsync(true);
        return (new ProveedorVideoAnimeAv1(bridge.Object, resolver), bridge, http);
    }

    private static readonly string[] Titulos = ["Tokyo Revengers"];

    // === Lectura de la página ===

    [Fact]
    public void ExtraerDescargas_LeeElBloqueDownloadsConSuPistaDeAudio()
    {
        var descargas = AnimeAv1HtmlParser.ExtraerDescargas(PaginaEpisodio);

        descargas.Should().HaveCount(8);
        descargas.Where(d => d.Audio == "SUB").Select(d => d.Server).Should().Equal("TransferIt", "Mega", "1Fichier", "MP4Upload");
        descargas.Single(d => d.Audio == "DUB" && d.Server == "TransferIt").Url.Should().Be("https://transfer.it/t/2HezjXxB3Twa");
    }

    [Fact]
    public void ExtraerDescargas_SinBloqueDownloads_DevuelveVacio() =>
        AnimeAv1HtmlParser.ExtraerDescargas("<html>embeds:{SUB:[]}</html>").Should().BeEmpty();

    [Fact]
    public void ExtraerEmbeds_NoMezclaLasDescargas()
    {
        var embeds = AnimeAv1HtmlParser.ExtraerEmbeds(PaginaEpisodio);

        embeds.Select(e => e.Server).Should().BeEquivalentTo("Voe", "MP4Upload", "MP4Upload");
    }

    [Fact]
    public void OrdenarEmbedsPorPreferencia_PoneTransferItYMegaTrasMp4UploadYAntesDeVoe()
    {
        var embeds = new[]
        {
            new AnimeAv1HtmlParser.EmbedServidor("Voe", "https://voe.sx/e/a", "SUB"),
            new AnimeAv1HtmlParser.EmbedServidor("Mega", EnlaceMegaSub, "SUB"),
            new AnimeAv1HtmlParser.EmbedServidor("TransferIt", "https://transfer.it/t/LWBgKbxnyZgK", "SUB"),
            new AnimeAv1HtmlParser.EmbedServidor("MP4Upload", "https://www.mp4upload.com/embed-x.html", "SUB"),
        };

        AnimeAv1HtmlParser.OrdenarEmbedsPorPreferencia(embeds).Select(e => e.Server)
            .Should().Equal("MP4Upload", "TransferIt", "Mega", "Voe");
    }

    [Theory]
    [InlineData("https://transfer.it/t/LWBgKbxnyZgK", true)]
    [InlineData("https://mega.nz/file/vvpnGa5K#clave", true)]
    [InlineData("https://1fichier.com/?abc", false)]
    [InlineData("https://transfer.it.evil.com/t/LWBgKbxnyZgK", false)]
    public void LaListaBlancaDeEmbedsAdmiteTransferItYMega(string url, bool permitido) =>
        UrlSeguridad.EsUrlEmbedPermitida(url).Should().Be(permitido);

    // === Resolución ===

    [Fact]
    public async Task Mp4UploadRoto_ResuelveConTransferIt_ConUnaUrlEnClaroYSinTocarElDaemon()
    {
        var (proveedor, bridge, http) = Crear(req => Responder(req));

        var url = await proveedor.BuscarUrlEpisodioAsync(Titulos, 1);

        url.Should().Be(UrlTransferIt);
        bridge.Verify(b => b.ExecuteCommandOneShotAsync<object, ProveedorVideoAnimeAv1.StreamResult>(
            It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Never);
        http.Peticiones.Should().Contain(p => p.Contains("bt7.api.mega.co.nz") && p.Contains("x=LWBgKbxnyZgK"));
    }

    [Fact]
    public async Task ConPreferenciaDub_UsaLaTransferenciaDeLaPistaDub()
    {
        var (proveedor, _, http) = Crear(req => Responder(req));

        await proveedor.BuscarUrlEpisodioAsync(Titulos, 1, audioPreferido: "DUB");

        http.Peticiones.Should().Contain(p => p.Contains("x=2HezjXxB3Twa"));
        http.Peticiones.Should().NotContain(p => p.Contains("x=LWBgKbxnyZgK"));
    }

    [Fact]
    public async Task SiTransferItFalla_SigueConMegaYLaUrlLlevaLaClaveDelEnlace()
    {
        var (proveedor, _, _) = Crear(req => Responder(req, respuestaTransferIt: "[-9]"));

        var url = await proveedor.BuscarUrlEpisodioAsync(Titulos, 1);

        url.Should().StartWith(UrlMega + "#mega=");
        MegaTransferIt.ClaveDeUrl(url).Should().Equal(MegaTransferIt.ParsearEnlaceMega(EnlaceMegaSub)!.Value.Clave);
        UrlSeguridad.EsUrlDescargaHttpSegura(url).Should().BeTrue();
    }

    [Fact]
    public async Task SiLaCuotaDeMegaEstaAgotada_SaltaAlSiguienteServidor()
    {
        // Los servidores de descarga de MEGA contestan 509 (cuota gratuita agotada): no se devuelve una URL que fallaría.
        var (proveedor, bridge, _) = Crear(req => Responder(req, estadoDescarga: (HttpStatusCode)509));
        bridge.Setup(b => b.ExecuteCommandOneShotAsync<object, ProveedorVideoAnimeAv1.StreamResult>(
                "resolve-stream", It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProveedorVideoAnimeAv1.StreamResult { Success = true, DirectUrl = "https://cdn.voe.example.com/video.mp4" });

        var url = await proveedor.BuscarUrlEpisodioAsync(Titulos, 1);

        url.Should().Be("https://cdn.voe.example.com/video.mp4", "Voe es el siguiente de la lista");
    }

    [Fact]
    public async Task ConServidorPreferidoMega_LoPruebaAntesQueMp4UploadAunqueFuncione()
    {
        var (proveedor, _, _) = Crear(req => Responder(req, player: PlayerMp4Upload));

        var url = await proveedor.BuscarUrlEpisodioAsync(Titulos, 1, servidorPreferido: "Mega");

        url.Should().StartWith(UrlMega + "#mega=");
    }

    [Fact]
    public async Task Mp4UploadFunciona_NoSeTocaNiMegaNiTransferIt()
    {
        var (proveedor, _, http) = Crear(req => Responder(req, player: PlayerMp4Upload));

        var url = await proveedor.BuscarUrlEpisodioAsync(Titulos, 1);

        url.Should().Be("https://cdn.mp4upload.com/c745mxa8a2lq/video.mp4");
        http.Peticiones.Should().NotContain(p => p.Contains("api.mega"));
    }

    [Fact]
    public async Task SiNingunServidorResuelve_DevuelveNull()
    {
        var (proveedor, _, _) = Crear(req => Responder(req, respuestaTransferIt: "[-9]", respuestaMega: "[-9]"));

        (await proveedor.BuscarUrlEpisodioAsync(Titulos, 1)).Should().BeNull();
    }
}
