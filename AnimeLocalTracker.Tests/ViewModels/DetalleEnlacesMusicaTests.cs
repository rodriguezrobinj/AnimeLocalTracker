using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.EnlacesMusica;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>Enlaces externos del panel de música de la ficha (AniPlaylist): se cargan al abrir el anime y se abren en el navegador.</summary>
public class DetalleEnlacesMusicaTests
{
    private readonly Mock<IAnimeTrackingService> _tracking = new();
    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<IFileScannerService> _escaner = new();
    private readonly Mock<IDialogService> _dialogos = new();
    private readonly Mock<IDownloadService> _descargas = new();
    private readonly Mock<IEnlacesMusicaService> _enlaces = new();

    private static readonly EnlaceMusica AniPlaylist = new("aniplaylist", "AniPlaylist", "https://aniplaylist.com/Frieren", "Det_MusicaAniPlaylistTip");

    private DetalleViewModel CrearSut(bool conServicio = true) => new(
        _tracking.Object, _db.Object, Mock.Of<IAuthService>(), _escaner.Object, _dialogos.Object, _descargas.Object,
        enlacesMusica: conServicio ? _enlaces.Object : null);

    private async Task<DetalleViewModel> AbrirFichaAsync(DetalleViewModel sut, int aniListId = 7, string titulo = "Frieren")
    {
        _escaner.Setup(e => e.EscanearEpisodiosAsync(It.IsAny<string>())).ReturnsAsync(new List<EpisodioItem>());
        _db.Setup(d => d.ObtenerRegistrosPorAnimeAsync(It.IsAny<int>())).ReturnsAsync(new List<RegistroEpisodio>());
        double p = 0;
        _descargas.Setup(d => d.EstaDescargando(It.IsAny<int>(), It.IsAny<int>(), out p)).Returns(false);

        await sut.InicializarAsync(new AnimeItem { AniListId = aniListId, Titulo = titulo });
        return sut;
    }

    [Fact]
    public async Task AlAbrirLaFicha_CargaLosEnlacesDeLosProveedores()
    {
        _enlaces.Setup(s => s.ObtenerEnlaces(It.Is<AnimeItem>(a => a.AniListId == 7))).Returns([AniPlaylist]);

        var sut = await AbrirFichaAsync(CrearSut());

        sut.EnlacesMusica.Should().ContainSingle().Which.Nombre.Should().Be("AniPlaylist");
        sut.EnlacesMusica[0].Url.Should().Be("https://aniplaylist.com/Frieren");
        sut.TieneEnlacesMusica.Should().BeTrue();
    }

    [Fact]
    public async Task SinServicioDeEnlaces_LaFichaFuncionaSinEnlaces()
    {
        var sut = await AbrirFichaAsync(CrearSut(conServicio: false));

        sut.EnlacesMusica.Should().BeEmpty();
        sut.TieneEnlacesMusica.Should().BeFalse();
    }

    [Fact]
    public async Task SiNingunProveedorTieneEnlace_NoSeMuestraNada()
    {
        _enlaces.Setup(s => s.ObtenerEnlaces(It.IsAny<AnimeItem>())).Returns([]);

        var sut = await AbrirFichaAsync(CrearSut());

        sut.TieneEnlacesMusica.Should().BeFalse();
    }

    [Fact]
    public async Task AlCambiarDeAnime_LosEnlacesSonLosDelNuevo_NoSeAcumulan()
    {
        _enlaces.Setup(s => s.ObtenerEnlaces(It.IsAny<AnimeItem>())).Returns((AnimeItem a) =>
            [new EnlaceMusica("aniplaylist", "AniPlaylist", $"https://aniplaylist.com/{a.Titulo}", "Det_MusicaAniPlaylistTip")]);
        var sut = CrearSut();

        await AbrirFichaAsync(sut, 7, "Frieren");
        await AbrirFichaAsync(sut, 8, "Bleach");

        sut.EnlacesMusica.Should().ContainSingle().Which.Url.Should().EndWith("/Bleach");
    }

    [Fact]
    public async Task AbrirEnlace_LoAbreEnElNavegador_SinAvisosSiSaleBien()
    {
        _enlaces.Setup(s => s.ObtenerEnlaces(It.IsAny<AnimeItem>())).Returns([AniPlaylist]);
        _enlaces.Setup(s => s.Abrir(AniPlaylist)).Returns(true);
        var sut = await AbrirFichaAsync(CrearSut());

        sut.AbrirEnlaceMusicaCommand.Execute(sut.EnlacesMusica[0]);

        _enlaces.Verify(s => s.Abrir(AniPlaylist), Times.Once);
        _dialogos.Verify(d => d.MostrarToast(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task AbrirEnlace_SiNoSePuedeAbrir_AvisaConUnToastConElNombre()
    {
        _enlaces.Setup(s => s.ObtenerEnlaces(It.IsAny<AnimeItem>())).Returns([AniPlaylist]);
        _enlaces.Setup(s => s.Abrir(It.IsAny<EnlaceMusica>())).Returns(false);
        var sut = await AbrirFichaAsync(CrearSut());

        sut.AbrirEnlaceMusicaCommand.Execute(sut.EnlacesMusica[0]);

        _dialogos.Verify(d => d.MostrarToast(It.IsAny<string>(), It.Is<string>(m => m.Contains("AniPlaylist")), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public void AbrirEnlace_ConNuloOSinServicio_NoHaceNada()
    {
        var sut = CrearSut(conServicio: false);

        sut.AbrirEnlaceMusicaCommand.Execute(null);
        sut.AbrirEnlaceMusicaCommand.Execute(new EnlaceMusicaItem(AniPlaylist));

        _dialogos.Verify(d => d.MostrarToast(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void Item_TraduceLaDescripcionYNoMuestraLaClave()
    {
        var item = new EnlaceMusicaItem(AniPlaylist);

        item.Descripcion.Should().NotBe("Det_MusicaAniPlaylistTip").And.Contain("Spotify");
        item.Url.Should().Be(AniPlaylist.Url);
    }

    [Fact]
    public void Item_AlCambiarElIdioma_AvisaQueLaDescripcionCambio()
    {
        var item = new EnlaceMusicaItem(AniPlaylist);
        var avisadas = new List<string?>();
        item.PropertyChanged += (_, e) => avisadas.Add(e.PropertyName);

        item.Receive(new AnimeLocalTracker.Messages.IdiomaCambiadoMensaje());

        avisadas.Should().Contain(nameof(EnlaceMusicaItem.Descripcion));
    }
}
