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
}
