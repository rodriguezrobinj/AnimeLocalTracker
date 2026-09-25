using System.Linq;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// INT-01: contrato tipado del scraping de animeav1.com — fixtures de HTML realista
/// (enlaces /media/, JSON slug:"...", embeds de MP4Upload y player.src) para que un
/// cambio del sitio rompa los tests y no en producción.
/// </summary>
public class AnimeAv1HtmlParserTests
{
    // CA1861: arrays constantes reutilizados como campos estáticos
    private static readonly string[] SlugsEsperados = { "one-piece-1085", "one-piece-film-red", "jujutsu-kaisen-season-2" };

    [Fact]
    public void ExtraerSlugs_ConEnlacesMediaYJson_DeberiaDevolverSlugsUnicos()
    {
        // Arrange: HTML de catálogo con enlaces /media/ y JSON slug:"..."
        string html = """
            <a href="/media/one-piece-1085">One Piece</a>
            <a href="/media/one-piece-film-red">One Piece Film Red</a>
            <script>var anime = { slug: "jujutsu-kaisen-season-2", type: "ANIME" };</script>
            <a href="/media/catalogo">catálogo</a>
            """;

        // Act
        var slugs = AnimeAv1HtmlParser.ExtraerSlugs(html).ToList();

        // Assert: únicos, sin comillas y sin "catalogo"
        slugs.Should().Contain(SlugsEsperados);
        slugs.Should().NotContain("catalogo");
        slugs.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void ExtraerSlugs_ConHtmlVacioONulo_DeberiaDevolverVacio()
    {
        AnimeAv1HtmlParser.ExtraerSlugs(string.Empty).Should().BeEmpty();
        AnimeAv1HtmlParser.ExtraerSlugs(null!).Should().BeEmpty();
    }

    [Fact]
    public void ExtraerMp4UploadId_ConEmbed_DeberiaExtraerElId()
    {
        // Arrange: página de episodio con embed de MP4Upload
        string html = """
            <div class="video-player">
              <iframe src="https://www.mp4upload.com/embed-abc123xyz.html" allowfullscreen></iframe>
            </div>
            <a href="https://www.mp4upload.com/embed-abc123xyz.html">Descargar</a>
            """;

        // Act & Assert
        AnimeAv1HtmlParser.ExtraerMp4UploadId(html).Should().Be("abc123xyz");
    }

    [Fact]
    public void ExtraerMp4UploadId_SinEmbed_DeberiaDevolverNull()
    {
        AnimeAv1HtmlParser.ExtraerMp4UploadId("<html><body>sin embed</body></html>").Should().BeNull();
    }

    [Fact]
    public void ExtraerVideoDirecto_ConPlayerSrc_DeberiaExtraerLaUrlMp4()
    {
        // Arrange: página de MP4Upload con player.src apuntando al archivo directo
        string html = """
            <script>
              var config = { src: "https://cdn.mp4upload.com/abc123xyz/720p/video.mp4", type: "video/mp4" };
              player.setup(config);
            </script>
            """;

        // Act & Assert
        AnimeAv1HtmlParser.ExtraerVideoDirecto(html).Should().Be("https://cdn.mp4upload.com/abc123xyz/720p/video.mp4");
    }

    [Fact]
    public void ExtraerVideoDirecto_ConUrlMkv_DeberiaExtraerLaUrl()
    {
        string html = """
            <script>jwplayer().setup({ src: "https://cdn.example.com/episodio.mkv?v=2" });</script>
            """;

        AnimeAv1HtmlParser.ExtraerVideoDirecto(html).Should().Be("https://cdn.example.com/episodio.mkv?v=2");
    }

    [Fact]
    public void ExtraerVideoDirecto_SinCoincidencia_DeberiaDevolverNull()
    {
        AnimeAv1HtmlParser.ExtraerVideoDirecto("<html>sin player</html>").Should().BeNull();
    }

    // Fixture real (confirmada contra animeav1.com): el sitio publica dos pistas de audio,
    // cada una con sus propios servidores.
    private const string HtmlConDosPistasDeAudio = """
        <script>{__sveltekit={data:[{type:"data",data:{
        embeds:{SUB:[
          {server:"UPNShare",url:"https://animeav1.uns.bio/#6uekra"},
          {server:"Voe",url:"https://voe.sx/e/fm2zgn6plvtt"},
          {server:"Byse",url:"https://byselapuix.com/e/yzkkh96luuwc"},
          {server:"MP4Upload",url:"https://www.mp4upload.com/embed-mxk0txlex6iz.html"}
        ],DUB:[
          {server:"UPNShare",url:"https://animeav1.uns.bio/#hrpd8a"},
          {server:"Voe",url:"https://voe.sx/e/ck6esmuuv5kc"},
          {server:"Byse",url:"https://byselapuix.com/e/ml3gobpkkhe6"},
          {server:"MP4Upload",url:"https://www.mp4upload.com/embed-p8ryayejs9d0.html"}
        ]}}}]}}</script>
        """;

    [Fact]
    public void ExtraerEmbeds_ConDosPistasDeAudio_DeberiaEtiquetarCadaServidorConSuPista()
    {
        var embeds = AnimeAv1HtmlParser.ExtraerEmbeds(HtmlConDosPistasDeAudio);

        embeds.Should().HaveCount(8);
        embeds.Count(e => e.Audio == "SUB").Should().Be(4);
        embeds.Count(e => e.Audio == "DUB").Should().Be(4);

        var mp4Sub = embeds.Single(e => e.Audio == "SUB" && e.Server == "MP4Upload");
        mp4Sub.Url.Should().Be("https://www.mp4upload.com/embed-mxk0txlex6iz.html");

        var mp4Dub = embeds.Single(e => e.Audio == "DUB" && e.Server == "MP4Upload");
        mp4Dub.Url.Should().Be("https://www.mp4upload.com/embed-p8ryayejs9d0.html");
    }

    [Fact]
    public void ExtraerEmbeds_ConUnaSolaPista_DeberiaEtiquetarlaIgual()
    {
        // Algunas páginas (películas, OVAs) solo publican una pista.
        const string html = """
            <script>embeds:{SUB:[{server:"MP4Upload",url:"https://www.mp4upload.com/embed-abc.html"}]}</script>
            """;

        var embeds = AnimeAv1HtmlParser.ExtraerEmbeds(html);

        embeds.Should().ContainSingle();
        embeds[0].Audio.Should().Be("SUB");
    }

    [Fact]
    public void OrdenarEmbedsPorPreferencia_SinPreferencia_DeberiaOrdenarSoloPorServidor()
    {
        var embeds = AnimeAv1HtmlParser.ExtraerEmbeds(HtmlConDosPistasDeAudio);

        var ordenados = AnimeAv1HtmlParser.OrdenarEmbedsPorPreferencia(embeds);

        // MP4Upload primero (server de mayor preferencia) — SUB antes que DUB porque el sitio
        // lo lista primero y no hay preferencia de audio que rompa el empate.
        ordenados[0].Server.Should().Be("MP4Upload");
        ordenados[0].Audio.Should().Be("SUB");
        ordenados[1].Server.Should().Be("MP4Upload");
        ordenados[1].Audio.Should().Be("DUB");
    }

    [Fact]
    public void OrdenarEmbedsPorPreferencia_ConPreferenciaDub_DeberiaProbarPrimeroLosServidoresDub()
    {
        var embeds = AnimeAv1HtmlParser.ExtraerEmbeds(HtmlConDosPistasDeAudio);

        var ordenados = AnimeAv1HtmlParser.OrdenarEmbedsPorPreferencia(embeds, "DUB");

        // Los 4 servidores DUB (en orden de servidor) van antes que cualquier SUB.
        ordenados.Take(4).Should().OnlyContain(e => e.Audio == "DUB");
        ordenados[0].Server.Should().Be("MP4Upload");
        ordenados.Skip(4).Should().OnlyContain(e => e.Audio == "SUB");
    }

    [Fact]
    public void OrdenarEmbedsPorPreferencia_PistaPedidaSinServidores_DeberiaCaerALaOtra()
    {
        // Solo hay SUB; se pide DUB. No debe quedarse sin nada: cae al SUB disponible.
        const string html = """
            <script>embeds:{SUB:[{server:"MP4Upload",url:"https://www.mp4upload.com/embed-abc.html"}]}</script>
            """;
        var embeds = AnimeAv1HtmlParser.ExtraerEmbeds(html);

        var ordenados = AnimeAv1HtmlParser.OrdenarEmbedsPorPreferencia(embeds, "DUB");

        ordenados.Should().ContainSingle();
        ordenados[0].Audio.Should().Be("SUB");
    }
}
