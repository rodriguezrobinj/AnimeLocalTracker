using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// animeav1.com leído por su respuesta de datos (página/__data.json) en vez de con expresiones regulares sobre el HTML: se lee
/// por nombre de campo, así que el sitio puede reordenar o añadir campos. El HTML queda de respaldo.
/// </summary>
public class AnimeAv1DatosJsonTests
{
    [Theory]
    [InlineData("https://animeav1.com/media/dungeon-meshi/1", "https://animeav1.com/media/dungeon-meshi/1/__data.json")]
    [InlineData("https://animeav1.com/media/dungeon-meshi/", "https://animeav1.com/media/dungeon-meshi/__data.json")]
    [InlineData("https://animeav1.com/catalogo?search=dungeon%20meshi", "https://animeav1.com/catalogo/__data.json?search=dungeon%20meshi")]
    public void UrlDeDatos_VaTrasLaRutaYAntesDeLosParametros(string pagina, string esperada)
    {
        AnimeAv1HtmlParser.UrlDeDatos(pagina).Should().Be(esperada);
    }

    [Fact]
    public void PaginaDeEpisodioReal_SeLeenServidoresDescargasYDatosDelAnime()
    {
        var embeds = AnimeAv1HtmlParser.ExtraerEmbeds(EpisodioReal);
        embeds.Should().HaveCount(16, "8 servidores por pista");
        embeds.Select(e => e.Audio).Distinct().Should().Equal("DUB", "SUB");
        embeds.Where(e => e.Audio == "SUB").Select(e => e.Server)
            .Should().Equal("HLS", "UPNShare", "Voe", "Byse", "YourUpload", "MP4Upload", "VidHide", "StreamTape");
        embeds.Should().OnlyContain(e => e.Url.StartsWith("https://"));

        var descargas = AnimeAv1HtmlParser.ExtraerDescargas(EpisodioReal);
        descargas.Where(d => d.Audio == "SUB").Select(d => d.Server).Should().Equal("Mega", "1Fichier", "MP4Upload", "StreamTape");
        descargas.First(d => d.Server == "Mega").Url.Should().StartWith("https://mega.nz/file/");

        AnimeAv1HtmlParser.ExtraerMalIdDelMedia(EpisodioReal).Should().Be(25777);
        AnimeAv1HtmlParser.ExtraerEpisodiosDelMedia(EpisodioReal).Select(e => e.Numero).Should().Equal(Enumerable.Range(1, 12));

        var titulos = AnimeAv1HtmlParser.ExtraerTitulosDelMedia(EpisodioReal);
        titulos!.Principal.Should().Be("Shingeki no Kyojin Season 2");
        titulos.Alternativos.Should().BeEquivalentTo("Attack on Titan Season 2", "進撃の巨人 Season2", "Ataque a los Titanes Temporada 2");

        AnimeAv1HtmlParser.ExtraerRelationsDelMedia(EpisodioReal).Should().Equal(
            ("shingeki-no-kyojin", "Shingeki no Kyojin"), ("shingeki-no-kyojin-season-3", "Shingeki no Kyojin Season 3"));
    }

    [Fact]
    public void SiElSitioReordenaOAnadeCampos_SeSigueLeyendo()
    {
        // Con el HTML, "url" antes que "server", un campo nuevo delante del título o "title" antes que "slug" en una relación
        // dejaban la lectura vacía: las expresiones regulares esperan los campos en un orden.
        const string reordenado = """
            {"type":"data","nodes":[null,{"type":"data","data":[{"embeds":1,"media":6},{"SUB":2},[3],{"nuevo":4,"url":5,"server":4},"Voe","https://voe.sx/e/abc",
            {"nuevo":4,"malId":7,"title":8,"relations":9},62542,"Grand Blue Season 3",[10],{"destination":11},{"title":12,"slug":13,"id":7},"Grand Blue","grand-blue"]}]}
            """;

        AnimeAv1HtmlParser.ExtraerEmbeds(reordenado).Should().Equal(new AnimeAv1HtmlParser.EmbedServidor("Voe", "https://voe.sx/e/abc", "SUB"));
        AnimeAv1HtmlParser.ExtraerMalIdDelMedia(reordenado).Should().Be(62542);
        AnimeAv1HtmlParser.ExtraerTitulosDelMedia(reordenado)!.Principal.Should().Be("Grand Blue Season 3");
        AnimeAv1HtmlParser.ExtraerRelationsDelMedia(reordenado).Should().Equal(("grand-blue", "Grand Blue"));
    }

    [Fact]
    public void BusquedaDelCatalogo_DaLosResultadosEnElOrdenDelSitio_SinRepetidosNiSlugsRaros()
    {
        // Misma forma que la respuesta real (results, total, mapas de géneros…), recortada a mano.
        const string catalogo = """
            {"type":"data","nodes":[null,{"type":"data","data":[{"user":1},null]},{"type":"data","data":[{"results":1,"total":2,"genresIdsMap":3},
            [4,8,4,11],5,{"7":6},{"id":5,"title":6,"slug":7},"130","Dungeon Meshi","dungeon-meshi",{"id":5,"title":9,"slug":10},"Dungeon no Naka no Hito",
            "dungeon-no-naka-no-hito",{"id":5,"title":6,"slug":12},"../catalogo?x=1"]}]}
            """;

        AnimeAv1HtmlParser.ExtraerResultadosCatalogo(catalogo).Should().Equal(
            new AnimeAv1HtmlParser.ResultadoCatalogo("dungeon-meshi", "Dungeon Meshi"),
            new AnimeAv1HtmlParser.ResultadoCatalogo("dungeon-no-naka-no-hito", "Dungeon no Naka no Hito"));
    }

    [Theory]
    [InlineData(PaginaInexistente, AnimeAv1HtmlParser.RespuestaDeDatos.PaginaInexistente)]
    [InlineData("<html><body>Just a moment...</body></html>", AnimeAv1HtmlParser.RespuestaDeDatos.NoEsDeDatos)]
    [InlineData("{\"type\":\"data\",\"nodes\":[]}", AnimeAv1HtmlParser.RespuestaDeDatos.NoEsDeDatos)]
    [InlineData("{\"type\":\"data\",\"nodes\":[null,{\"type\":\"skip\"}]}", AnimeAv1HtmlParser.RespuestaDeDatos.NoEsDeDatos)]
    [InlineData("{ json roto", AnimeAv1HtmlParser.RespuestaDeDatos.NoEsDeDatos)]
    [InlineData(null, AnimeAv1HtmlParser.RespuestaDeDatos.NoEsDeDatos)]
    public void ClasificarDatos_DistingueLaPaginaQueNoExisteDeLoQueNoSonDatos(string? texto, AnimeAv1HtmlParser.RespuestaDeDatos esperado)
    {
        AnimeAv1HtmlParser.ClasificarDatos(texto).Should().Be(esperado);
    }

    [Fact]
    public void DatosMalFormadosDelSitio_NoCuelganNiTumbanLaLectura()
    {
        // Un objeto que se contiene a sí mismo, y listas que se repiten unas a otras (crecerían sin fin al deshacerlas).
        const string ciclo = """{"type":"data","nodes":[{"type":"data","data":[{"media":0,"embeds":1},{"SUB":2},[3],{"server":4,"url":5},"Voe","https://voe.sx/e/abc"]}]}""";
        string bomba = "{\"type\":\"data\",\"nodes\":[{\"type\":\"data\",\"data\":[{\"media\":1}"
            + string.Concat(Enumerable.Range(1, 40).Select(i => $",[{string.Join(",", Enumerable.Repeat(i + 1, 10))}]")) + ",\"fin\"]}]}";
        // Tipos especiales del formato (fechas, mapas): se leen como "sin valor" y lo demás sigue.
        const string conFecha = """{"type":"data","nodes":[{"type":"data","data":[{"media":1},{"malId":2,"updatedAt":3,"title":-1},77,["Date","2026-08-25T00:44:08.811Z"]]}]}""";

        AnimeAv1HtmlParser.ExtraerEmbeds(ciclo).Should().ContainSingle().Which.Server.Should().Be("Voe");
        AnimeAv1HtmlParser.ExtraerMalIdDelMedia(ciclo).Should().BeNull();
        AnimeAv1HtmlParser.ExtraerEpisodiosDelMedia(bomba).Should().BeEmpty();
        AnimeAv1HtmlParser.ExtraerMalIdDelMedia(conFecha).Should().Be(77);
        AnimeAv1HtmlParser.ExtraerTitulosDelMedia(conFecha).Should().BeNull();
    }

    [Fact]
    public async Task ConDatosDelSitio_UnaSolaPeticionPorPagina_YNoSePideElHtml()
    {
        var sitio = new SitioFalso(url => url.EndsWith("/__data.json") ? (HttpStatusCode.OK, EpisodioReal) : (HttpStatusCode.OK, "<html>no debía pedirse</html>"));
        var resolver = new AnimeAv1VideoSourceResolver(new HttpClient(sitio));

        var embeds = await resolver.ObtenerEmbedsDeEpisodioAsync("shingeki-no-kyojin-season-2", 12, malIdEsperado: 25777);

        embeds.Select(e => e.Server).Should().Contain(["MP4Upload", "Voe", "Mega"]);
        sitio.Pedidas.Should().Equal("https://animeav1.com/media/shingeki-no-kyojin-season-2/12/__data.json");
    }

    [Fact]
    public async Task SiElSitioDiceQueLaPaginaNoExiste_NoSePideTambienElHtml()
    {
        var sitio = new SitioFalso(_ => (HttpStatusCode.OK, PaginaInexistente));
        var resolver = new AnimeAv1VideoSourceResolver(new HttpClient(sitio));

        (await resolver.ObtenerEmbedsDeEpisodioAsync("dungeon-meshi", 999, malIdEsperado: null)).Should().BeEmpty();
        (await resolver.ObtenerInfoMediaAsync("no-existe")).Should().BeNull();
        sitio.Pedidas.Should().OnlyContain(url => url.EndsWith("/__data.json"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "bloqueado")]
    [InlineData(HttpStatusCode.OK, "<html><body>Just a moment...</body></html>")]
    public async Task SiElSitioDejaDeDarLosDatos_SeLeeLaPaginaComoAntes(HttpStatusCode estado, string cuerpo)
    {
        const string pagina = """
            <html><body><script>{data: [{type:"data",data:{media:{id:4408,title:"Grand Blue Season 3",slug:"grand-blue-season-3",malId:62542},
            embeds:{SUB:[{server:"MP4Upload",url:"https://www.mp4upload.com/embed-r0xdfbvme2yy.html"}]}}}]}</script></body></html>
            """;
        var sitio = new SitioFalso(url => url.EndsWith("/__data.json") ? (estado, cuerpo) : (HttpStatusCode.OK, pagina));
        var resolver = new AnimeAv1VideoSourceResolver(new HttpClient(sitio));

        var embeds = await resolver.ObtenerEmbedsDeEpisodioAsync("grand-blue-season-3", 9, malIdEsperado: 62542);

        embeds.Should().ContainSingle().Which.Server.Should().Be("MP4Upload");
        sitio.Pedidas.Should().Equal(
            "https://animeav1.com/media/grand-blue-season-3/9/__data.json",
            "https://animeav1.com/media/grand-blue-season-3/9");
    }

    private sealed class SitioFalso(Func<string, (HttpStatusCode Estado, string Cuerpo)> responder) : HttpMessageHandler
    {
        public List<string> Pedidas { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.ToString();
            lock (Pedidas) Pedidas.Add(url);
            var (estado, cuerpo) = responder(url);
            return Task.FromResult(new HttpResponseMessage(estado) { Content = new StringContent(cuerpo) });
        }
    }

    /// <summary>Lo que contesta el sitio para un episodio o un anime que no tiene (capturado el 2026-10-10).</summary>
    private const string PaginaInexistente =
        """{"type":"data","nodes":[null,{"type":"data","data":[{"user":1},null],"uses":{"dependencies":["https://animeav1.com/media/dungeon-meshi/auth"]}},{"type":"error","error":{"message":"No encontrado"},"status":404}]}""";

    /// <summary>https://animeav1.com/media/shingeki-no-kyojin-season-2/12/__data.json, tal cual lo sirvió el sitio el 2026-10-10.</summary>
    private const string EpisodioReal =
        """{"type":"data","nodes":[null,{"type":"data","data":[{"user":1},null],"uses":{"dependencies":["https://animeav1.com/media/shingeki-no-kyojin-season-2/auth"]}},{"type":"data","data":[{"media":1,"episode":101,"embeds":104,"downloads":147},{"id":2,"categoryId":3,"title":4,"aka":5,"genres":9,"synopsis":39,"poster":40,"backdrop":40,"trailer":41,"status":12,"runtime":40,"startDate":42,"nextDate":40,"endDate":43,"waitDays":44,"featured":45,"mature":45,"episodesCount":46,"score":47,"votes":48,"slug":49,"malId":50,"seasons":40,"createdAt":51,"updatedAt":52,"category":53,"episodes":56,"relations":88},172,1,"Shingeki no Kyojin Season 2",{"en-us":6,"ja-jp":7,"es-419":8},"Attack on Titan Season 2","進撃の巨人 Season2","Ataque a los Titanes Temporada 2",[10,14,19,24,29,34],{"id":3,"name":11,"type":12,"slug":13,"malId":3},"Acción",0,"accion",{"id":15,"name":16,"type":12,"slug":17,"malId":18},6,"Drama","drama",8,{"id":20,"name":21,"type":12,"slug":22,"malId":23},15,"Suspenso","suspenso",41,{"id":25,"name":26,"type":12,"slug":27,"malId":28},13,"Shounen","shounen",27,{"id":30,"name":31,"type":3,"slug":32,"malId":33},25,"Gore","gore",58,{"id":35,"name":36,"type":3,"slug":37,"malId":38},37,"Militar","militar",38,"Durante siglos, la humanidad ha sido cazada por gigantescos y misteriosos depredadores conocidos como los Titanes. Tres poderosos muros—el Muro María, el Muro Rosa y el Muro Sheena—brindaron paz y protección a la humanidad durante más de cien años. Sin embargo, esa paz se desmoronó cuando aparecieron el Titán Colosal y el Titán Acorazado, destruyendo el muro más exterior, el Muro María. Obligados a retroceder tras el Muro Rosa, la humanidad esperó con la respiración contenida a que los Titanes reaparecieran y destruyeran su refugio seguro una vez más.\n\nEn la Temporada 2 de Shingeki no Kyojin, Eren Yeager y otros miembros del 104.º Cuerpo de Entrenamiento han comenzado a convertirse en miembros plenos del Cuerpo de Expedición. Mientras se preparan para enfrentarse a los Titanes una vez más, sus preparativos son interrumpidos por la invasión del Muro Rosa, pero no todo es lo que parece, ya que se desvelan más misterios. A medida que el Cuerpo de Expedición se apresura a salvar el muro, descubren más sobre los Titanes invasores y los oscuros secretos de sus propios miembros.",null,"zLaVP8IhIuc","2017-04-01","2017-06-17",7,false,12,8.54,2091116,"shingeki-no-kyojin-season-2",25777,"2025-02-06 07:03:17.725643+00","2026-08-25 01:30:06.891628+00",{"id":3,"name":54,"slug":55},"TV Anime","tv-anime",[57,59,62,65,68,71,73,75,77,80,83,86],{"id":58,"number":3},2361,{"id":60,"number":61},2362,2,{"id":63,"number":64},2363,3,{"id":66,"number":67},2364,4,{"id":69,"number":70},2365,5,{"id":72,"number":15},2366,{"id":74,"number":44},2367,{"id":76,"number":18},2368,{"id":78,"number":79},2369,9,{"id":81,"number":82},2370,10,{"id":84,"number":85},2371,11,{"id":87,"number":46},2372,[89,95],{"type":3,"destination":90},{"id":91,"slug":92,"title":93,"startDate":94},171,"shingeki-no-kyojin","Shingeki no Kyojin","2013-04-07",{"type":61,"destination":96},{"id":97,"slug":98,"title":99,"startDate":100},173,"shingeki-no-kyojin-season-3","Shingeki no Kyojin Season 3","2018-07-23",{"id":87,"mediaId":2,"title":40,"number":46,"season":40,"relativeNumber":40,"variants":102,"filler":45,"publishedAt":40,"createdAt":103,"updatedAt":103,"mirrors":-1},{"DUB":3,"SUB":3},"2025-02-06T07:05:39.295478+00:00",{"DUB":105,"SUB":130},[106,109,112,115,118,121,124,127],{"server":107,"url":108},"HLS","https://player.zilla-networks.com/play/04544c1d9af3e9ecebe9a28b33c6e76f",{"server":110,"url":111},"UPNShare","https://animeav1.uns.bio/#lrlv5t",{"server":113,"url":114},"Voe","https://voe.sx/e/ucx1aeek9aqh",{"server":116,"url":117},"Byse","https://byselapuix.com/e/yr54g959x9br",{"server":119,"url":120},"YourUpload","https://www.yourupload.com/embed/OWPf00hq5MmQ",{"server":122,"url":123},"MP4Upload","https://www.mp4upload.com/embed-4wqdembgn43e.html",{"server":125,"url":126},"VidHide","https://ryderjet.com/embed/qlaklqkqsmzw",{"server":128,"url":129},"StreamTape","https://streamtape.com/e/P8lGWzwBBlS0XMM/",[131,133,135,137,139,141,143,145],{"server":107,"url":132},"https://player.zilla-networks.com/play/f74c18768f9887e66f70e0df538e288e",{"server":110,"url":134},"https://animeav1.uns.bio/#gpgjnf",{"server":113,"url":136},"https://voe.sx/e/yccyzduifysj",{"server":116,"url":138},"https://byselapuix.com/e/vc7ajgpsmxjg",{"server":119,"url":140},"https://www.yourupload.com/embed/IDir56IUY1RB",{"server":122,"url":142},"https://www.mp4upload.com/embed-n1svszacr1fa.html",{"server":125,"url":144},"https://ryderjet.com/embed/zlzb6zq4mm92",{"server":128,"url":146},"https://streamtape.com/e/4PM6JJ9exeHKDJz/",{"SUB":148,"DUB":159},[149,152,155,157],{"server":150,"url":151},"Mega","https://mega.nz/file/BzAyEboD#u2xn0zw8scZXvQZm2__ZwPy1aPGPfHFFcljBLr_53OM",{"server":153,"url":154},"1Fichier","https://1fichier.com/?lbom8nzhbvoztmf96sbl",{"server":122,"url":156},"https://www.mp4upload.com/n1svszacr1fa",{"server":128,"url":158},"https://streamtape.com/v/4PM6JJ9exeHKDJz/",[160,162,164,166],{"server":150,"url":161},"https://mega.nz/file/37R12aJD#1tRAZ-1pwdb2w_gKZ6U-j67yK3P8CVO80e2PVvyKgmg",{"server":153,"url":163},"https://1fichier.com/?n05zimkarl5lr8nio9fw",{"server":122,"url":165},"https://www.mp4upload.com/4wqdembgn43e",{"server":128,"url":167},"https://streamtape.com/v/P8lGWzwBBlS0XMM/"],"uses":{"params":["number","slug"]}}]}""";
}
