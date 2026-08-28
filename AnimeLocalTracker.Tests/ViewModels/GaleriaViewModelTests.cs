using System.Collections.Generic;
using System.Net.Http;
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

public class GaleriaViewModelTests
{
    private readonly Mock<IAnimeTrackingService> _trackingServiceMock = new();
    private readonly Mock<IDatabaseService> _databaseServiceMock = new();
    private readonly Mock<IAuthService> _authServiceMock = new();
    private readonly Mock<IDialogService> _dialogServiceMock = new();
    private readonly Mock<IHttpClientFactory> _httpClientFactoryMock = new();
    private readonly Mock<IImageCacheService> _imageCacheServiceMock = new();
    private readonly Mock<IFileScannerService> _fileScannerServiceMock = new();

    private GaleriaViewModel CreateSut(List<AnimeItem>? animes = null)
    {
        _databaseServiceMock
            .Setup(d => d.ObtenerTodosLosAnimesAsync())
            .ReturnsAsync(animes ?? new List<AnimeItem>());

        _databaseServiceMock
            .Setup(d => d.ObtenerTodosLosRegistrosAsync())
            .ReturnsAsync(new List<RegistroEpisodio>());

        return new GaleriaViewModel(
            _trackingServiceMock.Object,
            _databaseServiceMock.Object,
            _authServiceMock.Object,
            _dialogServiceMock.Object,
            _httpClientFactoryMock.Object,
            _imageCacheServiceMock.Object,
            _fileScannerServiceMock.Object
        );
    }

    [Fact]
    public async Task CargarBibliotecaAsync_DeberiaLlenarColeccionYActualizarPropiedades()
    {
        // Arrange
        var lista = new List<AnimeItem>
        {
            new() { AniListId = 1, Titulo = "Bleach", EstadoUsuario = "CURRENT" },
            new() { AniListId = 2, Titulo = "Death Note", EstadoUsuario = "COMPLETED" }
        };

        // Act
        var sut = CreateSut(lista);
        await Task.Delay(100); // Dar tiempo a la tarea asíncrona del constructor

        // Assert
        sut.BibliotecaLocales.Should().HaveCount(2);
        sut.TotalAnimesBiblioteca.Should().Be(2);
        sut.BibliotecaVacia.Should().BeFalse();
    }

    [Fact]
    public void Receive_AnimeAnadidoMensaje_DeberiaAgregarALaBibliotecaSiNoExiste()
    {
        // Arrange
        var sut = CreateSut();
        var nuevoAnime = new AnimeItem { AniListId = 50, Titulo = "Solo Leveling" };

        // Act
        sut.Receive(new AnimeAñadidoMensaje(nuevoAnime));

        // Assert
        sut.BibliotecaLocales.Should().ContainSingle(a => a.AniListId == 50);
        sut.TotalAnimesBiblioteca.Should().Be(1);
    }

    [Fact]
    public void CambiarFiltroEstadoCommand_DeberiaActualizarFiltroEstado()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.CambiarFiltroEstadoCommand.Execute("Completados");

        // Assert
        sut.FiltroEstado.Should().Be("Completados");
    }

    [Fact]
    public async Task ElegirQueVerHoy_ConEpisodiosNoVistos_DeberiaNavegarAlReproductor()
    {
        // Arrange
        var anime = new AnimeItem { AniListId = 50, Titulo = "One Piece", EstadoUsuario = "CURRENT", RutaCarpeta = "C:\\Anime\\OnePiece" };
        var episodiosLocales = new List<EpisodioItem>
        {
            new() { NumeroEpisodio = 1, RutaCompleta = "C:\\Anime\\OnePiece\\ep01.mkv", Visto = true },
            new() { NumeroEpisodio = 2, RutaCompleta = "C:\\Anime\\OnePiece\\ep02.mkv", Visto = false },
            new() { NumeroEpisodio = 3, RutaCompleta = "C:\\Anime\\OnePiece\\ep03.mkv", Visto = false }
        };
        _fileScannerServiceMock
            .Setup(s => s.EscanearEpisodiosAsync(anime.RutaCarpeta))
            .ReturnsAsync(episodiosLocales);
        _databaseServiceMock
            .Setup(d => d.ObtenerRegistrosPorAnimeAsync(50))
            .ReturnsAsync(new List<RegistroEpisodio> { new() { AniListId = 50, NumeroEpisodio = 1, VistoLocal = true } });

        var sut = CreateSut(new List<AnimeItem> { anime });
        await Task.Delay(100);

        NavegarMensaje_Reproductor? recibido = null;
        var recipient = new Recipient();
        WeakReferenceMessenger.Default.Register<NavegarMensaje_Reproductor>(recipient,
            (r, m) => recibido = m);
        try
        {
            // Act
            await sut.ElegirQueVerHoyCommand.ExecuteAsync(null);

            // Assert: navega al reproductor con el siguiente episodio no visto cronológicamente (episodio 2)
            recibido.Should().NotBeNull();
            recibido!.AnimeId.Should().Be(50);
            recibido.TituloAnime.Should().Be("One Piece");
            recibido.Episodio.Should().Be(2);
            recibido.EpisodiosDisponibles.Should().HaveCount(3);
            recibido.EpisodiosDisponibles!.Should().OnlyContain(e => !string.IsNullOrWhiteSpace(e.RutaCompleta));
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }
    }

    [Fact]
    public async Task ElegirQueVerHoy_TodosVistos_DeberiaMostrarDialogoYNoNavegar()
    {
        // Arrange
        var anime = new AnimeItem { AniListId = 50, Titulo = "One Piece", EstadoUsuario = "CURRENT", RutaCarpeta = "C:\\Anime\\OnePiece" };
        var episodiosLocales = new List<EpisodioItem>
        {
            new() { NumeroEpisodio = 1, RutaCompleta = "C:\\Anime\\OnePiece\\ep01.mkv" }
        };
        _fileScannerServiceMock
            .Setup(s => s.EscanearEpisodiosAsync(anime.RutaCarpeta))
            .ReturnsAsync(episodiosLocales);
        _databaseServiceMock
            .Setup(d => d.ObtenerRegistrosPorAnimeAsync(50))
            .ReturnsAsync(new List<RegistroEpisodio> { new() { AniListId = 50, NumeroEpisodio = 1, VistoLocal = true } });

        var sut = CreateSut(new List<AnimeItem> { anime });
        await Task.Delay(100);

        NavegarMensaje_Reproductor? recibido = null;
        var recipient = new Recipient();
        WeakReferenceMessenger.Default.Register<NavegarMensaje_Reproductor>(recipient,
            (r, m) => recibido = m);
        try
        {
            // Act
            await sut.ElegirQueVerHoyCommand.ExecuteAsync(null);

            // Assert
            recibido.Should().BeNull();
            _dialogServiceMock.Verify(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }
    }

    [Fact]
    public async Task ElegirQueVerHoy_SinCarpetas_DeberiaMostrarDialogoInformativo()
    {
        // Arrange
        var anime = new AnimeItem { AniListId = 50, Titulo = "One Piece", EstadoUsuario = "CURRENT" };
        var sut = CreateSut(new List<AnimeItem> { anime });
        await Task.Delay(100);

        // Act
        await sut.ElegirQueVerHoyCommand.ExecuteAsync(null);

        // Assert
        _dialogServiceMock.Verify(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        sut.EstaBuscandoQueVer.Should().BeFalse();
    }

    [Fact]
    public async Task ElegirQueVerHoy_SinEpisodiosSinVer_DeberiaPreferirAnimesEnCurso()
    {
        // Arrange: anime COMPLETED en biblioteca pero CURRENT también. Solo CURRENT tiene no vistos.
        var completado = new AnimeItem { AniListId = 1, Titulo = "Bleach", EstadoUsuario = "COMPLETED", RutaCarpeta = "C:\\Anime\\Bleach" };
        var enCurso = new AnimeItem { AniListId = 2, Titulo = "Jujutsu Kaisen", EstadoUsuario = "CURRENT", RutaCarpeta = "C:\\Anime\\JJK" };

        _fileScannerServiceMock
            .Setup(s => s.EscanearEpisodiosAsync("C:\\Anime\\Bleach"))
            .ReturnsAsync(new List<EpisodioItem> { new() { NumeroEpisodio = 1, RutaCompleta = "C:\\Anime\\Bleach\\ep01.mkv" }, });
        _fileScannerServiceMock
            .Setup(s => s.EscanearEpisodiosAsync("C:\\Anime\\JJK"))
            .ReturnsAsync(new List<EpisodioItem>
            {
                new() { NumeroEpisodio = 1, RutaCompleta = "C:\\Anime\\JJK\\ep01.mkv", Visto = true },
                new() { NumeroEpisodio = 2, RutaCompleta = "C:\\Anime\\JJK\\ep02.mkv", Visto = false }
            });
        _databaseServiceMock
            .Setup(d => d.ObtenerRegistrosPorAnimeAsync(1))
            .ReturnsAsync(new List<RegistroEpisodio>());
        _databaseServiceMock
            .Setup(d => d.ObtenerRegistrosPorAnimeAsync(2))
            .ReturnsAsync(new List<RegistroEpisodio>());

        var sut = CreateSut(new List<AnimeItem> { completado, enCurso });
        await Task.Delay(100);

        NavegarMensaje_Reproductor? recibido = null;
        var recipient = new Recipient();
        WeakReferenceMessenger.Default.Register<NavegarMensaje_Reproductor>(recipient,
            (r, m) => recibido = m);
        try
        {
            // Act
            await sut.ElegirQueVerHoyCommand.ExecuteAsync(null);

            // Assert: prefiere el anime en curso, no el completado
            recibido.Should().NotBeNull();
            recibido!.AnimeId.Should().Be(2);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }
    }

    public class Recipient : IRecipient<NavegarMensaje_Reproductor>
    {
        public void Receive(NavegarMensaje_Reproductor message) { }
    }
}
