using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Controls;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>
/// Barra de progreso del reproductor (investigación 2026-09-29): empieza vacía en el segundo 0, termina llena y con el tiempo igual a
/// la duración ("24:00 / 24:00", no "23:59 / 24:00"), y al cambiar de episodio no arrastra la posición, la duración ni los
/// marcadores del anterior.
/// </summary>
public class ReproductorBarraProgresoTests
{
    private readonly Mock<IPlaybackStateService> _estado = new();
    private readonly Mock<ISkipTimesCoordinator> _skips = new();

    private ReproductorViewModel CrearSut() => new(
        Mock.Of<IDatabaseService>(), Mock.Of<IAnimeTrackingService>(), Mock.Of<IAuthService>(),
        playbackStateService: _estado.Object, skipTimesCoordinator: _skips.Object);

    private void Reanudar(int episodio, double posicion, double duracion) =>
        _estado.Setup(e => e.ObtenerPosicionParaReanudarAsync(101, episodio, It.IsAny<string?>())).ReturnsAsync((posicion, duracion));

    // === Formato ===

    [Theory]
    [InlineData(0, 1440, "00:00")]
    [InlineData(1439.9, 1440, "23:59")]
    [InlineData(1440, 1440.4, "24:00")]
    [InlineData(59.99, 1440, "00:59")]
    [InlineData(3599, 4800, "0:59:59")]   // mismo formato que la duración (antes "59:59 / 01:20:00")
    [InlineData(4800, 4800, "1:20:00")]
    public void FormatearTiempo_UsaElMismoFormatoParaPosicionYDuracion(double segundos, double duracion, string esperado) =>
        ReproductorViewModel.FormatearTiempo(segundos, duracion).Should().Be(esperado);

    // === Final del episodio ===

    [Fact]
    public async Task AlTerminar_LaBarraQuedaLlenaYElTiempoIgualALaDuracion()
    {
        Reanudar(5, 1000, 1440.4);
        using var sut = CrearSut();
        await sut.CargarVideoAsync("C:\\Anime\\Ep05.mkv", 101, "Frieren", 5);
        sut.Seek(1439.6); // la última lectura del sondeo antes del final

        sut.AjustarPosicionAlFinal();

        sut.CurrentSeconds.Should().Be(sut.TotalSeconds);
        sut.TiempoCombinadoTexto.Should().Be("24:00 / 24:00");
    }

    [Fact]
    public async Task AlReanudar_LaDuracionSeFijaAntesQueLaPosicion()
    {
        Reanudar(5, 1000, 1440);
        using var sut = CrearSut();

        await sut.CargarVideoAsync("C:\\Anime\\Ep05.mkv", 101, "Frieren", 5);

        (sut.CurrentSeconds, sut.TotalSeconds).Should().Be((1000d, 1440d));
        sut.TiempoCombinadoTexto.Should().Be("16:40 / 24:00");
    }

    // === Cambio de episodio ===

    [Fact]
    public async Task AlPasarAlSiguienteEpisodio_LaBarraEmpiezaDeCero()
    {
        Reanudar(5, 1000, 1440);
        using var sut = CrearSut();
        await sut.CargarVideoAsync("C:\\Anime\\Ep05.mkv", 101, "Frieren", 5);

        await sut.CargarVideoAsync("C:\\Anime\\Ep06.mkv", 101, "Frieren", 6); // sin progreso guardado

        (sut.CurrentSeconds, sut.TotalSeconds).Should().Be((0d, 0d));
        sut.TiempoCombinadoTexto.Should().Be("00:00 / 00:00", "antes seguía mostrando la posición y la duración del episodio anterior");
    }

    [Fact]
    public async Task UnEndingQueEmpiezaDespuesDelFinalDelEpisodioAnterior_NoDesapareceDeLaBarra()
    {
        // Episodio 5 dura 1420 s; el 6 dura más y su ending empieza en el 1425. Antes se recortaba con la duración del 5 y se perdía.
        Reanudar(5, 1000, 1420);
        _skips.Setup(s => s.CargarSkipTimesAsync(101, 6, It.IsAny<double>(), It.IsAny<string?>(), It.IsAny<IProgress<IReadOnlyList<AniSkipResult>>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AniSkipResult> { new() { SkipType = "ed", Interval = new AniSkipInterval { StartTime = 1425, EndTime = 1435 } } });
        using var sut = CrearSut();
        await sut.CargarVideoAsync("C:\\Anime\\Ep05.mkv", 101, "Frieren", 5);

        await sut.CargarVideoAsync("C:\\Anime\\Ep06.mkv", 101, "Frieren", 6);
        await sut.CargarSkipTimesAsync(101, 6);

        sut.SegmentosLineaTiempo.Should().ContainSingle(s => s.Tipo == TipoSegmentoLineaTiempo.Ending && s.Inicio == 1425);
    }

    // === Dibujo ===

    [Theory]
    [InlineData(1366)]
    [InlineData(300)]
    public void EnElSegundo0_LaLineaReproducidaNoTieneLargo(double ancho)
    {
        // La línea blanca va de InicioLinea a la posición actual: en el segundo 0 son el mismo punto (antes empezaba en el borde, 10 px antes).
        MarcadoresLineaTiempo.PosicionX(0, 1440, ancho, 10).Should().Be(MarcadoresLineaTiempo.InicioLinea(ancho, 10));
    }

    [Fact]
    public void AlFinal_LaBolitaLlegaAlFinalDeLaPista()
    {
        // La pista de fondo tiene 10 px de margen a cada lado (igual que el recorrido de la bolita).
        MarcadoresLineaTiempo.PosicionX(1440, 1440, 1366, 10).Should().Be(1366 - 10);
    }
}
