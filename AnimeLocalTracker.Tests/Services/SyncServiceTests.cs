using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

public class SyncServiceTests
{
    private readonly Mock<IDatabaseService> _dbMock = new();
    private readonly Mock<IAnimeTrackingService> _trackingMock = new();
    private readonly Mock<IAuthService> _authMock = new();
    private readonly SyncService _sut;

    public SyncServiceTests()
    {
        _sut = new SyncService(_dbMock.Object, _trackingMock.Object, _authMock.Object);
    }

    [Fact]
    public async Task SincronizarPendientesAsync_UsuarioNoAutenticado_DeberiaOmitirYRetornarCero()
    {
        // Arrange
        _authMock.Setup(a => a.EstaAutenticado()).Returns(false);

        // Act
        int result = await _sut.SincronizarPendientesAsync();

        // Assert
        result.Should().Be(0);
        _dbMock.Verify(d => d.ObtenerEpisodiosNoSincronizadosAsync(), Times.Never);
    }

    [Fact]
    public async Task SincronizarPendientesAsync_SinPendientes_DeberiaRetornarCero()
    {
        // Arrange
        _authMock.Setup(a => a.EstaAutenticado()).Returns(true);
        _authMock.Setup(a => a.ObtenerToken()).Returns("test_token");
        _dbMock.Setup(d => d.ObtenerEpisodiosNoSincronizadosAsync()).ReturnsAsync(new List<RegistroEpisodio>());

        // Act
        int result = await _sut.SincronizarPendientesAsync();

        // Assert
        result.Should().Be(0);
        _trackingMock.Verify(t => t.ActualizarProgresoAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SincronizarPendientesAsync_ConPendientes_DeberiaActualizarProgresoYMarcarSincronizados()
    {
        // Arrange
        _authMock.Setup(a => a.EstaAutenticado()).Returns(true);
        _authMock.Setup(a => a.ObtenerToken()).Returns("test_token_123");

        var pendientes = new List<RegistroEpisodio>
        {
            new() { Id = 1, AniListId = 50, NumeroEpisodio = 3, VistoLocal = true, SincronizadoEnNube = false },
            new() { Id = 2, AniListId = 50, NumeroEpisodio = 4, VistoLocal = true, SincronizadoEnNube = false },
            new() { Id = 3, AniListId = 99, NumeroEpisodio = 12, VistoLocal = true, SincronizadoEnNube = false }
        };

        _dbMock.Setup(d => d.ObtenerEpisodiosNoSincronizadosAsync()).ReturnsAsync(pendientes);
        _trackingMock.Setup(t => t.ActualizarProgresoAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>()))
                     .ReturnsAsync(true);

        // Act
        int result = await _sut.SincronizarPendientesAsync();

        // Assert
        result.Should().Be(3);
        // Debe enviar el episodio más alto (4) para el anime 50
        _trackingMock.Verify(t => t.ActualizarProgresoAsync(50, 4, "test_token_123"), Times.Once);
        // Debe enviar el episodio 12 para el anime 99
        _trackingMock.Verify(t => t.ActualizarProgresoAsync(99, 12, "test_token_123"), Times.Once);
        // Debe marcar los IDs como sincronizados
        _dbMock.Verify(d => d.MarcarEpisodiosSincronizadosAsync(It.Is<IEnumerable<int>>(ids => ids.Count() == 2)), Times.Once);
        _dbMock.Verify(d => d.MarcarEpisodiosSincronizadosAsync(It.Is<IEnumerable<int>>(ids => ids.Count() == 1)), Times.Once);
    }
}
