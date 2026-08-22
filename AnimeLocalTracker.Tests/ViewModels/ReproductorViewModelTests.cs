using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

public class ReproductorViewModelTests
{
    private readonly Mock<IDatabaseService> _dbMock = new();
    private readonly Mock<IAnimeTrackingService> _trackingMock = new();
    private readonly Mock<IAuthService> _authMock = new();

    private ReproductorViewModel CreateSut()
    {
        return new ReproductorViewModel(_dbMock.Object, _trackingMock.Object, _authMock.Object);
    }

    [Fact]
    public void CargarVideo_ConListaDeEpisodios_DeberiaCalcularAnteriorYSiguienteCorrectamente()
    {
        // Arrange
        var sut = CreateSut();
        var lista = new List<EpisodioItem>
        {
            new() { NumeroEpisodio = 1, RutaCompleta = "C:\\Anime\\Ep01.mkv" },
            new() { NumeroEpisodio = 2, RutaCompleta = "C:\\Anime\\Ep02.mkv" },
            new() { NumeroEpisodio = 3, RutaCompleta = "C:\\Anime\\Ep03.mkv" }
        };

        // Act - Cargar Episodio 2 (tiene anterior y siguiente)
        sut.CargarVideo("C:\\Anime\\Ep02.mkv", 101, "Frieren", 2, lista);

        // Assert
        sut.TieneEpisodioAnterior.Should().BeTrue();
        sut.TieneEpisodioSiguiente.Should().BeTrue();
        sut.TituloAnime.Should().Be("Frieren");
        sut.TituloEpisodio.Should().Be("Episodio 2");
        sut.EpisodioAnteriorTooltip.Should().Contain("Episodio 1");
        sut.EpisodioSiguienteTooltip.Should().Contain("Episodio 3");

        sut.Dispose();
    }

    [Fact]
    public void CargarVideo_PrimerEpisodio_NoDeberiaTenerAnterior()
    {
        // Arrange
        var sut = CreateSut();
        var lista = new List<EpisodioItem>
        {
            new() { NumeroEpisodio = 1, RutaCompleta = "C:\\Anime\\Ep01.mkv" },
            new() { NumeroEpisodio = 2, RutaCompleta = "C:\\Anime\\Ep02.mkv" }
        };

        // Act
        sut.CargarVideo("C:\\Anime\\Ep01.mkv", 101, "Frieren", 1, lista);

        // Assert
        sut.TieneEpisodioAnterior.Should().BeFalse();
        sut.TieneEpisodioSiguiente.Should().BeTrue();
        sut.EpisodioAnteriorTooltip.Should().Be("No hay episodio anterior");

        sut.Dispose();
    }

    [Fact]
    public void CargarVideo_UltimoEpisodio_NoDeberiaTenerSiguiente()
    {
        // Arrange
        var sut = CreateSut();
        var lista = new List<EpisodioItem>
        {
            new() { NumeroEpisodio = 1, RutaCompleta = "C:\\Anime\\Ep01.mkv" },
            new() { NumeroEpisodio = 2, RutaCompleta = "C:\\Anime\\Ep02.mkv" }
        };

        // Act
        sut.CargarVideo("C:\\Anime\\Ep02.mkv", 101, "Frieren", 2, lista);

        // Assert
        sut.TieneEpisodioAnterior.Should().BeTrue();
        sut.TieneEpisodioSiguiente.Should().BeFalse();
        sut.EpisodioSiguienteTooltip.Should().Be("No hay siguiente episodio");

        sut.Dispose();
    }

    [Fact]
    public void CargarVideo_ConEpisodiosNoConsecutivos_DeberiaEncontrarElMasCercano()
    {
        // Arrange (episodios 1, 4, 10 descargados)
        var sut = CreateSut();
        var lista = new List<EpisodioItem>
        {
            new() { NumeroEpisodio = 1, RutaCompleta = "C:\\Anime\\Ep01.mkv" },
            new() { NumeroEpisodio = 4, RutaCompleta = "C:\\Anime\\Ep04.mkv" },
            new() { NumeroEpisodio = 10, RutaCompleta = "C:\\Anime\\Ep10.mkv" }
        };

        // Act - Cargar Episodio 4
        sut.CargarVideo("C:\\Anime\\Ep04.mkv", 101, "Frieren", 4, lista);

        // Assert
        sut.ObtenerAnteriorEpisodio()?.NumeroEpisodio.Should().Be(1);
        sut.ObtenerSiguienteEpisodio()?.NumeroEpisodio.Should().Be(10);

        sut.Dispose();
    }

    [Fact]
    public void SiguienteEpisodio_DeberiaCargarNuevoCapituloYActualizarEstados()
    {
        // Arrange
        var sut = CreateSut();
        var lista = new List<EpisodioItem>
        {
            new() { NumeroEpisodio = 1, RutaCompleta = "C:\\Anime\\Ep01.mkv" },
            new() { NumeroEpisodio = 2, RutaCompleta = "C:\\Anime\\Ep02.mkv" }
        };

        sut.CargarVideo("C:\\Anime\\Ep01.mkv", 101, "Frieren", 1, lista);

        // Act
        sut.SiguienteEpisodioCommand.Execute(null);

        // Assert
        sut.TituloEpisodio.Should().Be("Episodio 2");
        sut.TieneEpisodioAnterior.Should().BeTrue();
        sut.TieneEpisodioSiguiente.Should().BeFalse();

        sut.Dispose();
    }

    [Fact]
    public void AnteriorEpisodio_DeberiaCargarCapituloPrevioYActualizarEstados()
    {
        // Arrange
        var sut = CreateSut();
        var lista = new List<EpisodioItem>
        {
            new() { NumeroEpisodio = 1, RutaCompleta = "C:\\Anime\\Ep01.mkv" },
            new() { NumeroEpisodio = 2, RutaCompleta = "C:\\Anime\\Ep02.mkv" }
        };

        sut.CargarVideo("C:\\Anime\\Ep02.mkv", 101, "Frieren", 2, lista);

        // Act
        sut.AnteriorEpisodioCommand.Execute(null);

        // Assert
        sut.TituloEpisodio.Should().Be("Episodio 1");
        sut.TieneEpisodioAnterior.Should().BeFalse();
        sut.TieneEpisodioSiguiente.Should().BeTrue();

        sut.Dispose();
    }

    [Fact]
    public void ToggleAutoPlay_DeberiaAlternarEstadoEIcono()
    {
        // Arrange
        var sut = CreateSut();
        sut.AutoPlaySiguiente.Should().BeTrue();
        sut.AutoPlayIcon.Should().Be("MotionPlay");

        // Act & Assert 1: Desactivar
        sut.ToggleAutoPlayCommand.Execute(null);
        sut.AutoPlaySiguiente.Should().BeFalse();
        sut.AutoPlayIcon.Should().Be("MotionPlayOff");

        // Act & Assert 2: Reactivar
        sut.ToggleAutoPlayCommand.Execute(null);
        sut.AutoPlaySiguiente.Should().BeTrue();
        sut.AutoPlayIcon.Should().Be("MotionPlay");

        sut.Dispose();
    }

    [Theory]
    [InlineData(0, "VolumeMute")]
    [InlineData(15, "VolumeLow")]
    [InlineData(50, "VolumeMedium")]
    [InlineData(85, "VolumeHigh")]
    public void Volumen_CambioDeValor_DeberiaCalcularIconoCorrecto(int nuevoVolumen, string iconoEsperado)
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.Volumen = nuevoVolumen;

        // Assert
        sut.VolumenIcon.Should().Be(iconoEsperado);

        sut.Dispose();
    }

    [Fact]
    public void ToggleMute_DeberiaSilenciarYRestaurarVolumenPrevio()
    {
        // Arrange
        var sut = CreateSut();
        sut.Volumen = 75;

        // Act: Silenciar
        sut.ToggleMuteCommand.Execute(null);
        sut.Volumen.Should().Be(0);
        sut.VolumenIcon.Should().Be("VolumeMute");

        // Act: Desmutear
        sut.ToggleMuteCommand.Execute(null);
        sut.Volumen.Should().Be(75);

        sut.Dispose();
    }

    [Fact]
    public async Task RealizarAutoTrackingAsync_DeberiaGuardarEnBdYEnviarNotificaciones()
    {
        // Arrange
        var sut = CreateSut();
        _dbMock.Setup(d => d.ObtenerRegistrosPorAnimeAsync(101))
            .ReturnsAsync(new List<RegistroEpisodio>());
        _authMock.Setup(a => a.ObtenerTokenGuardado()).Returns("test-token");
        _trackingMock.Setup(t => t.ActualizarProgresoAsync(101, 5, "test-token"))
            .ReturnsAsync(true);

        EpisodioActualizadoMensaje? mensajeRecibido = null;
        WeakReferenceMessenger.Default.Register<EpisodioActualizadoMensaje>(this, (r, m) =>
        {
            if (m.AnimeId == 101) mensajeRecibido = m;
        });

        sut.CargarVideo("C:\\Anime\\Ep05.mkv", 101, "Solo Leveling", 5);

        // Act
        await sut.RealizarAutoTrackingAsync();

        // Assert
        _dbMock.Verify(d => d.GuardarRegistroEpisodioAsync(It.Is<RegistroEpisodio>(r =>
            r.AniListId == 101 && r.NumeroEpisodio == 5 && r.VistoLocal)), Times.Once);

        _trackingMock.Verify(t => t.ActualizarProgresoAsync(101, 5, "test-token"), Times.Once);

        mensajeRecibido.Should().NotBeNull();
        mensajeRecibido!.AnimeId.Should().Be(101);
        mensajeRecibido.NumeroEpisodio.Should().Be(5);
        mensajeRecibido.VistoLocal.Should().BeTrue();

        WeakReferenceMessenger.Default.Unregister<EpisodioActualizadoMensaje>(this);
        sut.Dispose();
    }

    [Fact]
    public void CerrarCommand_DeberiaEnviarMensajeVolverDelReproductorYLimpiarRecursos()
    {
        // Arrange
        var sut = CreateSut();
        bool mensajeVolverRecibido = false;
        WeakReferenceMessenger.Default.Register<NavegarMensaje_VolverDelReproductor>(this, (r, m) =>
        {
            mensajeVolverRecibido = true;
        });

        sut.CargarVideo("C:\\Anime\\Ep01.mkv", 101, "Frieren", 1);

        // Act
        sut.CerrarCommand.Execute(null);

        // Assert
        mensajeVolverRecibido.Should().BeTrue();

        WeakReferenceMessenger.Default.Unregister<NavegarMensaje_VolverDelReproductor>(this);
    }
}
