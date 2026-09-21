using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>Etiquetas con datos de AniList: caché semanal local, conversión y enlace seguro al tráiler.</summary>
public class DatosExtraServiceTests
{
    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<IAnimeTrackingService> _tracking = new();

    private DatosExtraService CrearSut() => new(_db.Object, _tracking.Object);

    private static AniListMedia Media() => new()
    {
        AverageScore = 82, Format = "TV", Duration = 24, Source = "LIGHT_NOVEL",
        Studios = new AniListStudios { Nodes = [new AniListStudio { Name = " " }, new AniListStudio { Name = "Studio Comet" }] },
        Trailer = new AniListTrailer { Id = "abc123", Site = "youtube" }
    };

    [Fact]
    public async Task ConCopiaDeEstaSemana_NoConsultaAniList()
    {
        _db.Setup(d => d.ObtenerDatosExtraAsync(5)).ReturnsAsync(new DatosExtraAnime { AniListId = 5, NotaMedia = 70, ConsultadoUtc = DateTime.UtcNow.AddDays(-3) });

        var datos = await CrearSut().ObtenerAsync(5);

        datos!.NotaMedia.Should().Be(70);
        _tracking.Verify(t => t.ObtenerDatosExtraAsync(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task SinCopia_ConsultaConvierteYGuarda()
    {
        _db.Setup(d => d.ObtenerDatosExtraAsync(5)).ReturnsAsync((DatosExtraAnime?)null);
        _tracking.Setup(t => t.ObtenerDatosExtraAsync(5)).ReturnsAsync((true, Media()));

        var datos = await CrearSut().ObtenerAsync(5);

        datos!.NotaMedia.Should().Be(82);
        datos.Formato.Should().Be("TV");
        datos.DuracionMin.Should().Be(24);
        datos.Estudio.Should().Be("Studio Comet", "se salta los nombres vacíos");
        datos.Fuente.Should().Be("LIGHT_NOVEL");
        _db.Verify(d => d.GuardarDatosExtraAsync(It.Is<DatosExtraAnime>(x => x.AniListId == 5 && x.NotaMedia == 82)), Times.Once);
    }

    [Fact]
    public async Task ConCopiaDeMasDeUnaSemana_LaRefresca()
    {
        _db.Setup(d => d.ObtenerDatosExtraAsync(5)).ReturnsAsync(new DatosExtraAnime { AniListId = 5, NotaMedia = 60, ConsultadoUtc = DateTime.UtcNow.AddDays(-8) });
        _tracking.Setup(t => t.ObtenerDatosExtraAsync(5)).ReturnsAsync((true, Media()));

        (await CrearSut().ObtenerAsync(5))!.NotaMedia.Should().Be(82);
    }

    [Fact]
    public async Task SiFallaLaRed_ConservaLaCopiaVieja_YNoInsisteEnCadaVisita()
    {
        _db.Setup(d => d.ObtenerDatosExtraAsync(5)).ReturnsAsync(new DatosExtraAnime { AniListId = 5, NotaMedia = 60, ConsultadoUtc = DateTime.UtcNow.AddDays(-8) });
        _tracking.Setup(t => t.ObtenerDatosExtraAsync(5)).ReturnsAsync((false, (AniListMedia?)null));

        var datos = await CrearSut().ObtenerAsync(5);

        datos!.NotaMedia.Should().Be(60);
        DatosExtraService.DebeConsultar(datos, DateTime.UtcNow).Should().BeFalse("el fallo se anota y se reintenta pasadas unas horas");
        DatosExtraService.DebeConsultar(datos, DateTime.UtcNow.AddHours(7)).Should().BeTrue();
    }

    [Fact]
    public async Task SinCopiaYSinRed_DevuelveNulo()
    {
        _db.Setup(d => d.ObtenerDatosExtraAsync(5)).ReturnsAsync((DatosExtraAnime?)null);
        _tracking.Setup(t => t.ObtenerDatosExtraAsync(5)).ReturnsAsync((false, (AniListMedia?)null));

        (await CrearSut().ObtenerAsync(5)).Should().BeNull();
    }

    [Fact]
    public void ConsultadoUtcSinKind_SeTrataComoUtc()
    {
        var hace8Dias = DateTime.SpecifyKind(DateTime.UtcNow.AddDays(-8), DateTimeKind.Unspecified);
        DatosExtraService.DebeConsultar(new DatosExtraAnime { ConsultadoUtc = hace8Dias }, DateTime.UtcNow).Should().BeTrue();
    }

    // ── Enlace del tráiler ──

    [Theory]
    [InlineData("youtube", "abc123", "https://www.youtube.com/watch?v=abc123")]
    [InlineData("YouTube", "abc123", "https://www.youtube.com/watch?v=abc123")]
    [InlineData("dailymotion", "x8abc", "https://www.dailymotion.com/video/x8abc")]
    public void Trailer_SitiosConocidos_DevuelvenUnaUrlHttps(string sitio, string id, string esperado)
    {
        DatosExtraService.UrlTrailer(new DatosExtraAnime { TrailerSitio = sitio, TrailerId = id }).Should().Be(esperado);
    }

    [Theory]
    [InlineData("otro-sitio", "abc")]
    [InlineData("youtube", "")]
    [InlineData("", "abc")]
    public void Trailer_SitioDesconocidoOSinId_NoHayEnlace(string sitio, string id)
    {
        DatosExtraService.UrlTrailer(new DatosExtraAnime { TrailerSitio = sitio, TrailerId = id }).Should().BeNull();
        DatosExtraService.UrlTrailer(null).Should().BeNull();
    }

    [Fact]
    public void Trailer_ElIdentificadorSeEscapa_NoSePuedeInyectarNadaEnLaUrl()
    {
        // El id viene de un servicio externo: nunca debe poder cambiar el destino del enlace.
        var url = DatosExtraService.UrlTrailer(new DatosExtraAnime { TrailerSitio = "youtube", TrailerId = "a&redirect=http://malo.example/x y" });

        url.Should().StartWith("https://www.youtube.com/watch?v=");
        url.Should().NotContain("&redirect").And.NotContain(" ");
    }
}
