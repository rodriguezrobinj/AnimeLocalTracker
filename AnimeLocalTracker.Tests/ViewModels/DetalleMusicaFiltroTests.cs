using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>
/// Filtro Todos/Openings/Endings de la ventana de música: con un anime como One Piece (decenas de temas) separa la lista
/// para no tener que buscar a ojo cuáles son openings y cuáles endings. El filtro solo cambia qué se ve, nunca qué suena.
/// </summary>
public class DetalleMusicaFiltroTests
{
    private readonly Mock<IAnimeTrackingService> _tracking = new();
    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<IFileScannerService> _escaner = new();
    private readonly Mock<IDialogService> _dialogos = new();
    private readonly Mock<IDownloadService> _descargas = new();
    private readonly Mock<IAnimeThemesService> _themesService = new();
    private readonly Mock<IAnimeThemesDownloadService> _themesDownload = new();

    private DetalleViewModel CrearSut() => new(
        _tracking.Object, _db.Object, Mock.Of<IAuthService>(), _escaner.Object, _dialogos.Object, _descargas.Object,
        animeThemesService: _themesService.Object, animeThemesDownload: _themesDownload.Object);

    private static AnimeThemeInfo Tema(string tipo, string slug) => new() { Slug = slug, Tipo = tipo, AudioUrlOgg = $"https://a.animethemes.moe/{slug}.ogg" };

    private async Task<DetalleViewModel> AbrirFichaConTemasAsync(int aniListId, params AnimeThemeInfo[] temas)
    {
        var anime = new AnimeItem { AniListId = aniListId, Titulo = "One Piece" };
        _escaner.Setup(e => e.EscanearEpisodiosAsync(It.IsAny<string>())).ReturnsAsync(new List<EpisodioItem>());
        _db.Setup(d => d.ObtenerRegistrosPorAnimeAsync(It.IsAny<int>())).ReturnsAsync(new List<RegistroEpisodio>());
        double p = 0;
        _descargas.Setup(d => d.EstaDescargando(It.IsAny<int>(), It.IsAny<int>(), out p)).Returns(false);
        _themesService.Setup(s => s.ObtenerTemasAsync(aniListId, It.IsAny<CancellationToken>())).ReturnsAsync(temas.ToList());

        var sut = CrearSut();
        await sut.InicializarAsync(anime);
        await sut.Musica.CargarTemasMusicalesAsync();
        return sut;
    }

    [Fact]
    public async Task ConOpeningsYEndings_MuestraElFiltro()
    {
        var sut = await AbrirFichaConTemasAsync(21, Tema("OP", "OP1"), Tema("ED", "ED1"));

        sut.Musica.MostrarFiltroTemasMusicales.Should().BeTrue();
    }

    [Fact]
    public async Task ConSoloOpenings_NoMuestraElFiltro()
    {
        var sut = await AbrirFichaConTemasAsync(21, Tema("OP", "OP1"), Tema("OP", "OP2"));

        sut.Musica.MostrarFiltroTemasMusicales.Should().BeFalse();
    }

    [Fact]
    public async Task PorDefecto_MuestraTodosLosTemas()
    {
        var sut = await AbrirFichaConTemasAsync(21, Tema("OP", "OP1"), Tema("ED", "ED1"), Tema("ED", "ED2"));

        sut.Musica.EsFiltroTemasTodos.Should().BeTrue();
        sut.Musica.TemasMusicalesVisibles.Should().HaveCount(3);
    }

    [Fact]
    public async Task FiltrarOpenings_SoloDejaVerLosOpenings()
    {
        var sut = await AbrirFichaConTemasAsync(21, Tema("OP", "OP1"), Tema("OP", "OP2"), Tema("ED", "ED1"));

        sut.Musica.CambiarFiltroTemasCommand.Execute("Openings");

        sut.Musica.EsFiltroTemasOpenings.Should().BeTrue();
        sut.Musica.EsFiltroTemasTodos.Should().BeFalse();
        sut.Musica.TemasMusicalesVisibles.Select(t => t.Slug).Should().Equal("OP1", "OP2");
    }

    [Fact]
    public async Task FiltrarEndings_SoloDejaVerLosEndings()
    {
        var sut = await AbrirFichaConTemasAsync(21, Tema("OP", "OP1"), Tema("ED", "ED1"), Tema("ED", "ED2"));

        sut.Musica.CambiarFiltroTemasCommand.Execute("Endings");

        sut.Musica.TemasMusicalesVisibles.Select(t => t.Slug).Should().Equal("ED1", "ED2");
    }

    [Fact]
    public async Task VolverATodos_MuestraDeNuevoTodosLosTemas()
    {
        var sut = await AbrirFichaConTemasAsync(21, Tema("OP", "OP1"), Tema("ED", "ED1"));
        sut.Musica.CambiarFiltroTemasCommand.Execute("Endings");

        sut.Musica.CambiarFiltroTemasCommand.Execute("Todos");

        sut.Musica.TemasMusicalesVisibles.Should().HaveCount(2);
    }

    [Fact]
    public async Task ElFiltro_NoAfectaALaListaCompletaQueUsaLaReproduccionContinua()
    {
        var sut = await AbrirFichaConTemasAsync(21, Tema("OP", "OP1"), Tema("ED", "ED1"), Tema("ED", "ED2"));

        sut.Musica.CambiarFiltroTemasCommand.Execute("Openings");

        sut.Musica.TemasMusicales.Should().HaveCount(3, "la lista completa (de la que sale 'siguiente que se pueda escuchar') no depende del filtro");
    }

    [Fact]
    public async Task AlCambiarDeFicha_ElFiltroVuelveATodos()
    {
        var sut = await AbrirFichaConTemasAsync(21, Tema("OP", "OP1"), Tema("ED", "ED1"));
        sut.Musica.CambiarFiltroTemasCommand.Execute("Endings");

        var otro = new AnimeItem { AniListId = 22, Titulo = "Otro anime" };
        _themesService.Setup(s => s.ObtenerTemasAsync(22, It.IsAny<CancellationToken>())).ReturnsAsync(new List<AnimeThemeInfo> { Tema("OP", "OP1") });
        await sut.InicializarAsync(otro);
        await sut.Musica.CargarTemasMusicalesAsync();

        sut.Musica.EsFiltroTemasTodos.Should().BeTrue();
        sut.Musica.TemasMusicalesVisibles.Should().ContainSingle();
    }

    [Fact]
    public void SinTemas_NoMuestraElFiltroYLaListaVisibleEstaVacia()
    {
        var sut = CrearSut();

        sut.Musica.MostrarFiltroTemasMusicales.Should().BeFalse();
        sut.Musica.TemasMusicalesVisibles.Should().BeEmpty();
    }
}
