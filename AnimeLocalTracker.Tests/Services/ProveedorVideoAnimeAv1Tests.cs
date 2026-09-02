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
/// Proveedor AnimeAV1 como fuente intercambiable (Fase A): embeds multi-servidor
/// de la página del episodio (MP4Upload directo + Voe/UPNShare/HLS/Byse vía
/// yt-dlp mockeado, sin red real).
/// </summary>
public class ProveedorVideoAnimeAv1Tests
{
    private const string FixturePaginaEpisodio = """
        <html><body>
        <script>{__sveltekit_1p4gm49 = {data: [{type:"data",data:{media:{id:4408,title:"Grand Blue Season 3",slug:"grand-blue-season-3",malId:62542,episodes:[{id:60052,number:9}]},episode:{number:9},
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

    private const string FixturePeliculaMedia = """
        <html><body>
        <script>{__sveltekit_1p4gm49 = {data: [{type:"data",data:{media:{id:1328,title:"Dragon Ball Z Película 14: Battle of Gods",slug:"dragon-ball-z-movie-14-kami-to-kami",malId:14837,category:{id:2,name:"Película",slug:"pelicula"},episodes:[{id:21013,number:14}],relations:[{type:5,destination:{id:350,slug:"dragon-ball-z"}}]}}}]}}</script>
        </body></html>
        """;

    private const string FixturePeliculaEpisodio14 = """
        <html><body>
        <script>{__sveltekit_1p4gm49 = {data: [{type:"data",data:{media:{id:1328,title:"Dragon Ball Z Película 14: Battle of Gods",slug:"dragon-ball-z-movie-14-kami-to-kami",malId:14837,episodes:[{id:21013,number:14}]},episode:{id:21013,number:14,variants:{DUB:1}},
        embeds:{DUB:[
          {server:"HLS",url:"https://player.zilla-networks.com/play/ee137c2514f48a79d6c7c41063133be7"},
          {server:"Voe",url:"https://voe.sx/e/37dfuaxjurww"},
          {server:"MP4Upload",url:"https://www.mp4upload.com/embed-r0xdfbvme2yy.html"}
        ]}}]}}</script>
        </body></html>
        """;

    private const string FixtureCatalogo = """
        <html><a href="/media/dragon-ball-z-movie-14-kami-to-kami">Dragon Ball Z Película 14</a></html>
        """;

    private const string FixtureDragonBallZMedia = """
        <html><body>
        <script>{__sveltekit_1p4gm49 = {data: [{type:"data",data:{media:{id:350,categoryId:1,title:"Dragon Ball Z",slug:"dragon-ball-z",malId:813,episodes:[{id:1,number:1},{id:2,number:2}],relations:[{type:2,destination:{id:1328,slug:"dragon-ball-z-movie-14-kami-to-kami",title:"Dragon Ball Z Película 14: Battle of Gods"}},{type:2,destination:{id:1329,slug:"dragon-ball-z-movie-15-fukkatsu-no-f",title:"Dragon Ball Z Película 15: La Resurrección de F"}}]}}}]}}</script>
        </body></html>
        """;

    private static readonly string[] Titulos = { "Grand Blue Season 3" };
    private static readonly string[] TitulosPelicula = { "Dragon Ball Z: Battle of Gods" };
    private static readonly string[] TituloNativoPelicula = { "ドラゴンボールZ 神と神" };
    private static readonly string[] AkaEsperados = { "Dragon Ball Z: Battle of Gods", "ドラゴンボールZ 神と神" };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }

    private static (ProveedorVideoAnimeAv1 Proveedor, Mock<IPythonBridgeService> Bridge) Crear(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        Func<int, CancellationToken, Task<int?>>? malIdResolver = null)
    {
        var resolver = new AnimeAv1VideoSourceResolver(new HttpClient(new StubHandler(responder)), malIdResolver);
        var bridge = new Mock<IPythonBridgeService>();
        bridge.Setup(b => b.IsAvailableAsync()).ReturnsAsync(true);
        return (new ProveedorVideoAnimeAv1(bridge.Object, resolver), bridge);
    }

    private static HttpResponseMessage Ok(string html) =>
        new(HttpStatusCode.OK) { Content = new StringContent(html) };

    [Fact]
    public async Task BuscarUrlEpisodioAsync_Mp4UploadDisponible_DeberiaUsarElExtractorDirecto()
    {
        // Arrange: la página del episodio responde con los 6 embeds y el player de
        // mp4upload con src directo. Con HLS como prioridad, el daemon se intenta
        // primero (HLS/Voe/UPNShare) pero sin resultado → cae al extractor C#.
        var (proveedor, bridge) = Crear(req =>
        {
            if (req.RequestUri!.Host.Contains("animeav1.com")) return Ok(FixturePaginaEpisodio);
            if (req.RequestUri!.Host.Contains("mp4upload.com")) return Ok(FixturePlayerMp4Upload);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        // Act
        var url = await proveedor.BuscarUrlEpisodioAsync(Titulos, 9);

        // Assert: URL directa del CDN de mp4upload, validada por la política.
        // Con MP4Upload primero, el daemon no se toca.
        url.Should().Be("https://cdn.mp4upload.com/r0xdfbvme2yy/720p/video.mp4");
        bridge.Verify(b => b.ExecuteCommandAsync<object, ProveedorVideoAnimeAv1.StreamResult>(
            It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task BuscarUrlEpisodioAsync_Mp4UploadRoto_DeberiaCaerAlSiguienteServidorViaYtDlp()
    {
        // Arrange: mp4upload sin src (extracción falla) → Voe se resuelve con el daemon
        var (proveedor, bridge) = Crear(req =>
        {
            if (req.RequestUri!.Host.Contains("animeav1.com")) return Ok(FixturePaginaEpisodio);
            if (req.RequestUri!.Host.Contains("mp4upload.com")) return Ok("<html>sin player</html>");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        bridge.Setup(b => b.ExecuteCommandAsync<object, ProveedorVideoAnimeAv1.StreamResult>(
                "resolve-stream", It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProveedorVideoAnimeAv1.StreamResult { Success = true, DirectUrl = "https://cdn.voe.example.com/video.mp4" });

        // Act
        var url = await proveedor.BuscarUrlEpisodioAsync(Titulos, 9);

        // Assert: resuelto por yt-dlp con el servidor Voe
        url.Should().Be("https://cdn.voe.example.com/video.mp4");
        bridge.Verify(b => b.ExecuteCommandAsync<object, ProveedorVideoAnimeAv1.StreamResult>(
            "resolve-stream", It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BuscarUrlEpisodioAsync_ManifiestoHlsDeYtDlp_DeberiaDevolverloParaElDaemon()
    {
        // Arrange: yt-dlp devuelve un manifiesto m3u8 → se entrega al descargador
        // segmentado del daemon (fase 2), no se descarta
        var (proveedor, bridge) = Crear(req =>
        {
            if (req.RequestUri!.Host.Contains("animeav1.com")) return Ok(FixturePaginaEpisodio);
            if (req.RequestUri!.Host.Contains("mp4upload.com")) return Ok("<html>sin player</html>");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        bridge.Setup(b => b.ExecuteCommandAsync<object, ProveedorVideoAnimeAv1.StreamResult>(
                "resolve-stream", It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProveedorVideoAnimeAv1.StreamResult { Success = true, DirectUrl = "https://cdn.example.com/master.m3u8" });

        // Act
        var url = await proveedor.BuscarUrlEpisodioAsync(Titulos, 9);

        // Assert: el manifiesto se devuelve (DownloadService lo enruta al daemon)
        url.Should().Be("https://cdn.example.com/master.m3u8");
    }

    [Fact]
    public async Task BuscarUrlEpisodioAsync_MalIdDePaginaDiferente_DeberiaRechazarElAnimeEquivocado()
    {
        // Arrange: el MAL ID esperado (99999) no coincide con el de la página (62542)
        // → es OTRO anime con nombre parecido → no se resuelve nada
        var (proveedor, bridge) = Crear(req =>
        {
            if (req.RequestUri!.Host.Contains("animeav1.com")) return Ok(FixturePaginaEpisodio);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }, malIdResolver: (_, _) => Task.FromResult<int?>(99999));

        // Act
        var url = await proveedor.BuscarUrlEpisodioAsync(Titulos, 9, aniListId: 4408);

        // Assert: rechazado sin descargar (la página no es del anime buscado)
        url.Should().BeNull();
        bridge.Verify(b => b.ExecuteCommandAsync<object, ProveedorVideoAnimeAv1.StreamResult>(
            It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task BuscarUrlEpisodioAsync_MalIdCoincide_DeberiaResolverNormalmente()
    {
        // Arrange: el MAL ID esperado (62542) coincide con la página
        var (proveedor, _) = Crear(req =>
        {
            if (req.RequestUri!.Host.Contains("animeav1.com")) return Ok(FixturePaginaEpisodio);
            if (req.RequestUri!.Host.Contains("mp4upload.com")) return Ok(FixturePlayerMp4Upload);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }, malIdResolver: (_, _) => Task.FromResult<int?>(62542));

        // Act
        var url = await proveedor.BuscarUrlEpisodioAsync(Titulos, 9, aniListId: 4408);

        // Assert: resuelto (el anime de la página es el correcto)
        url.Should().Be("https://cdn.mp4upload.com/r0xdfbvme2yy/720p/video.mp4");
    }

    [Fact]
    public void ExtraerMalIdDelMedia_ConHtmlDelSitio_DeberiaExtraerElMalIdDelAnime()
    {
        // Act
        var malId = AnimeAv1HtmlParser.ExtraerMalIdDelMedia(FixturePaginaEpisodio);

        // Assert: el par slug+malId del media, no el de los géneros
        malId.Should().Be(62542);
    }

    [Fact]
    public void ExtraerMalIdDelMedia_ConGenerosConMalIdAntes_DeberiaExtraerElDelAnime()
    {
        // Arrange: payload real — los géneros (malId 10/22) aparecen ANTES del media
        // (malId 61240). Regresión: la regex antigua tomaba el malId del género.
        string html = """
            <script>{__sveltekit_1p4gm49 = {data: [{type:"data",data:{media:{id:4426,title:"Futsutsuka na Akujo dewa Gozaimasu ga: Suuguu Chouso Torikae Den",aka:{},genres:[{id:7,name:"Fantasía",type:0,slug:"fantasia",malId:10},{id:10,name:"Romance",type:0,slug:"romance",malId:22}],synopsis:"...",status:2,score:8.16,votes:9307,slug:"futsutsuka-na-akujo-dewa-gozaimasu-ga-suuguu-chouso-torikae-den",malId:61240,seasons:null,episodes:[{id:59664,number:3}]}}}]}}</script>
            """;

        // Act
        var malId = AnimeAv1HtmlParser.ExtraerMalIdDelMedia(html);

        // Assert: el malId del ANIME, no el del género "Fantasía"
        malId.Should().Be(61240);
    }

    [Fact]
    public void ExtraerMalIdDelMedia_SinVotes_DeberiaDevolverLaPrimeraCoincidencia()
    {
        // Fixtures de test sin votes (Grand Blue/DBZ): primera coincidencia = media
        AnimeAv1HtmlParser.ExtraerMalIdDelMedia(FixturePeliculaMedia).Should().Be(14837);
    }

    [Fact]
    public void ExtraerMalIdDelMedia_SinMediaEnLaPagina_DeberiaDevolverNull()
    {
        AnimeAv1HtmlParser.ExtraerMalIdDelMedia("<html>sin datos</html>").Should().BeNull();
    }

    [Fact]
    public void ExtraerEpisodiosDelMedia_ConPayloadDelSitio_DeberiaExtraerLosEpisodiosReales()
    {
        // Act
        var episodios = AnimeAv1HtmlParser.ExtraerEpisodiosDelMedia(FixturePeliculaMedia);

        // Assert: la película se numera como Ep 14 en el catálogo del sitio
        episodios.Should().ContainSingle();
        episodios[0].Id.Should().Be(21013);
        episodios[0].Numero.Should().Be(14);
    }

    [Fact]
    public void ExtraerEpisodiosDelMedia_SinSeccionDeEpisodios_DeberiaDevolverVacio()
    {
        AnimeAv1HtmlParser.ExtraerEpisodiosDelMedia("<html>sin episodios</html>").Should().BeEmpty();
    }

    [Fact]
    public async Task BuscarUrlEpisodioAsync_PeliculaNumeradaDistinto_DeberiaResolverPorLaPaginaDelMedia()
    {
        // Arrange: la app registra la película como Episodio 1, pero el sitio la
        // numera como Ep 14 (posición en el catálogo). El slug del sitio contiene
        // "-movie-14-" y el título de AniList no dice "movie" → la heurística sola
        // lo rechazaría; con MAL ID conocido se acepta y FASE 3 corrige el número.
        var (proveedor, bridge) = Crear(req =>
        {
            var u = req.RequestUri!.AbsoluteUri;
            if (req.RequestUri.Host.Contains("mp4upload.com")) return Ok(FixturePlayerMp4Upload);
            if (u.Contains("/catalogo")) return Ok(FixtureCatalogo);
            if (u.Contains("/media/dragon-ball-z-movie-14-kami-to-kami/14")) return Ok(FixturePeliculaEpisodio14);
            if (u.Contains("/media/dragon-ball-z-movie-14-kami-to-kami")) return Ok(FixturePeliculaMedia);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }, malIdResolver: (_, _) => Task.FromResult<int?>(14837));

        // Act: episodio 1 solicitado (la app lo registra así)
        var url = await proveedor.BuscarUrlEpisodioAsync(TitulosPelicula, 1, aniListId: 1328);

        // Assert: resuelto con el número real del sitio (14) y el extractor directo
        url.Should().Be("https://cdn.mp4upload.com/r0xdfbvme2yy/720p/video.mp4");
    }

    [Fact]
    public async Task BuscarUrlEpisodioAsync_PeliculaDeOtroMalId_DeberiaRechazarEnLaPaginaDelMedia()
    {
        // Arrange: la página del media es de OTRA película (malId distinto)
        var (proveedor, bridge) = Crear(req =>
        {
            var u = req.RequestUri!.AbsoluteUri;
            if (u.Contains("/catalogo")) return Ok(FixtureCatalogo);
            if (u.Contains("/media/dragon-ball-z-movie-14-kami-to-kami")) return Ok(FixturePeliculaMedia);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }, malIdResolver: (_, _) => Task.FromResult<int?>(99999));

        // Act
        var url = await proveedor.BuscarUrlEpisodioAsync(TitulosPelicula, 1, aniListId: 1328);

        // Assert: rechazada (ni el episodio ni la página del media pasan el malId)
        url.Should().BeNull();
    }

    [Fact]
    public void GenerarTerminosBusqueda_ConSubtitulo_DeberiaIncluirElSubtituloComoTermino()
    {
        // Act: el título de AniList no dice "Película 14"; el subtítulo tras ':' es
        // lo que el catálogo del sitio matchea con "Dragon Ball Z Película 14: Battle of Gods"
        var terminos = AnimeAv1VideoSourceResolver.GenerarTerminosBusqueda("Dragon Ball Z: Battle of Gods");

        // Assert: el subtítulo y la cola se generan como términos de búsqueda
        terminos.Should().Contain("Battle of Gods");
        terminos.Should().Contain("Dragon Ball Z");
    }

    [Fact]
    public async Task BuscarUrlEpisodioAsync_Pelicula_DeberiaBuscarPorElSubtituloYResolver()
    {
        // Arrange: el catálogo solo encuentra la película cuando se busca "Battle of
        // Gods" (el término "Dragon Ball Z" pagina y la deja fuera)
        var busquedas = new List<string>();
        var (proveedor, bridge) = Crear(req =>
        {
            var u = req.RequestUri!.AbsoluteUri;
            if (req.RequestUri.Host.Contains("mp4upload.com")) return Ok(FixturePlayerMp4Upload);
            if (u.Contains("/catalogo"))
            {
                busquedas.Add(u);
                return Ok(FixtureCatalogo);
            }
            if (u.Contains("/media/dragon-ball-z-movie-14-kami-to-kami/14")) return Ok(FixturePeliculaEpisodio14);
            if (u.Contains("/media/dragon-ball-z-movie-14-kami-to-kami")) return Ok(FixturePeliculaMedia);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }, malIdResolver: (_, _) => Task.FromResult<int?>(14837));

        // Act
        var url = await proveedor.BuscarUrlEpisodioAsync(TitulosPelicula, 1, aniListId: 1328);

        // Assert: en alguna búsqueda se usó el subtítulo (lo distintivo) y se resolvió
        busquedas.Should().Contain(b => Uri.UnescapeDataString(b).Contains("Battle of Gods"));
        url.Should().Be("https://cdn.mp4upload.com/r0xdfbvme2yy/720p/video.mp4");
    }

    [Fact]
    public void ExtraerTitulosDelMedia_ConPayloadReal_DeberiaExtraerPrincipalYAka()
    {
        // Arrange: el payload del media tiene title + aka con varios idiomas
        string html = """
            <script>{__sveltekit_1p4gm49 = {data: [{type:"data",data:{media:{id:1328,categoryId:2,title:"Dragon Ball Z Película 14: Battle of Gods",aka:{"en-us":"Dragon Ball Z: Battle of Gods","ja-jp":"ドラゴンボールZ 神と神","es-419":"Dragon Ball Z Película 14: Battle of Gods"},genres:[],synopsis:"..."}}}}]}}</script>
            """;

        // Act
        var titulos = AnimeAv1HtmlParser.ExtraerTitulosDelMedia(html);

        // Assert
        titulos.Should().NotBeNull();
        titulos!.Principal.Should().Be("Dragon Ball Z Película 14: Battle of Gods");
        titulos.Alternativos.Should().Contain(AkaEsperados);
    }

    [Fact]
    public void ExtraerTitulosDelMedia_SinMedia_DeberiaDevolverNull()
    {
        AnimeAv1HtmlParser.ExtraerTitulosDelMedia("<html>sin datos</html>").Should().BeNull();
    }

    [Fact]
    public async Task BuscarUrlEpisodioAsync_SinMalIdComparable_DeberiaAceptarPorNombreSimilar()
    {
        // Arrange: sin malIdResolver (malId no comparable en NINGÚN lado se ignora)
        // → el veredicto cae a nombres: "Battle of Gods" vs "Película 14: Battle of Gods" ≥ 70%
        var (proveedor, _) = Crear(req =>
        {
            var u = req.RequestUri!.AbsoluteUri;
            if (req.RequestUri.Host.Contains("mp4upload.com")) return Ok(FixturePlayerMp4Upload);
            if (u.Contains("/catalogo")) return Ok(FixtureCatalogo);
            if (u.Contains("/media/dragon-ball-z-movie-14-kami-to-kami/14")) return Ok(FixturePeliculaEpisodio14);
            if (u.Contains("/media/dragon-ball-z-movie-14-kami-to-kami")) return Ok(FixturePeliculaMedia);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        // Act: sin aniListId → sin malId esperado → veredicto por nombres (fallback C#)
        var url = await proveedor.BuscarUrlEpisodioAsync(TitulosPelicula, 1);

        // Assert: aceptado por similitud de nombres
        url.Should().Be("https://cdn.mp4upload.com/r0xdfbvme2yy/720p/video.mp4");
    }

    [Fact]
    public async Task BuscarUrlEpisodioAsync_MalIdDistintoPeroNombreParecido_DeberiaRechazarIgual()
    {
        // Arrange: malId esperado 99999 vs página 14837 — el malId es autoritativo:
        // aunque "Battle of Gods" sea parecido al título del site, se rechaza
        var (proveedor, _) = Crear(req =>
        {
            var u = req.RequestUri!.AbsoluteUri;
            if (req.RequestUri.Host.Contains("mp4upload.com")) return Ok(FixturePlayerMp4Upload);
            if (u.Contains("/catalogo")) return Ok(FixtureCatalogo);
            if (u.Contains("/media/dragon-ball-z-movie-14-kami-to-kami")) return Ok(FixturePeliculaMedia);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }, malIdResolver: (_, _) => Task.FromResult<int?>(99999));

        // Act
        var url = await proveedor.BuscarUrlEpisodioAsync(TitulosPelicula, 1, aniListId: 1328);

        // Assert: el malId manda sobre el nombre
        url.Should().BeNull();
    }

    [Fact]
    public async Task BuscarUrlEpisodioAsync_TituloNativoJapones_DeberiaBuscarYResolver()
    {
        // Arrange: el título principal no matchea el catálogo pero el nativo (aka
        // ja-jp del sitio) sí — solo se pasa el japonés
        var busquedas = new List<string>();
        var (proveedor, _) = Crear(req =>
        {
            var u = req.RequestUri!.AbsoluteUri;
            if (req.RequestUri.Host.Contains("mp4upload.com")) return Ok(FixturePlayerMp4Upload);
            if (u.Contains("/catalogo"))
            {
                busquedas.Add(u);
                return Ok(FixtureCatalogo);
            }
            if (u.Contains("/media/dragon-ball-z-movie-14-kami-to-kami/14")) return Ok(FixturePeliculaEpisodio14);
            if (u.Contains("/media/dragon-ball-z-movie-14-kami-to-kami")) return Ok(FixturePeliculaMedia);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }, malIdResolver: (_, _) => Task.FromResult<int?>(14837));

        // Act
        var url = await proveedor.BuscarUrlEpisodioAsync(TituloNativoPelicula, 1, aniListId: 1328);

        // Assert: se buscó con el título japonés y la película se resolvió
        busquedas.Should().Contain(b => Uri.UnescapeDataString(b).Contains("ドラゴンボールZ"));
        url.Should().Be("https://cdn.mp4upload.com/r0xdfbvme2yy/720p/video.mp4");
    }

    [Fact]
    public void ExtraerRelationsDelMedia_ConPayloadReal_DeberiaExtraerSlugsYTitulos()
    {
        // Act
        var relations = AnimeAv1HtmlParser.ExtraerRelationsDelMedia(FixtureDragonBallZMedia);

        // Assert
        relations.Should().Contain(r => r.Slug == "dragon-ball-z-movie-14-kami-to-kami");
        relations.Should().Contain(r => r.Slug == "dragon-ball-z-movie-15-fukkatsu-no-f");
        relations.Should().OnlyContain(r => !string.IsNullOrWhiteSpace(r.Titulo));
    }

    [Fact]
    public async Task BuscarUrlEpisodioAsync_Franquicia_DeberiaEncontrarLaPeliculaViaRelations()
    {
        // Arrange: el catálogo solo devuelve el anime principal (dragon-ball-z);
        // su media rechazado por malId encola las relations → movie-14 → resuelve
        var (proveedor, _) = Crear(req =>
        {
            var u = req.RequestUri!.AbsoluteUri;
            if (req.RequestUri.Host.Contains("mp4upload.com")) return Ok(FixturePlayerMp4Upload);
            if (u.Contains("/catalogo")) return Ok("<html><a href='/media/dragon-ball-z'>Dragon Ball Z</a></html>");
            if (u.Contains("/media/dragon-ball-z-movie-14-kami-to-kami/14")) return Ok(FixturePeliculaEpisodio14);
            if (u.Contains("/media/dragon-ball-z-movie-14-kami-to-kami")) return Ok(FixturePeliculaMedia);
            if (u.Contains("/media/dragon-ball-z")) return Ok(FixtureDragonBallZMedia);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }, malIdResolver: (_, _) => Task.FromResult<int?>(14837));

        // Act
        var url = await proveedor.BuscarUrlEpisodioAsync(TitulosPelicula, 1, aniListId: 1328);

        // Assert: el crawl de relations encontró la película aunque el slug no
        // estuviera en los resultados del catálogo
        url.Should().Be("https://cdn.mp4upload.com/r0xdfbvme2yy/720p/video.mp4");
    }

    [Fact]
    public void OrdenarEmbedsPorPreferencia_DeberiaPonerMp4UploadPrimeroYOmitirMega()
    {
        // Arrange: lista en el orden del sitio (HLS, UPNShare, Voe, Byse, Mega, MP4Upload)
        var embeds = AnimeAv1HtmlParser.ExtraerEmbeds(FixturePaginaEpisodio);
        embeds.Should().HaveCount(6, "el fixture publica los 6 servidores");

        // Act
        var ordenados = AnimeAv1HtmlParser.OrdenarEmbedsPorPreferencia(embeds);

        // Assert: MP4Upload primero (única fuente fiable hoy; HLS tras Cloudflare
        // anti-bot, Voe/UPNShare/Byse sin extractor); Mega excluido
        ordenados.Select(e => e.Server).Should().Equal("MP4Upload", "HLS", "Voe", "UPNShare", "Byse");
    }
}
