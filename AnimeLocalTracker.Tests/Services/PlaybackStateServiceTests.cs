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
    public async Task MarcarComoVistoYSincronizarAsync_ConEpisodioAnteriorAlRemoto_NoDeberiaRegresarElProgreso()
    {
        // Arrange: en AniList ya se llegó al episodio 8 (p. ej. progreso de otro dispositivo o de
        // una sesión anterior); el usuario re-ve el episodio 5 (un recap). El progreso remoto no
        // debe regresar a 5.
        _authMock.Setup(a => a.ObtenerTokenGuardado()).Returns("token-valido");
        _trackingMock.Setup(t => t.ObtenerSeguimientoUsuarioAsync(16498, "token-valido"))
            .ReturnsAsync(new AniListMediaList { Progress = 8, Status = "CURRENT" });
        var sut = CrearSut();

        // Act
        bool resultado = await sut.MarcarComoVistoYSincronizarAsync(16498, 5, @"C:\videos\Ep05.mkv", 1500);

        // Assert: se marca localmente, pero NUNCA se sube progress=5 a AniList.
        resultado.Should().BeTrue();
        _dbMock.Verify(d => d.GuardarRegistroEpisodioAsync(It.IsAny<RegistroEpisodio>()), Times.Once);
        _trackingMock.Verify(t => t.ActualizarProgresoAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task MarcarComoVistoYSincronizarAsync_ConEpisodioIgualAlRemoto_NoDeberiaReenviarProgreso()
    {
        // Arrange: re-ver el mismo último episodio (progreso remoto ya está en 8, se ve el 8 de nuevo).
        _authMock.Setup(a => a.ObtenerTokenGuardado()).Returns("token-valido");
        _trackingMock.Setup(t => t.ObtenerSeguimientoUsuarioAsync(16498, "token-valido"))
            .ReturnsAsync(new AniListMediaList { Progress = 8, Status = "CURRENT" });
        var sut = CrearSut();

        // Act
        bool resultado = await sut.MarcarComoVistoYSincronizarAsync(16498, 8, @"C:\videos\Ep08.mkv", 1500);

        // Assert
        resultado.Should().BeTrue();
        _trackingMock.Verify(t => t.ActualizarProgresoAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task MarcarComoVistoYSincronizarAsync_ConAvanceNormal_SiDeberiaSubirElProgreso()
    {
        // Arrange: progreso remoto en 4, se ve el episodio 5 (avance normal) — comportamiento
        // existente, no debe romperse con el guard anti-regresión.
        _authMock.Setup(a => a.ObtenerTokenGuardado()).Returns("token-valido");
        _trackingMock.Setup(t => t.ObtenerSeguimientoUsuarioAsync(16498, "token-valido"))
            .ReturnsAsync(new AniListMediaList { Progress = 4, Status = "CURRENT" });
        _trackingMock.Setup(t => t.ActualizarProgresoAsync(16498, 5, "token-valido")).ReturnsAsync(true);
        var sut = CrearSut();

        // Act
        bool resultado = await sut.MarcarComoVistoYSincronizarAsync(16498, 5, @"C:\videos\Ep05.mkv", 1500);

        // Assert
        resultado.Should().BeTrue();
        _trackingMock.Verify(t => t.ActualizarProgresoAsync(16498, 5, "token-valido"), Times.Once);
    }

    [Fact]
    public async Task MarcarComoVistoYSincronizarAsync_SinSeguimientoRemotoPrevio_SiDeberiaSubirElProgreso()
    {
        // Arrange: anime nunca trackeado en AniList (ObtenerSeguimientoUsuarioAsync devuelve null) —
        // debe seguir subiendo el progreso normalmente.
        _authMock.Setup(a => a.ObtenerTokenGuardado()).Returns("token-valido");
        _trackingMock.Setup(t => t.ObtenerSeguimientoUsuarioAsync(16498, "token-valido"))
            .ReturnsAsync((AniListMediaList?)null);
        _trackingMock.Setup(t => t.ActualizarProgresoAsync(16498, 3, "token-valido")).ReturnsAsync(true);
        var sut = CrearSut();

        // Act
        bool resultado = await sut.MarcarComoVistoYSincronizarAsync(16498, 3, @"C:\videos\Ep03.mkv", 1500);

        // Assert
        resultado.Should().BeTrue();
        _trackingMock.Verify(t => t.ActualizarProgresoAsync(16498, 3, "token-valido"), Times.Once);
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

    [Fact]
    public async Task GuardarProgresoAsync_ConFueMarcadoComoVisto_NoDeberiaRevertirVistoLocal()
    {
        // Arrange: el reproductor ya determinó (vía RealizarAutoTrackingAsync) que el episodio
        // se vio, pero el guardado periódico llega con una posición justo por debajo del umbral
        // propio de este servicio (p. ej. entre el 90% del reproductor y un umbral más estricto
        // aquí, o simplemente una lectura de Player.CurTime un instante antes de cruzarlo).
        // Antes de la corrección, esta condición desmarcaba VistoLocal a pesar de que el llamador
        // ya había confirmado el visionado — el bug reportado al ver capítulos seguidos.
        var registro = new RegistroEpisodio
        {
            AniListId = 16498,
            NumeroEpisodio = 5,
            ProgresoSegundos = 0,
            TotalSegundos = 6000,
            VistoLocal = true
        };
        _dbMock.Setup(d => d.ObtenerRegistrosPorAnimeAsync(16498))
            .ReturnsAsync(new List<RegistroEpisodio> { registro });
        var sut = CrearSut();

        // Act: posición al 92% (por debajo de un umbral hipotético más estricto), pero el
        // reproductor ya marcó el episodio como visto.
        await sut.GuardarProgresoAsync(new DatosProgresoReproduccion
        {
            AnimeId = 16498,
            NumeroEpisodio = 5,
            PosicionSegundos = 5520,
            DuracionSegundos = 6000,
            FueMarcadoComoVisto = true
        });

        // Assert: VistoLocal debe seguir true — este guardado no debe revertirlo.
        registro.VistoLocal.Should().BeTrue();
    }

    [Fact]
    public async Task GuardarProgresoAsync_SinFueMarcadoComoVisto_SiDeberiaRevertirVistoLocalAMedias()
    {
        // Arrange: caso legítimo — el usuario reanuda un capítulo ya visto y lo deja a medias
        // (el reproductor NUNCA lo marcó como visto en esta sesión). Debe seguir quitando la
        // marca para que la barra de progreso se muestre correctamente.
        var registro = new RegistroEpisodio
        {
            AniListId = 16498,
            NumeroEpisodio = 5,
            ProgresoSegundos = 0,
            TotalSegundos = 6000,
            VistoLocal = true
        };
        _dbMock.Setup(d => d.ObtenerRegistrosPorAnimeAsync(16498))
            .ReturnsAsync(new List<RegistroEpisodio> { registro });
        var sut = CrearSut();

        // Act
        await sut.GuardarProgresoAsync(new DatosProgresoReproduccion
        {
            AnimeId = 16498,
            NumeroEpisodio = 5,
            PosicionSegundos = 3000,
            DuracionSegundos = 6000,
            FueMarcadoComoVisto = false
        });

        // Assert
        registro.VistoLocal.Should().BeFalse();
    }
}
