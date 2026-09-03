using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

public class PlaybackStateServiceTests
{
    private readonly Mock<IDatabaseService> _dbMock = new();
    private readonly Mock<IAnimeTrackingService> _trackingMock = new();
    private readonly Mock<IAuthService> _authMock = new();
    private readonly Mock<ISettingsService> _settingsMock = new();

    private PlaybackStateService CrearSut(bool conSettings = true)
    {
        if (conSettings)
        {
            _settingsMock.Setup(s => s.ObtenerConfiguracion()).Returns(new AppSettings());
        }
        return new PlaybackStateService(_dbMock.Object, _trackingMock.Object, _authMock.Object,
            conSettings ? _settingsMock.Object : null);
    }

    [Fact]
    public async Task MarcarComoVistoYSincronizarAsync_ConEpisodio0_NoDeberiaPersistirNiSincronizar()
    {
        // Arrange (FUN-004): archivo sin número de episodio derivado (NumeroEpisodio = 0)
        var sut = CrearSut();

        // Act
        bool resultado = await sut.MarcarComoVistoYSincronizarAsync(16498, 0, @"C:\videos\Specials.mkv", 1500);

        // Assert: nunca debe llegar a AniList (progress=0 resetearía el progreso real)
        resultado.Should().BeFalse();
        _dbMock.Verify(d => d.GuardarRegistroEpisodioAsync(It.IsAny<RegistroEpisodio>()), Times.Never);
        _trackingMock.Verify(t => t.ActualizarProgresoAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GuardarProgresoAsync_ConEpisodioInvalido_NoDeberiaPersistir()
    {
        // Arrange
        var sut = CrearSut();

        // Act
        var resultado = await sut.GuardarProgresoAsync(new DatosProgresoReproduccion
        {
            AnimeId = 16498,
            NumeroEpisodio = 0,
            PosicionSegundos = 600,
            DuracionSegundos = 1500
        });

        // Assert
        resultado.ProgresoSegundos.Should().Be(0);
        _dbMock.Verify(d => d.GuardarRegistroEpisodioAsync(It.IsAny<RegistroEpisodio>()), Times.Never);
    }

    [Fact]
    public async Task ObtenerPosicionParaReanudarAsync_ConProgresoSobreElUmbral_NoDeberiaReanudar()
    {
        // Arrange (FUN-003): umbral por defecto 90% → al 92% se considera terminado y no se reanuda
        _dbMock.Setup(d => d.ObtenerRegistrosPorAnimeAsync(16498))
            .ReturnsAsync(new List<RegistroEpisodio>
            {
                new() { AniListId = 16498, NumeroEpisodio = 5, ProgresoSegundos = 5520, TotalSegundos = 6000 }
            });
        var sut = CrearSut();

        // Act
        var resultado = await sut.ObtenerPosicionParaReanudarAsync(16498, 5);

        // Assert
        resultado.Should().BeNull();
    }

    [Fact]
    public async Task ObtenerPosicionParaReanudarAsync_ConProgresoAMedias_DeberiaDevolverLaPosicion()
    {
        // Arrange
        _dbMock.Setup(d => d.ObtenerRegistrosPorAnimeAsync(16498))
            .ReturnsAsync(new List<RegistroEpisodio>
            {
                new() { AniListId = 16498, NumeroEpisodio = 5, ProgresoSegundos = 3000, TotalSegundos = 6000 }
            });
        var sut = CrearSut();

        // Act
        var resultado = await sut.ObtenerPosicionParaReanudarAsync(16498, 5);

        // Assert
        resultado.Should().NotBeNull();
        resultado!.Value.Posicion.Should().Be(3000);
    }
}
