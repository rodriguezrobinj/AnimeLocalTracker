using System.Collections.Generic;
using System.Threading.Tasks;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

public class DetalleViewModelTests
{
    private readonly Mock<IAnimeTrackingService> _trackingServiceMock = new();
    private readonly Mock<IDatabaseService> _databaseServiceMock = new();
    private readonly Mock<IAuthService> _authServiceMock = new();
    private readonly Mock<IFileScannerService> _fileScannerServiceMock = new();
    private readonly Mock<IDialogService> _dialogServiceMock = new();
    private readonly Mock<IDownloadService> _downloadServiceMock = new();

    private DetalleViewModel CreateSut()
    {
        return new DetalleViewModel(
            _trackingServiceMock.Object,
            _databaseServiceMock.Object,
            _authServiceMock.Object,
            _fileScannerServiceMock.Object,
            _dialogServiceMock.Object,
            _downloadServiceMock.Object
        );
    }

    [Fact]
    public async Task InicializarAsync_DeberiaCargarEpisodiosYRegistrosCorrectamente()
    {
        // Arrange
        var anime = new AnimeItem
        {
            AniListId = 100,
            Titulo = "Attack on Titan",
            TotalEpisodios = 3,
            RutaCarpeta = "C:\\Anime\\AOT"
        };

        var episodiosLocales = new List<EpisodioItem>
        {
            new() { NumeroEpisodio = 1, RutaCompleta = "C:\\Anime\\AOT\\ep1.mkv", TituloArchivo = "ep1" },
            new() { NumeroEpisodio = 2, RutaCompleta = "C:\\Anime\\AOT\\ep2.mkv", TituloArchivo = "ep2" }
        };

        var registrosBd = new List<RegistroEpisodio>
        {
            new() { AniListId = 100, NumeroEpisodio = 1, VistoLocal = true, FavoritoLocal = true }
        };

        _fileScannerServiceMock
            .Setup(s => s.EscanearEpisodiosAsync(anime.RutaCarpeta))
            .ReturnsAsync(episodiosLocales);

        _databaseServiceMock
            .Setup(s => s.ObtenerRegistrosPorAnimeAsync(anime.AniListId))
            .ReturnsAsync(registrosBd);

        double progress = 0;
        _downloadServiceMock
            .Setup(s => s.EstaDescargando(100, It.IsAny<int>(), out progress))
            .Returns(false);

        var sut = CreateSut();

        // Act
        await sut.InicializarAsync(anime);

        // Assert
        sut.AnimeSeleccionado.Should().Be(anime);
        sut.EpisodiosDelAnime.Should().HaveCount(3);
        
        var ep1 = sut.EpisodiosDelAnime[2]; // Orden descendente por defecto: ep3, ep2, ep1
        var ep3 = sut.EpisodiosDelAnime[0];

        ep1.NumeroEpisodio.Should().Be(1);
        ep1.Descargado.Should().BeTrue();
        ep1.Visto.Should().BeTrue();
        ep1.Favorito.Should().BeTrue();

        ep3.NumeroEpisodio.Should().Be(3);
        ep3.Descargado.Should().BeFalse();
        ep3.Visto.Should().BeFalse();
    }

    [Fact]
    public async Task MarcarVistosCommand_DeberiaGuardarEnLote()
    {
        // Arrange
        var anime = new AnimeItem { AniListId = 200, Titulo = "Naruto", TotalEpisodios = 2 };
        var sut = CreateSut();
        sut.AnimeSeleccionado = anime;

        var ep1 = new EpisodioItem { NumeroEpisodio = 1, Visto = false };
        var ep2 = new EpisodioItem { NumeroEpisodio = 2, Visto = false };
        var seleccionados = new List<EpisodioItem> { ep1, ep2 };

        // Act
        await sut.MarcarVistosCommand.ExecuteAsync(seleccionados);

        // Assert
        ep1.Visto.Should().BeTrue();
        ep2.Visto.Should().BeTrue();

        _databaseServiceMock.Verify(d => d.GuardarRegistrosEpisodioBulkAsync(
            It.Is<IEnumerable<RegistroEpisodio>>(r => System.Linq.Enumerable.Count(r) == 2)), Times.Once);
    }

    [Fact]
    public async Task AlternarVistoEpisodioCommand_ConEpisodioAMedioVer_DeberiaLimpiarElProgresoYaEnMemoria()
    {
        // Arrange: episodio a medio ver (barra de progreso visible). EpisodioItem.Visto no
        // notificaba TieneProgresoGuardado, así que la barra no desaparecía hasta recargar Detalle.
        var anime = new AnimeItem { AniListId = 300, Titulo = "Bleach", TotalEpisodios = 5 };
        var sut = CreateSut();
        sut.AnimeSeleccionado = anime;

        var episodio = new EpisodioItem { NumeroEpisodio = 3, Visto = false, ProgresoSegundos = 600, TotalSegundos = 1200 };
        episodio.TieneProgresoGuardado.Should().BeTrue();

        // Act: marcar como visto
        await sut.AlternarVistoEpisodioCommand.ExecuteAsync(episodio);

        // Assert: visto y sin progreso guardado, en memoria, sin releer de la BD.
        episodio.Visto.Should().BeTrue();
        episodio.ProgresoSegundos.Should().Be(0);
        episodio.TieneProgresoGuardado.Should().BeFalse();

        // Act: alternar de nuevo a no visto (no debe resucitar el progreso viejo)
        await sut.AlternarVistoEpisodioCommand.ExecuteAsync(episodio);

        // Assert
        episodio.Visto.Should().BeFalse();
        episodio.TieneProgresoGuardado.Should().BeFalse();
    }

    [Fact]
    public void Receive_UsuarioLogeadoMensaje_DeberiaActualizarEstaConectado()
    {
        // Arrange
        _authServiceMock.Setup(a => a.EstaAutenticado()).Returns(false);
        var sut = CreateSut();
        sut.EstaConectado.Should().BeFalse();

        // Act
        sut.Receive(new UsuarioLogeadoMensaje());

        // Assert
        sut.EstaConectado.Should().BeTrue();
    }

    [Fact]
    public async Task MarcarAnterioresVistosCommand_DeberiaMarcarEpisodiosAnterioresOIguales()
    {
        // Arrange
        var anime = new AnimeItem { AniListId = 300, Titulo = "One Piece", TotalEpisodios = 5 };
        var sut = CreateSut();

        _fileScannerServiceMock
            .Setup(s => s.EscanearEpisodiosAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<EpisodioItem>());
        _databaseServiceMock
            .Setup(s => s.ObtenerRegistrosPorAnimeAsync(300))
            .ReturnsAsync(new List<RegistroEpisodio>());

        await sut.InicializarAsync(anime);

        // Act: marcar hasta el episodio 3
        var ep3 = sut.EpisodiosDelAnime.First(e => e.NumeroEpisodio == 3);
        await sut.MarcarAnterioresVistosCommand.ExecuteAsync(ep3);

        // Assert: 1, 2 y 3 deben quedar vistos; 4 y 5 no vistos
        sut.EpisodiosDelAnime.First(e => e.NumeroEpisodio == 1).Visto.Should().BeTrue();
        sut.EpisodiosDelAnime.First(e => e.NumeroEpisodio == 2).Visto.Should().BeTrue();
        sut.EpisodiosDelAnime.First(e => e.NumeroEpisodio == 3).Visto.Should().BeTrue();
        sut.EpisodiosDelAnime.First(e => e.NumeroEpisodio == 4).Visto.Should().BeFalse();
        sut.EpisodiosDelAnime.First(e => e.NumeroEpisodio == 5).Visto.Should().BeFalse();
        anime.EpisodiosVistos.Should().Be(3);

        _databaseServiceMock.Verify(d => d.GuardarRegistrosEpisodioBulkAsync(
            It.Is<IEnumerable<RegistroEpisodio>>(r => System.Linq.Enumerable.Count(r) == 3)), Times.Once);
    }

    [Fact]
    public async Task MarcarTemporadaCompletaCommand_DeberiaMarcarTodosLosEpisodios()
    {
        // Arrange
        var anime = new AnimeItem { AniListId = 300, Titulo = "One Piece", TotalEpisodios = 4 };
        var sut = CreateSut();

        _fileScannerServiceMock
            .Setup(s => s.EscanearEpisodiosAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<EpisodioItem>());
        _databaseServiceMock
            .Setup(s => s.ObtenerRegistrosPorAnimeAsync(300))
            .ReturnsAsync(new List<RegistroEpisodio>());

        await sut.InicializarAsync(anime);

        // Act
        await sut.MarcarTemporadaCompletaCommand.ExecuteAsync(null);

        // Assert
        sut.EpisodiosDelAnime.Should().OnlyContain(e => e.Visto);
        anime.EpisodiosVistos.Should().Be(4);

        _databaseServiceMock.Verify(d => d.GuardarRegistrosEpisodioBulkAsync(
            It.Is<IEnumerable<RegistroEpisodio>>(r => System.Linq.Enumerable.Count(r) == 4)), Times.Once);
    }

    [Fact]
    public async Task AlternarVistoEpisodioCommand_DeberiaAlternarEstado()
    {
        // Arrange
        var anime = new AnimeItem { AniListId = 400, Titulo = "Bleach", TotalEpisodios = 2 };
        var sut = CreateSut();

        _fileScannerServiceMock
            .Setup(s => s.EscanearEpisodiosAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<EpisodioItem>());
        _databaseServiceMock
            .Setup(s => s.ObtenerRegistrosPorAnimeAsync(400))
            .ReturnsAsync(new List<RegistroEpisodio>());

        await sut.InicializarAsync(anime);

        var ep1 = sut.EpisodiosDelAnime.First(e => e.NumeroEpisodio == 1);
        ep1.Visto.Should().BeFalse();

        // Act 1: marcar como visto
        await sut.AlternarVistoEpisodioCommand.ExecuteAsync(ep1);

        // Assert 1
        ep1.Visto.Should().BeTrue();
        anime.EpisodiosVistos.Should().Be(1);
        _databaseServiceMock.Verify(d => d.GuardarRegistroEpisodioAsync(
            It.Is<RegistroEpisodio>(r => r.NumeroEpisodio == 1 && r.VistoLocal)), Times.Once);

        // Act 2: alternar a no visto
        await sut.AlternarVistoEpisodioCommand.ExecuteAsync(ep1);

        // Assert 2
        ep1.Visto.Should().BeFalse();
        anime.EpisodiosVistos.Should().Be(0);
        _databaseServiceMock.Verify(d => d.GuardarRegistroEpisodioAsync(
            It.Is<RegistroEpisodio>(r => r.NumeroEpisodio == 1 && !r.VistoLocal)), Times.Once);
    }
}
