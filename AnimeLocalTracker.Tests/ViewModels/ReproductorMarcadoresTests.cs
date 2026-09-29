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

    // === Pre-análisis del siguiente episodio ===

    private static List<EpisodioItem> Episodios(params (int Numero, string Ruta)[] episodios) =>
        episodios.Select(e => new EpisodioItem { NumeroEpisodio = e.Numero, RutaCompleta = e.Ruta }).ToList();

    private static string ArchivoTemporal()
    {
        string ruta = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AnimeTracker_Pre_" + Guid.NewGuid().ToString("N") + ".mkv");
        System.IO.File.WriteAllBytes(ruta, new byte[16]);
        return ruta;
    }

    private void CoordinadorSinTramos() =>
        _coordinador.Setup(c => c.CargarSkipTimesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<double>(), It.IsAny<string?>(), It.IsAny<IProgress<IReadOnlyList<AniSkipResult>>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AniSkipResult>());

    private async Task EsperarPreanalisis(int episodio, string ruta)
    {
        for (int i = 0; i < 200; i++)
        {
            if (_coordinador.Invocations.Any(inv => inv.Method.Name == nameof(ISkipTimesCoordinator.PreanalizarAsync)
                                                   && (int)inv.Arguments[1] == episodio && (string)inv.Arguments[2] == ruta)) return;
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task TrasAnalizarElEpisodio_SePreAnalizaElSiguienteConArchivo()
    {
        string ep1 = ArchivoTemporal(), ep2 = ArchivoTemporal();
        try
        {
            CoordinadorSinTramos();
            using var sut = CrearSut();
            sut.EsperaPreanalisis = TimeSpan.Zero;
            sut.CargarVideo(ep1, 101, "Frieren", 1, Episodios((1, ep1), (2, ep2), (3, "")));

            await sut.CargarSkipTimesAsync(101, 1);
            await EsperarPreanalisis(2, ep2);

            _coordinador.Verify(c => c.PreanalizarAsync(101, 2, ep2, It.IsAny<CancellationToken>()), Times.AtLeastOnce());
        }
        finally
        {
            System.IO.File.Delete(ep1);
            System.IO.File.Delete(ep2);
        }
    }

    [Fact]
    public async Task EnElUltimoEpisodioConArchivo_NoSePreAnalizaNada()
    {
        string ep2 = ArchivoTemporal();
        try
        {
            CoordinadorSinTramos();
            using var sut = CrearSut();
            sut.EsperaPreanalisis = TimeSpan.Zero;
            sut.CargarVideo(ep2, 101, "Frieren", 2, Episodios((1, "C:\\Anime\\Ep01.mkv"), (2, ep2), (3, "")));

            await sut.CargarSkipTimesAsync(101, 2);
            await Task.Delay(100);

            _coordinador.Verify(c => c.PreanalizarAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            System.IO.File.Delete(ep2);
        }
    }

    [Fact]
    public async Task SiSeCambiaDeEpisodioDuranteLaEspera_ElPreAnalisisProgramadoNoSeHace()
    {
        string ep1 = ArchivoTemporal(), ep2 = ArchivoTemporal();
        try
        {
            CoordinadorSinTramos();
            using var sut = CrearSut();
            sut.EsperaPreanalisis = TimeSpan.FromMilliseconds(300);
            var lista = Episodios((1, ep1), (2, ep2));
            sut.CargarVideo(ep1, 101, "Frieren", 1, lista); // su análisis (instantáneo aquí) programa el pre-análisis del 2
            await Task.Delay(50);

            sut.CargarVideo(ep2, 101, "Frieren", 2, lista); // el usuario pasa al 2 antes de que empiece el pre-análisis
            await Task.Delay(600);

            _coordinador.Verify(c => c.PreanalizarAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
                "el cambio de episodio ya lanza su propio análisis (y el 2 no tiene siguiente)");
        }
        finally
        {
            System.IO.File.Delete(ep1);
            System.IO.File.Delete(ep2);
        }
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
