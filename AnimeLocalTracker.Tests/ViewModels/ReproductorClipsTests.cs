using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Clips;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

public class ReproductorClipsTests
{
    private readonly Mock<IClipService> _clips = new();

    private ReproductorViewModel Reproductor()
    {
        _clips.SetupGet(c => c.Carpeta).Returns(@"C:\Videos\AnimeLocalTracker");
        var sut = new ReproductorViewModel(Mock.Of<IDatabaseService>(), Mock.Of<IAnimeTrackingService>(), Mock.Of<IAuthService>(), clipService: _clips.Object);
        sut.CargarVideo(@"C:\Anime\Frieren\Episodio 14.mkv", 101, "Frieren", 14);
        sut.TotalSeconds = 1440;
        return sut;
    }

    private static void Marcar(ReproductorViewModel sut, double inicio, double fin)
    {
        sut.CurrentSeconds = inicio;
        sut.MarcarInicioClipCommand.Execute(null);
        sut.CurrentSeconds = fin;
        sut.MarcarFinClipCommand.Execute(null);
    }

    [Fact]
    public void MarcarInicioYFin_MuestraElPanelConElTramo()
    {
        var sut = Reproductor();

        Marcar(sut, 725, 751);

        sut.MostrarPanelClip.Should().BeTrue();
        sut.Mp4Disponible.Should().BeTrue();
        sut.GifDisponible.Should().BeFalse("26 s pasan del máximo de 10 s del GIF");
        sut.TextoRangoClip.Should().Contain("12:05").And.Contain("12:31");
    }

    [Fact]
    public void TramoCorto_AdmiteGif()
    {
        var sut = Reproductor();

        Marcar(sut, 725, 733);

        sut.GifDisponible.Should().BeTrue();
    }

    [Fact]
    public void SoloInicio_PanelSinBotonesDeCrear()
    {
        var sut = Reproductor();
        sut.CurrentSeconds = 725;

        sut.MarcarInicioClipCommand.Execute(null);

        sut.MostrarPanelClip.Should().BeTrue();
        sut.Mp4Disponible.Should().BeFalse();
        sut.GifDisponible.Should().BeFalse();
    }

    [Fact]
    public async Task CrearMp4_PideElTramoConNombreYCierraElPanel()
    {
        var sut = Reproductor();
        Marcar(sut, 725, 751);

        await sut.CrearClipMp4Command.ExecuteAsync(null);

        _clips.Verify(c => c.CrearAsync(It.Is<SolicitudClip>(s =>
            s.RutaVideo == @"C:\Anime\Frieren\Episodio 14.mkv" && s.InicioSeg == 725 && s.DuracionSeg == 26 && s.Formato == FormatoClip.Mp4
            && s.RutaSalida == Path.Combine(@"C:\Videos\AnimeLocalTracker", "Frieren - Ep 14 - 12-05.mp4")), It.IsAny<CancellationToken>()), Times.Once);
        sut.MostrarPanelClip.Should().BeFalse();
    }

    [Fact]
    public void Descartar_OcultaElPanel()
    {
        var sut = Reproductor();
        Marcar(sut, 725, 751);

        sut.DescartarClipCommand.Execute(null);

        sut.MostrarPanelClip.Should().BeFalse();
    }
}
