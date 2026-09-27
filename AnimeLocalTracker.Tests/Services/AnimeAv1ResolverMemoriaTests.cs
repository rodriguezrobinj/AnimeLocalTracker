using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// Descargar varios episodios del mismo anime no debe repetir la búsqueda completa en el
/// sitio (búsquedas de catálogo + decenas de páginas candidatas) para cada episodio.
/// </summary>
public class AnimeAv1ResolverMemoriaTests
{
    // Página de media/episodio del sitio (misma estructura SvelteKit que ProveedorVideoAnimeAv1Tests).
    private const string FixturePagina = """
        <html><body>
        <script>{__sveltekit_1p4gm49 = {data: [{type:"data",data:{media:{id:4408,title:"Grand Blue Season 3",slug:"grand-blue-season-3",malId:62542,episodes:[{id:60051,number:8},{id:60052,number:9}]},episode:{number:9},
        embeds:{SUB:[
          {server:"MP4Upload",url:"https://www.mp4upload.com/embed-r0xdfbvme2yy.html"}
        ]}}]}}</script>
        </body></html>
        """;

    private static readonly string[] TitulosGrandBlue = { "Grand Blue Season 3" };
    private static readonly int[] EpisodiosALaVez = { 8, 9, 8, 9, 8 };

    private sealed class ContadorHandler : HttpMessageHandler
    {
        public int Peticiones;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Peticiones);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(FixturePagina) });
        }
    }

    [Fact]
    public async Task ObtenerEmbedsEpisodioAsync_SegundoEpisodioDelMismoAnime_VaDirectoALaPaginaYaVerificada()
    {
        var handler = new ContadorHandler();
        var resolver = new AnimeAv1VideoSourceResolver(new HttpClient(handler));
        string[] titulos = { "Grand Blue Season 3" };

        var primero = await resolver.ObtenerEmbedsEpisodioAsync(titulos, 8, aniListId: 900);
        int peticionesPrimero = handler.Peticiones;

        handler.Peticiones = 0;
        var segundo = await resolver.ObtenerEmbedsEpisodioAsync(titulos, 9, aniListId: 900);

        primero.Should().NotBeEmpty();
        segundo.Should().NotBeEmpty();
        handler.Peticiones.Should().Be(2, "página del anime (¿existe el episodio?) + página del episodio");
        peticionesPrimero.Should().BeGreaterThan(2);
    }

    [Fact]
    public async Task ObtenerEmbedsEpisodioAsync_TrasReiniciarLaApp_UsaLaPaginaGuardadaEnLaBaseDeDatos()
    {
        // Sesión 1: búsqueda completa; la página verificada se guarda en la base de datos.
        AnimeLocalTracker.Models.MediaAnimeAv1Verificado? guardada = null;
        var db = new Moq.Mock<IDatabaseService>();
        db.Setup(d => d.GuardarMediaAnimeAv1Async(Moq.It.IsAny<AnimeLocalTracker.Models.MediaAnimeAv1Verificado>()))
            .Callback<AnimeLocalTracker.Models.MediaAnimeAv1Verificado>(m => guardada = m)
            .Returns(Task.CompletedTask);
        db.Setup(d => d.ObtenerMediaAnimeAv1Async(Moq.It.IsAny<int>())).ReturnsAsync(() => guardada);
        string[] titulos = { "Grand Blue Season 3" };

        var sesion1 = new AnimeAv1VideoSourceResolver(new HttpClient(new ContadorHandler()), database: db.Object);
        (await sesion1.ObtenerEmbedsEpisodioAsync(titulos, 8, aniListId: 901)).Should().NotBeEmpty();
        guardada.Should().NotBeNull();
        guardada!.AniListId.Should().Be(901);
        guardada.Slug.Should().NotBeNullOrWhiteSpace();

        // Sesión 2 (app reiniciada: resolver nuevo, memoria vacía) → directo a la página guardada.
        var handler = new ContadorHandler();
        var sesion2 = new AnimeAv1VideoSourceResolver(new HttpClient(handler), database: db.Object);
        var embeds = await sesion2.ObtenerEmbedsEpisodioAsync(titulos, 9, aniListId: 901);

        embeds.Should().NotBeEmpty();
        handler.Peticiones.Should().Be(2);
    }

    private sealed class ContadorCatalogoHandler : HttpMessageHandler
    {
        public int BusquedasCatalogo;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.Contains("catalogo")) Interlocked.Increment(ref BusquedasCatalogo);
            await Task.Delay(20, cancellationToken); // que las peticiones de episodios distintos se solapen de verdad
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(FixturePagina) };
        }
    }

    [Fact]
    public async Task ObtenerEmbedsEpisodioAsync_VariosEpisodiosALaVezDeUnAnimeNuevo_HacenUnaSolaBusqueda()
    {
        // Log real (16:33): 5 episodios de Jujutsu Kaisen a la vez repetían en paralelo la misma búsqueda de catálogo.
        var referencia = new ContadorCatalogoHandler();
        await new AnimeAv1VideoSourceResolver(new HttpClient(referencia)).ObtenerEmbedsEpisodioAsync(TitulosGrandBlue, 8, aniListId: 903);

        var handler = new ContadorCatalogoHandler();
        var resolver = new AnimeAv1VideoSourceResolver(new HttpClient(handler));
        var resultados = await Task.WhenAll(EpisodiosALaVez.Select(ep => resolver.ObtenerEmbedsEpisodioAsync(TitulosGrandBlue, ep, aniListId: 904)));

        resultados.Should().OnlyContain(r => r.Count > 0);
        handler.BusquedasCatalogo.Should().Be(referencia.BusquedasCatalogo, "una sola búsqueda completa; los demás usan la página encontrada");
    }

    [Fact]
    public async Task ObtenerEmbedsEpisodioAsync_SiLaBaseDeDatosFalla_HaceLaBusquedaCompletaIgual()
    {
        var db = new Moq.Mock<IDatabaseService>();
        db.Setup(d => d.ObtenerMediaAnimeAv1Async(Moq.It.IsAny<int>())).ThrowsAsync(new InvalidOperationException("base bloqueada"));
        db.Setup(d => d.GuardarMediaAnimeAv1Async(Moq.It.IsAny<AnimeLocalTracker.Models.MediaAnimeAv1Verificado>())).ThrowsAsync(new InvalidOperationException("base bloqueada"));

        var resolver = new AnimeAv1VideoSourceResolver(new HttpClient(new ContadorHandler()), database: db.Object);

        (await resolver.ObtenerEmbedsEpisodioAsync(TitulosGrandBlue, 9, aniListId: 902)).Should().NotBeEmpty();
    }
}
