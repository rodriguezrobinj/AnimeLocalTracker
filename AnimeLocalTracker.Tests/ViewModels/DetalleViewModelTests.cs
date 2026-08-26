using System.Collections.Generic;
using System.Threading.Tasks;
using AnimeLocalTracker.Core.Messages;
using AnimeLocalTracker.Core.Models;
using AnimeLocalTracker.Core.Services;
using AnimeLocalTracker.Core.ViewModels;
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
}
