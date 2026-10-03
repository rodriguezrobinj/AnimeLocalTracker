using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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
/// Mediafire (descarga directa), Vidhide y Streamwish (mismo reproductor, listas HLS) como servidores de JKAnime. Los resuelve el daemon
/// de Python; aquí se prueba lo de C#: cómo se llaman, qué hosts se admiten, en qué orden van y que el daemon recibe el nombre del
/// servidor (los dominios de Vidhide y Streamwish rotan, así que el nombre que da el sitio manda sobre el dominio).
/// </summary>
public class ServidoresMediafireVidhideTests
{
    private static string B64(string url) => Convert.ToBase64String(Encoding.UTF8.GetBytes(url + "\n"));

    private static string Episodio(params (string Servidor, string Url)[] servidores) =>
        "<script>var servers = [" + string.Join(",", servidores.Select(s =>
            $$"""{"slug":"x","server":"{{s.Servidor}}","lang":1,"size":"398 MB","append":0,"remote":"{{B64(s.Url)}}"}""")) + "];</script>";

    private static string Item(string slug, string titulo) => $$"""
        <div class="anime__item">
            <a
              href="https://jkanime.net/{{slug}}/"><div class="g-0"></div></a>
            <div class="anime__item__text"><ul><li>Emisión</li>
                <li class="anime">Serie</li></ul>
                <h5><a
                   href="https://jkanime.net/{{slug}}/">
                  {{titulo}}
                 </a></h5>
            </div>
        </div>
        """;

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(responder(request));
    }

    private static HttpResponseMessage Ok(string cuerpo) => new(HttpStatusCode.OK) { Content = new StringContent(cuerpo) };

    private static (ProveedorVideoJkAnime Proveedor, Mock<IPythonBridgeService> Bridge, List<string> Payloads) Crear(string paginaEpisodio)
    {
        var payloads = new List<string>();
        var http = new HttpClient(new StubHandler(req => req.RequestUri!.AbsoluteUri.Contains("/buscar/")
            ? Ok(Item("tokyo-revengers-santen-sensou-hen", "Tokyo Revengers: Santen Sensou-hen"))
            : req.RequestUri.AbsoluteUri.Contains("/tokyo-revengers-santen-sensou-hen/1/") ? Ok(paginaEpisodio) : new HttpResponseMessage(HttpStatusCode.NotFound)));
        var bridge = new Mock<IPythonBridgeService>();
        bridge.Setup(b => b.IsAvailableAsync()).ReturnsAsync(true);
        bridge.Setup(b => b.ExecuteCommandOneShotAsync<object, ProveedorVideoAnimeAv1.StreamResult>("resolve-stream", It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Callback<string, object, CancellationToken>((_, payload, _) => payloads.Add(JsonSerializer.Serialize(payload)))
            .ReturnsAsync(new ProveedorVideoAnimeAv1.StreamResult { Success = true, DirectUrl = "https://cdn.resuelto.example.com/master.m3u8" });
        return (new ProveedorVideoJkAnime(bridge.Object, new AnimeAv1VideoSourceResolver(http), new JkAnimeClient(http)), bridge, payloads);
    }

    private static readonly string[] Titulos = ["Tokyo Revengers: Santen Sensou-hen"];
    private static readonly string[] NombresDesordenados = ["Streamwish", "Voe", "Vidhide", "Mediafire", "Mega", "MP4Upload"];

    // === Nombres y lista blanca ===

    [Fact]
    public void JkAnimeLlamaAEstosServidoresComoLaApp()
    {
        var servidores = JkAnimeHtmlParser.ExtraerServidores(Episodio(
            ("Mediafire", "https://mediafire.com/file/x/"), ("Vidhide", "https://vidhidevip.com/embed/x"), ("Streamwish", "https://sfastwish.com/e/x")));

        servidores.Select(s => s.Server).Should().Equal("Mediafire", "Vidhide", "Streamwish");
    }

    [Theory]
    [InlineData("Mediafire", "https://www.mediafire.com/file/4f7fyuv9kcrn0em/", true)]
    [InlineData("Mediafire", "https://evil.example.com/file/x/", false)]            // Mediafire solo en su dominio
    [InlineData("Vidhide", "https://vidhidevip.com/embed/ja75jxviemst", true)]
    [InlineData("Vidhide", "https://callistanise.com/e/ja75jxviemst", true)]        // sus dominios rotan: vale cualquiera público...
    [InlineData("Streamwish", "https://dominio-nuevo-que-rota.example/e/abc", true)]
    [InlineData("streamwish", "https://dominio-nuevo-que-rota.example/e/abc", true)]
    [InlineData("Vidhide", "http://callistanise.com/e/x", false)]                   // ...pero solo https
    [InlineData("Vidhide", "https://127.0.0.1/e/x", false)]                         // ...y nunca la propia máquina
    [InlineData("Vidhide", "https://192.168.1.10/e/x", false)]
    [InlineData("Vidhide", "https://intranet/e/x", false)]
    [InlineData("Vidhide", "https://usuario:clave@callistanise.com/e/x", false)]
    [InlineData("Servidor-raro", "https://dominio-nuevo-que-rota.example/e/abc", false)] // un nombre desconocido no abre la puerta
    [InlineData("Voe", "https://dominio-nuevo-que-rota.example/e/abc", false)]
    [InlineData("Voe", "https://voe.sx/e/abc", true)]                               // los de siempre siguen igual
    public void ElEmbedDeUnServidorSoloSePermiteSiEncajaConSuNombre(string servidor, string url, bool esperado) =>
        UrlSeguridad.EsEmbedDeServidorPermitido(servidor, url).Should().Be(esperado);

    // === Orden ===

    [Fact]
    public void ElOrdenPorDefecto_PoneLosDe1080pAntesQueVoe_YStreamwishDetras()
    {
        var embeds = NombresDesordenados
            .Select(n => new AnimeAv1HtmlParser.EmbedServidor(n, $"https://{n.ToLowerInvariant()}.example/x", "SUB")).ToArray();

        AnimeAv1HtmlParser.OrdenarEmbedsPorPreferencia(embeds).Select(e => e.Server)
            .Should().Equal("MP4Upload", "Mega", "Mediafire", "Vidhide", "Voe", "Streamwish");
    }

    // === Resolución ===

    [Fact]
    public async Task ElDaemonRecibeElNombreDelServidor_AunqueElDominioSeaNuevo()
    {
        var (proveedor, _, payloads) = Crear(Episodio(("Vidhide", "https://dominio-que-rota.example/e/ja75jxviemst")));

        var url = await proveedor.BuscarUrlEpisodioAsync(Titulos, 1);

        url.Should().Be("https://cdn.resuelto.example.com/master.m3u8");
        payloads.Should().ContainSingle();
        payloads[0].Should().Contain("\"server\":\"Vidhide\"").And.Contain("https://dominio-que-rota.example/e/ja75jxviemst");
    }

    [Fact]
    public async Task MediafireSeResuelveConElDaemon()
    {
        var (proveedor, _, payloads) = Crear(Episodio(("Mediafire", "https://www.mediafire.com/file/4f7fyuv9kcrn0em/")));

        (await proveedor.BuscarUrlEpisodioAsync(Titulos, 1)).Should().NotBeNull();
        payloads[0].Should().Contain("\"server\":\"Mediafire\"");
    }

    [Fact]
    public async Task UnServidorConDominioQueNoEncaja_NoLlegaAlDaemon()
    {
        var (proveedor, bridge, payloads) = Crear(Episodio(("Mediafire", "https://evil.example.com/file/x/"), ("Vidhide", "https://127.0.0.1/e/x")));

        (await proveedor.BuscarUrlEpisodioAsync(Titulos, 1)).Should().BeNull();
        payloads.Should().BeEmpty();
    }

    [Fact]
    public async Task ElOrdenDelUsuarioTambienAplicaAEstosServidores()
    {
        var (proveedor, _, payloads) = Crear(Episodio(("Mediafire", "https://www.mediafire.com/file/x/"), ("Vidhide", "https://vidhidevip.com/embed/x")));

        await proveedor.BuscarUrlEpisodioAsync(Titulos, 1, servidorPreferido: "Streamwish,Vidhide,Mediafire");

        payloads[0].Should().Contain("\"server\":\"Vidhide\"", "Vidhide va antes que Mediafire en el orden elegido");
    }
}
