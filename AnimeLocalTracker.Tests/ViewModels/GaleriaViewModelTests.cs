using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
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
            _httpClientFactoryMock.Object
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
}
