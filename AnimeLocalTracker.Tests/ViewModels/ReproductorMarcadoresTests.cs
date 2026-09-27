using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>Marcadores de opening/ending en la barra de progreso: se llenan cuando llegan los tramos y se limpian al cambiar de episodio.</summary>
public class ReproductorMarcadoresTests
{
    private readonly Mock<ISkipTimesCoordinator> _coordinador = new();

    private ReproductorViewModel CrearSut() => new(
        Mock.Of<IDatabaseService>(), Mock.Of<IAnimeTrackingService>(), Mock.Of<IAuthService>(),
        aniSkipService: null, settingsService: null, playbackStateService: null,
        skipTimesCoordinator: _coordinador.Object);

    private static AniSkipResult T(string tipo, double ini, double fin) => new() { SkipType = tipo, Interval = new AniSkipInterval { StartTime = ini, EndTime = fin } };

    [Fact]
    public void AlAbrirElReproductor_NoHayMarcadores()
    {
        using var sut = CrearSut();

        sut.SegmentosLineaTiempo.Should().BeEmpty();
    }

    [Fact]
    public async Task CuandoLlegan_LosTramosSeConvierteEnMarcadores()
    {
        _coordinador.Setup(c => c.CargarSkipTimesAsync(101, 5, It.IsAny<double>(), It.IsAny<string?>(), It.IsAny<IProgress<IReadOnlyList<AniSkipResult>>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AniSkipResult> { T("op", 60, 150), T("ed", 1300, 1390) });
        using var sut = CrearSut();
        sut.TotalSeconds = 1400;

        await sut.CargarSkipTimesAsync(101, 5);

        sut.SegmentosLineaTiempo.Select(s => (s.Tipo, s.Inicio, s.Fin)).Should().Equal(
            (TipoSegmentoLineaTiempo.Opening, 60d, 150d), (TipoSegmentoLineaTiempo.Ending, 1300d, 1390d));
    }

    [Fact]
    public async Task AlAvisarDelCambio_ElPropertyChangedLlegaALaVista()
    {
        _coordinador.Setup(c => c.CargarSkipTimesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<double>(), It.IsAny<string?>(), It.IsAny<IProgress<IReadOnlyList<AniSkipResult>>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AniSkipResult> { T("op", 60, 150) });
        using var sut = CrearSut();
        var avisadas = new List<string?>();
        sut.PropertyChanged += (_, e) => avisadas.Add(e.PropertyName);

        await sut.CargarSkipTimesAsync(101, 5);

        avisadas.Should().Contain(nameof(ReproductorViewModel.SegmentosLineaTiempo));
    }

    [Fact]
    public async Task SinTramos_LaBarraQuedaSinMarcadores()
    {
        _coordinador.Setup(c => c.CargarSkipTimesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<double>(), It.IsAny<string?>(), It.IsAny<IProgress<IReadOnlyList<AniSkipResult>>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AniSkipResult>());
        using var sut = CrearSut();

        await sut.CargarSkipTimesAsync(101, 5);

        sut.SegmentosLineaTiempo.Should().BeEmpty();
    }

    [Fact]
    public async Task AlCambiarDeEpisodio_LosMarcadoresDelAnteriorSeLimpian()
    {
        _coordinador.Setup(c => c.CargarSkipTimesAsync(101, 1, It.IsAny<double>(), It.IsAny<string?>(), It.IsAny<IProgress<IReadOnlyList<AniSkipResult>>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AniSkipResult> { T("op", 60, 150) });
        using var sut = CrearSut();
        sut.CargarVideo("C:\\Anime\\Ep01.mkv", 101, "Frieren", 1);
        await sut.CargarSkipTimesAsync(101, 1);
        sut.SegmentosLineaTiempo.Should().NotBeEmpty();

        sut.CargarVideo("C:\\Anime\\Ep02.mkv", 101, "Frieren", 2);

        sut.SegmentosLineaTiempo.Should().BeEmpty("el opening del episodio 1 no es el del 2");
    }

    [Fact]
    public async Task SiElCoordinadorFalla_NoRompeYNoHayMarcadores()
    {
        _coordinador.Setup(c => c.CargarSkipTimesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<double>(), It.IsAny<string?>(), It.IsAny<IProgress<IReadOnlyList<AniSkipResult>>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.InvalidOperationException("boom"));
        using var sut = CrearSut();

        await sut.CargarSkipTimesAsync(101, 5);

        sut.SegmentosLineaTiempo.Should().BeEmpty();
    }

    [Fact]
    public async Task LosTramosParcialesSeMuestranSinEsperarAlFinalDelAnalisis()
    {
        var terminar = new TaskCompletionSource();
        _coordinador.Setup(c => c.CargarSkipTimesAsync(101, 5, It.IsAny<double>(), It.IsAny<string?>(), It.IsAny<IProgress<IReadOnlyList<AniSkipResult>>?>(), It.IsAny<CancellationToken>()))
            .Returns(async (int _, int _, double _, string? _, IProgress<IReadOnlyList<AniSkipResult>>? progreso, CancellationToken _) =>
            {
                progreso!.Report(new List<AniSkipResult> { T("ed", 1300, 1390) }); // primero solo lo del audio
                await terminar.Task;                                              // el resto del análisis sigue en curso
                return (IReadOnlyList<AniSkipResult>)new List<AniSkipResult> { T("op", 60, 150), T("ed", 1300, 1390) };
            });
        using var sut = CrearSut();
        sut.TotalSeconds = 1400;

        var carga = sut.CargarSkipTimesAsync(101, 5);
        for (int i = 0; i < 100 && sut.SegmentosLineaTiempo.Count == 0; i++) await Task.Delay(20);

        sut.SegmentosLineaTiempo.Should().ContainSingle().Which.Tipo.Should().Be(TipoSegmentoLineaTiempo.Ending, "el ending del audio ya se ve mientras el análisis sigue");
        sut.SkipTimes.Should().ContainSingle();

        terminar.SetResult();
        await carga;
        sut.SegmentosLineaTiempo.Should().HaveCount(2);
    }

    [Fact]
    public async Task UnAvisoParcialTardioDeUnEpisodioCancelado_NoPisaLosMarcadoresDelNuevo()
    {
        IProgress<IReadOnlyList<AniSkipResult>>? progresoViejo = null;
        var liberar = new TaskCompletionSource();
        _coordinador.Setup(c => c.CargarSkipTimesAsync(101, 1, It.IsAny<double>(), It.IsAny<string?>(), It.IsAny<IProgress<IReadOnlyList<AniSkipResult>>?>(), It.IsAny<CancellationToken>()))
            .Returns(async (int _, int _, double _, string? _, IProgress<IReadOnlyList<AniSkipResult>>? progreso, CancellationToken _) =>
            {
                progresoViejo = progreso;
                await liberar.Task;
                return (IReadOnlyList<AniSkipResult>)new List<AniSkipResult>();
            });
        using var sut = CrearSut();
        using var cts = new CancellationTokenSource();

        var carga = sut.CargarSkipTimesAsync(101, 1, cts.Token);
        for (int i = 0; i < 100 && progresoViejo == null; i++) await Task.Delay(20);
        cts.Cancel();
        progresoViejo!.Report(new List<AniSkipResult> { T("op", 60, 150) });
        await Task.Delay(150);
        liberar.SetResult();
        await carga;

        sut.SegmentosLineaTiempo.Should().BeEmpty("el episodio ya cambió: su aviso tardío se ignora");
    }
}
