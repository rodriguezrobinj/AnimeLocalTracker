using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Moq;
using Xunit;

// Los títulos reales de cada caso van en línea para que el test se lea solo (CA1861 pide campos estáticos).
#pragma warning disable CA1861

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// Precisión del buscador de AnimeAv1: fallos medidos contra el sitio real (géneros tomados por
/// animes, términos basura, temporada equivocada sin MAL ID, partes que el sitio junta en una página).
/// </summary>
public class AnimeAv1BusquedaPrecisionTests
{
    // Estructura real del catálogo: results:[...] + los géneros del filtro (slug:"accion"...).
    private const string CatalogoReZero = """
        <html><a href="/media/rezero-kara-hajimeru-isekai-seikatsu-2nd-season">x</a><script>
        {type:"data",data:{results:[{id:"10",title:"Re:Zero kara Hajimeru Isekai Seikatsu 2nd Season",synopsis:"Subaru \"vuelve\" [otra vez]",categoryId:1,slug:"rezero-kara-hajimeru-isekai-seikatsu-2nd-season",category:a},{id:"11",title:"Re:Zero kara Hajimeru Isekai Seikatsu",synopsis:"...",categoryId:1,slug:"rezero-kara-hajimeru-isekai-seikatsu",category:a}],genres:[{id:1,name:"Acción",slug:"accion",malId:1},{id:2,name:"Isekai",slug:"isekai",malId:62}]}}
        </script></html>
        """;

    private const string MediaReZeroS2 = """
        <script>{data:{media:{id:10,categoryId:1,title:"Re:Zero kara Hajimeru Isekai Seikatsu 2nd Season",aka:{"en-us":"Re:ZERO -Starting Life in Another World- Season 2"},genres:[{id:1,name:"Acción",type:0,slug:"accion",malId:1}],synopsis:"...",status:1,episodesCount:25,votes:10,slug:"rezero-kara-hajimeru-isekai-seikatsu-2nd-season",malId:39587,episodes:[{id:1,number:1},{id:2,number:2},{id:3,number:3},{id:16,number:16},{id:25,number:25}],relations:[]}}}</script>
        """;

    private static string EpisodioReZeroS2(int n) =>
        "<script>{data:{media:{id:10,title:\"Re:Zero kara Hajimeru Isekai Seikatsu 2nd Season\",votes:10,slug:\"rezero-kara-hajimeru-isekai-seikatsu-2nd-season\",malId:39587},episode:{number:" + n + "}," +
        "embeds:{SUB:[{server:\"MP4Upload\",url:\"https://www.mp4upload.com/embed-rezero" + n + ".html\"}]}}}</script>";

    private sealed class SitioFalso : HttpMessageHandler
    {
        private readonly Func<string, HttpResponseMessage> _responder;
        public readonly ConcurrentQueue<string> Pedidas = new();
        public SitioFalso(Func<string, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = Uri.UnescapeDataString(request.RequestUri!.AbsoluteUri);
            Pedidas.Enqueue(url);
            return Task.FromResult(_responder(url));
        }
    }

    private static HttpResponseMessage Ok(string html) => new(HttpStatusCode.OK) { Content = new StringContent(html) };
    private static HttpResponseMessage NoEncontrado() => new(HttpStatusCode.NotFound);

    private static HttpResponseMessage SitioReZero(string url)
    {
        if (url.Contains("/catalogo")) return Ok(CatalogoReZero);
        if (url.EndsWith("/media/rezero-kara-hajimeru-isekai-seikatsu-2nd-season")) return Ok(MediaReZeroS2);
        var m = System.Text.RegularExpressions.Regex.Match(url, @"/media/rezero-kara-hajimeru-isekai-seikatsu-2nd-season/(\d+)$");
        if (m.Success) return Ok(EpisodioReZeroS2(int.Parse(m.Groups[1].Value)));
        return NoEncontrado();
    }

    [Fact]
    public void ExtraerResultadosCatalogo_SoloResultadosConTitulo_SinGeneros()
    {
        var resultados = AnimeAv1HtmlParser.ExtraerResultadosCatalogo(CatalogoReZero);

        resultados.Select(r => r.Slug).Should().Equal("rezero-kara-hajimeru-isekai-seikatsu-2nd-season", "rezero-kara-hajimeru-isekai-seikatsu");
        resultados[0].Titulo.Should().Be("Re:Zero kara Hajimeru Isekai Seikatsu 2nd Season");
        AnimeAv1HtmlParser.ExtraerSlugs(CatalogoReZero).Should().NotContain(new[] { "accion", "isekai" });
    }

    [Theory]
    [InlineData("Kami no Niwatsuki Kusunoki-tei", "tei")]
    [InlineData("Meitou \"Isekai no Yu\" Kaitaku-ki: AraFou Onsen Mania no Tensei-saki wa, Nonbiri Onsen Tengoku deshita", "ki")]
    public void GenerarTerminosBusqueda_NoPartePorGuionesPegados(string titulo, string terminoBasura)
    {
        AnimeAv1VideoSourceResolver.GenerarTerminosBusqueda(titulo).Should().NotContain(terminoBasura);
    }

    [Fact]
    public void GenerarTerminosBusqueda_TituloEnOtroAlfabeto_NoGeneraNada()
    {
        AnimeAv1VideoSourceResolver.GenerarTerminosBusqueda("บ้านพักคุสึโนกิกับสวนเทพเจ้า").Should().BeEmpty();
        AnimeAv1VideoSourceResolver.GenerarTerminosBusqueda("神の庭付き楠木邸").Should().BeEmpty();
    }

    [Fact]
    public void ResolverNumeroEpisodio_SerieConSoloElEpisodio1_NoDescargaEl1ComoEl2()
    {
        var media = new AnimeAv1VideoSourceResolver.InfoMedia("estreno", 1, "Estreno", new List<string>(), new List<(int, int)> { (1, 1) }, "");

        AnimeAv1VideoSourceResolver.ResolverNumeroEpisodio(media, 2).Should().BeNull();
    }

    [Fact]
    public void ResolverNumeroEpisodio_PeliculaNumeradaPorPosicion_SigueFuncionando()
    {
        var media = new AnimeAv1VideoSourceResolver.InfoMedia("movie-14", 1, "Película 14", new List<string>(), new List<(int, int)> { (21013, 14) }, "");

        AnimeAv1VideoSourceResolver.ResolverNumeroEpisodio(media, 1).Should().Be(14);
    }

    [Fact]
    public void DesfasePorPrecuela_SumaLasPartesHastaLaDeLaPagina()
    {
        var precuelas = new[] { new PrecuelaAnime(42203, 12), new PrecuelaAnime(39587, 13) };

        AnimeAv1VideoSourceResolver.DesfasePorPrecuela(precuelas, 42203).Should().Be(12);
        AnimeAv1VideoSourceResolver.DesfasePorPrecuela(precuelas, 39587).Should().Be(25);
        AnimeAv1VideoSourceResolver.DesfasePorPrecuela(precuelas, 999).Should().BeNull();
        AnimeAv1VideoSourceResolver.DesfasePorPrecuela(new[] { new PrecuelaAnime(39587, 0) }, 39587).Should().BeNull("sin el total de la parte no se puede calcular");
    }

    [Fact]
    public async Task ObtenerEmbeds_ParteQueElSitioJuntaConLaAnterior_PideElEpisodioDesplazado()
    {
        // Re:Zero 2nd Season Part 2 (MAL 42203) ep 3: el sitio solo tiene la página de 2nd Season (MAL 39587,
        // 25 episodios). Antes se rechazaba por MAL distinto y el episodio "no existía".
        var sitio = new SitioFalso(SitioReZero);
        var resolver = new AnimeAv1VideoSourceResolver(new HttpClient(sitio),
            malIdResolver: (_, _) => Task.FromResult<int?>(42203),
            precuelas: (_, _) => Task.FromResult<IReadOnlyList<PrecuelaAnime>>(new[] { new PrecuelaAnime(39587, 13) }));

        var embeds = await resolver.ObtenerEmbedsEpisodioAsync(new[] { "Re:Zero kara Hajimeru Isekai Seikatsu 2nd Season Part 2" }, 3, aniListId: 119661);

        embeds.Should().ContainSingle().Which.Url.Should().Contain("rezero16");
        sitio.Pedidas.Should().NotContain(u => u.EndsWith("/media/accion") || u.EndsWith("/media/isekai"), "los géneros del catálogo no son animes");
    }

    [Fact]
    public async Task ObtenerEmbeds_ParteConPaginaGuardada_TambienDesplaza()
    {
        // Segundo episodio (atajo de la página ya verificada): el desfase se recalcula con las precuelas.
        MediaAnimeAv1Verificado? guardada = null;
        var db = new Moq.Mock<IDatabaseService>();
        db.Setup(d => d.GuardarMediaAnimeAv1Async(Moq.It.IsAny<MediaAnimeAv1Verificado>())).Callback<MediaAnimeAv1Verificado>(m => guardada = m).Returns(Task.CompletedTask);
        db.Setup(d => d.ObtenerMediaAnimeAv1Async(Moq.It.IsAny<int>())).ReturnsAsync(() => guardada);
        Func<int, CancellationToken, Task<IReadOnlyList<PrecuelaAnime>>> precuelas = (_, _) => Task.FromResult<IReadOnlyList<PrecuelaAnime>>(new[] { new PrecuelaAnime(39587, 13) });
        string[] titulos = { "Re:Zero kara Hajimeru Isekai Seikatsu 2nd Season Part 2" };

        await new AnimeAv1VideoSourceResolver(new HttpClient(new SitioFalso(SitioReZero)), malIdResolver: (_, _) => Task.FromResult<int?>(42203), database: db.Object, precuelas: precuelas)
            .ObtenerEmbedsEpisodioAsync(titulos, 3, aniListId: 119661);
        var sitio = new SitioFalso(SitioReZero);
        var embeds = await new AnimeAv1VideoSourceResolver(new HttpClient(sitio), malIdResolver: (_, _) => Task.FromResult<int?>(42203), database: db.Object, precuelas: precuelas)
            .ObtenerEmbedsEpisodioAsync(titulos, 12, aniListId: 119661);

        embeds.Should().ContainSingle().Which.Url.Should().Contain("rezero25");
        sitio.Pedidas.Should().NotContain(u => u.Contains("/catalogo"), "la página ya estaba verificada");
    }

    [Fact]
    public async Task ObtenerEmbeds_SinMalId_NoAceptaOtraTemporadaDeLaMismaSerie()
    {
        // Sin MAL ID (AniList no responde y el anime no está en la biblioteca) y sin precuelas: la única
        // página es la de la parte 1. Antes se aceptaba por nombre (98 %) y bajaba el episodio equivocado.
        var resolver = new AnimeAv1VideoSourceResolver(new HttpClient(new SitioFalso(SitioReZero)));

        var embeds = await resolver.ObtenerEmbedsEpisodioAsync(new[] { "Re:Zero kara Hajimeru Isekai Seikatsu 2nd Season Part 2" }, 3, aniListId: 119661);

        embeds.Should().BeEmpty();
    }

    [Fact]
    public async Task ObtenerEmbeds_FalloPasajeroDelSitio_SeReintentaUnaVez()
    {
        int fallosMedia = 0;
        var sitio = new SitioFalso(url =>
        {
            if (url.EndsWith("/media/rezero-kara-hajimeru-isekai-seikatsu-2nd-season") && Interlocked.Increment(ref fallosMedia) == 1)
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            return SitioReZero(url);
        });
        var resolver = new AnimeAv1VideoSourceResolver(new HttpClient(sitio), malIdResolver: (_, _) => Task.FromResult<int?>(39587));

        var embeds = await resolver.ObtenerEmbedsEpisodioAsync(new[] { "Re:Zero kara Hajimeru Isekai Seikatsu 2nd Season" }, 3, aniListId: 108632);

        embeds.Should().ContainSingle().Which.Url.Should().Contain("rezero3");
    }

    [Fact]
    public void PrecuelasAnime_Construir_SigueLaCadenaMientrasEsteEnLaBiblioteca()
    {
        var relaciones = new List<RelacionAnime>
        {
            new() { AnimeId = 119661, RelacionadoId = 108632, Tipo = "PREQUEL" },
            new() { AnimeId = 119661, RelacionadoId = 128306, Tipo = "SPIN_OFF" },
            new() { AnimeId = 108632, RelacionadoId = 21355, Tipo = "PREQUEL" },
        };
        var biblioteca = new Dictionary<int, AnimeItem>
        {
            [108632] = new() { AniListId = 108632, MalId = 39587, TotalEpisodios = 13 },
        };

        var cadena = PrecuelasAnime.Construir(relaciones, 119661, id => biblioteca.GetValueOrDefault(id));

        // 21355 no está en la biblioteca: se para ahí
        cadena.Should().ContainSingle();
        (cadena[0].MalId, cadena[0].Episodios).Should().Be((39587, 13));
    }
}
