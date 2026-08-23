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
        WeakReferenceMessenger.Default.UnregisterAll(this);
        WeakReferenceMessenger.Default.Register<ReproductorViewModelTests, EpisodioActualizadoMensaje>(this, (r, m) =>
        {
            if (m.AnimeId == 101 && m.VistoLocal) mensajeRecibido = m;
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

        WeakReferenceMessenger.Default.UnregisterAll(this);
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

    [Fact]
    public async Task VerificarProgresoPrevioAsync_ConProgresoValido_DeberiaEstablecerPosicionDeReanudacion()
    {
        // Arrange
        var sut = CreateSut();
        var registroPrevio = new RegistroEpisodio
        {
            AniListId = 101,
            NumeroEpisodio = 3,
            ProgresoSegundos = 450, // 7:30
            TotalSegundos = 1440,   // 24:00
            VistoLocal = false
        };

        _dbMock.Setup(d => d.ObtenerRegistrosPorAnimeAsync(101))
            .ReturnsAsync(new List<RegistroEpisodio> { registroPrevio });

        // Act
        await sut.VerificarProgresoPrevioAsync(101, 3);

        // Assert
        sut.ResumingPositionSeconds.Should().Be(450);
        sut.CurrentSeconds.Should().Be(450);
        sut.TiempoActualTexto.Should().Be("07:30");
        sut.TotalSeconds.Should().Be(1440);
        sut.TiempoTotalTexto.Should().Be("24:00");

        sut.Dispose();
    }

    [Fact]
    public async Task GuardarProgresoActualAsync_DeberiaPersistirEnBdYEnviarMensaje()
    {
        // Arrange
        var sut = CreateSut();
        _dbMock.Setup(d => d.ObtenerRegistrosPorAnimeAsync(101))
            .ReturnsAsync(new List<RegistroEpisodio>());

        EpisodioActualizadoMensaje? msg = null;
        WeakReferenceMessenger.Default.Register<EpisodioActualizadoMensaje>(this, (r, m) =>
        {
            if (m.AnimeId == 101 && m.NumeroEpisodio == 4) msg = m;
        });

        sut.CargarVideo("C:\\Anime\\Ep04.mkv", 101, "Frieren", 4);
        sut.CurrentSeconds = 500;
        sut.TotalSeconds = 1440;

        // Act
        await sut.GuardarProgresoActualAsync();

        // Assert
        _dbMock.Verify(d => d.GuardarRegistroEpisodioAsync(It.Is<RegistroEpisodio>(r =>
            r.AniListId == 101 && r.NumeroEpisodio == 4 && r.ProgresoSegundos == 500 && r.TotalSegundos == 1440)), Times.AtLeastOnce);

        msg.Should().NotBeNull();
        msg!.ProgresoSegundos.Should().Be(500);
        msg.TotalSegundos.Should().Be(1440);

        WeakReferenceMessenger.Default.Unregister<EpisodioActualizadoMensaje>(this);
        sut.Dispose();
    }

    [Fact]
    public void EpisodioItem_ProgresoPropiedades_DeberianCalcularseCorrectamente()
    {
        // Arrange
        var item = new EpisodioItem
        {
            NumeroEpisodio = 1,
            ProgresoSegundos = 600, // 10:00
            TotalSegundos = 1200,   // 20:00
            Visto = false
        };

        // Assert
        item.PorcentajeProgreso.Should().Be(0.5);
        item.TieneProgresoGuardado.Should().BeTrue();
        item.ProgresoFormateado.Should().Be("10:00 / 20:00");

        // Si se marca como visto, no debe mostrar progreso guardado
        item.Visto = true;
        item.TieneProgresoGuardado.Should().BeFalse();
    }

    [Fact]
    public void SkipIntroOutro_ConSegmentoActivo_DeberiaSaltarAlFinalDelIntervalo()
    {
        // Arrange
        var aniSkipMock = new Mock<IAniSkipService>();
        var settingsMock = new Mock<ISettingsService>();
        var sut = new ReproductorViewModel(_dbMock.Object, _trackingMock.Object, _authMock.Object, aniSkipMock.Object, settingsMock.Object);

        sut.CargarVideo("C:\\Anime\\Ep01.mkv", 101, "Frieren", 1);
        sut.TotalSeconds = 1440;
        sut.CurrentSeconds = 95;

        // Inyectar manualmente un segmento activo simulado
        var skip = new AniSkipResult
        {
            SkipType = "op",
            Interval = new AniSkipInterval { StartTime = 90.0, EndTime = 180.0 }
        };
        sut.SkipTimes.Add(skip);

        // Act - Simular botón visible y clic
        sut.SkipIntroOutroCommand.Execute(null);

        // Assert: Salta al final del intervalo (180.0 + 0.2 = 180.2)
        sut.CurrentSeconds.Should().Be(180.2);
        sut.MostrarSkipButton.Should().BeFalse();

        sut.Dispose();
    }

    [Fact]
    public void SkipIntroOutro_SinSegmentoActivo_DeberiaSaltar85Segundos()
    {
        // Arrange
        var sut = CreateSut();
        sut.CargarVideo("C:\\Anime\\Ep01.mkv", 101, "Frieren", 1);
        sut.TotalSeconds = 1440;
        sut.CurrentSeconds = 30;

        // Act
        sut.SkipIntroOutroCommand.Execute(null);

        // Assert: 30 + 85 = 115s
        sut.CurrentSeconds.Should().Be(115);
        sut.MostrarSkipButton.Should().BeFalse();

        sut.Dispose();
    }

    [Fact]
    public void IniciarCuentaRegresivaAutoPlay_DeberiaMostrarCountdownYDatosDelSiguiente()
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
        sut.IniciarCuentaRegresivaAutoPlay();

        // Assert
        sut.MostrarAutoPlayCountdown.Should().BeTrue();
        sut.AutoPlayCountdownSegundos.Should().Be(5);
        sut.SiguienteEpisodioTitulo.Should().Be("Episodio 2");

        sut.Dispose();
    }

    [Fact]
    public void CancelarAutoPlay_DeberiaOcultarCountdown()
    {
        // Arrange
        var sut = CreateSut();
        var lista = new List<EpisodioItem>
        {
            new() { NumeroEpisodio = 1, RutaCompleta = "C:\\Anime\\Ep01.mkv" },
            new() { NumeroEpisodio = 2, RutaCompleta = "C:\\Anime\\Ep02.mkv" }
        };
        sut.CargarVideo("C:\\Anime\\Ep01.mkv", 101, "Frieren", 1, lista);
        sut.IniciarCuentaRegresivaAutoPlay();
        sut.MostrarAutoPlayCountdown.Should().BeTrue();

        // Act
        sut.CancelarAutoPlayCommand.Execute(null);

        // Assert
        sut.MostrarAutoPlayCountdown.Should().BeFalse();

        sut.Dispose();
    }

    [Fact]
    public void ReproducirSiguienteAhora_DeberiaCargarSiguienteEpisodioInmediatamente()
    {
        // Arrange
        var sut = CreateSut();
        var lista = new List<EpisodioItem>
        {
            new() { NumeroEpisodio = 1, RutaCompleta = "C:\\Anime\\Ep01.mkv" },
            new() { NumeroEpisodio = 2, RutaCompleta = "C:\\Anime\\Ep02.mkv" }
        };
        sut.CargarVideo("C:\\Anime\\Ep01.mkv", 101, "Frieren", 1, lista);
        sut.IniciarCuentaRegresivaAutoPlay();

        // Act
        sut.ReproducirSiguienteAhoraCommand.Execute(null);

        // Assert
        sut.MostrarAutoPlayCountdown.Should().BeFalse();
        sut.TituloEpisodio.Should().Be("Episodio 2");

        sut.Dispose();
    }

    [Fact]
    public void Scrubbing_CicloCompleto_DeberiaActualizarPosicionYEstados()
    {
        // Arrange
        var sut = CreateSut();
        sut.CargarVideo("C:\\Anime\\Ep01.mkv", 101, "Frieren", 1);
        sut.TotalSeconds = 1440;

        // Act: inicio de arrastre
        sut.IniciarArrastre();
        sut.IsDraggingSlider.Should().BeTrue();

        // Vista previa dentro de rango
        sut.VistaPreviaArrastre(600);
        sut.CurrentSeconds.Should().Be(600);
        sut.TiempoActualTexto.Should().Be("10:00");
        sut.TiempoCombinadoTexto.Should().Contain("10:00");

        // Vista previa fuera de rango (debe acotar a la duración)
        sut.VistaPreviaArrastre(99999);
        sut.CurrentSeconds.Should().Be(1440);

        // Vista previa negativa (debe acotar a 0)
        sut.VistaPreviaArrastre(-10);
        sut.CurrentSeconds.Should().Be(0);

        // Fin de arrastre con movimiento real
        sut.FinalizarArrastre(720);
        sut.IsDraggingSlider.Should().BeFalse();
        sut.CurrentSeconds.Should().Be(720);
        sut.TiempoActualTexto.Should().Be("12:00");

        sut.Dispose();
    }

    [Fact]
    public void VistaPreviaArrastre_SinArrastrePrevio_NoDeberiaAplicarNada()
    {
        // Arrange
        var sut = CreateSut();
        sut.CargarVideo("C:\\Anime\\Ep01.mkv", 101, "Frieren", 1);
        sut.CurrentSeconds = 30;

        // Act
        sut.VistaPreviaArrastre(500);

        // Assert: sin IniciarArrastre la vista previa se ignora
        sut.CurrentSeconds.Should().Be(30);
        sut.IsDraggingSlider.Should().BeFalse();

        sut.Dispose();
    }

    [Fact]
    public void FinalizarArrastre_ClicDirectoEnPista_DeberiaHacerSeekSinArrastre()
    {
        // Arrange
        var sut = CreateSut();
        sut.CargarVideo("C:\\Anime\\Ep01.mkv", 101, "Frieren", 1);
        sut.TotalSeconds = 1440;

        // Act: clic directo (sin arrastre previo)
        sut.FinalizarArrastre(300);

        // Assert
        sut.CurrentSeconds.Should().Be(300);
        sut.TiempoActualTexto.Should().Be("05:00");
        sut.IsDraggingSlider.Should().BeFalse();

        sut.Dispose();
    }

    [Fact]
    public void Seek_ConValoresFueraDeRango_DeberiaAcotar()
    {
        // Arrange
        var sut = CreateSut();
        sut.CargarVideo("C:\\Anime\\Ep01.mkv", 101, "Frieren", 1);

        // Act & Assert: negativo -> 0
        sut.SeekCommand.Execute(-25.0);
        sut.CurrentSeconds.Should().Be(0);

        // Act & Assert: negativo grande -> 0
        sut.SeekCommand.Execute(-0.001);
        sut.CurrentSeconds.Should().Be(0);

        sut.Dispose();
    }
}
