using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Python;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// Prioridad de fuentes de saltos de OP/ED: la detección por audio de referencia (mp3 ya descargado
/// desde la Ficha vía AnimeThemes.moe) debe ganarle tanto al plugin de comparación entre 2 episodios
/// como a AniSkip en la nube cuando hay una referencia local aplicable al episodio.
/// </summary>
public class SkipTimesCoordinatorTests : System.IDisposable
{
    private readonly Mock<IAniSkipService> _aniSkipMock = new();
    private readonly Mock<IPythonBridgeService> _pythonBridgeMock = new();
    private readonly Mock<IAnimeThemesDownloadService> _themesDownloadMock = new();
    private readonly string _carpetaEpisodioVacia;

    public void Dispose()
    {
        System.GC.SuppressFinalize(this);
        try { Directory.Delete(_carpetaEpisodioVacia, recursive: true); } catch { /* best-effort */ }
    }

    public SkipTimesCoordinatorTests()
    {
        _pythonBridgeMock.Setup(p => p.IsAvailableAsync()).ReturnsAsync(true);

        // Carpeta real pero vacía: así el plugin de comparación entre 2 episodios (que busca
        // "otro video" en el mismo directorio) no encuentra ninguno y no interfiere en las
        // aserciones sobre la fuente de referencia.
        _carpetaEpisodioVacia = Path.Combine(Path.GetTempPath(), "AnimeLocalTrackerTests_" + System.Guid.NewGuid());
        Directory.CreateDirectory(_carpetaEpisodioVacia);
    }

    private SkipTimesCoordinator CrearSut() =>
        new(_aniSkipMock.Object, _pythonBridgeMock.Object, _themesDownloadMock.Object);

    private string RutaEpisodioFicticia() => Path.Combine(_carpetaEpisodioVacia, "Episodio 05.mkv");

    [Fact]
    public async Task CargarSkipTimesAsync_ConReferenciaOpLocalAplicable_DeberiaUsarlaYNoConsultarAniSkip()
    {
        var opLocal = new TemaLocalDisponible("OP", "OP1", 1, "1-16", @"C:\Music\1\OP_OP1_v1_ep1-16.mp3");
        _themesDownloadMock.Setup(t => t.ListarDescargasLocales(101)).Returns(new List<TemaLocalDisponible> { opLocal });

        var respuesta = new PluginDaemonResponse<SkipTimesCoordinator.AudioReferenceSkipResult>
        {
            Success = true,
            Result = new SkipTimesCoordinator.AudioReferenceSkipResult
            {
                Found = true,
                EstimatedStart = 90.0,
                EstimatedEnd = 180.0,
                Confidence = 0.8
            }
        };
        _pythonBridgeMock
            .Setup(p => p.ExecuteCommandAsync<It.IsAnyType, PluginDaemonResponse<SkipTimesCoordinator.AudioReferenceSkipResult>>(
                "run-plugin", It.IsAny<It.IsAnyType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(respuesta);

        var sut = CrearSut();

        var resultado = await sut.CargarSkipTimesAsync(101, 5, 1400, RutaEpisodioFicticia());

        resultado.Should().ContainSingle();
        resultado[0].SkipType.Should().Be("op");
        resultado[0].Interval.StartTime.Should().Be(90.0);
        resultado[0].Interval.EndTime.Should().Be(180.0);
        _aniSkipMock.Verify(a => a.ObtenerMalIdDesdeAniListAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never,
            "si la referencia local ya dio resultado, no debería hacer falta ir a la nube");
    }

    [Fact]
    public async Task CargarSkipTimesAsync_ConReferenciasOpYEdAplicables_DeberiaDevolverAmbas()
    {
        var opLocal = new TemaLocalDisponible("OP", "OP1", 1, null, @"C:\Music\1\OP_OP1_v1_eptodos.mp3");
        var edLocal = new TemaLocalDisponible("ED", "ED1", 1, null, @"C:\Music\1\ED_ED1_v1_eptodos.mp3");
        _themesDownloadMock.Setup(t => t.ListarDescargasLocales(101)).Returns(new List<TemaLocalDisponible> { opLocal, edLocal });

        var respuesta = new PluginDaemonResponse<SkipTimesCoordinator.AudioReferenceSkipResult>
        {
            Success = true,
            Result = new SkipTimesCoordinator.AudioReferenceSkipResult { Found = true, EstimatedStart = 10, EstimatedEnd = 100, Confidence = 0.9 }
        };
        _pythonBridgeMock
            .Setup(p => p.ExecuteCommandAsync<It.IsAnyType, PluginDaemonResponse<SkipTimesCoordinator.AudioReferenceSkipResult>>(
                "run-plugin", It.IsAny<It.IsAnyType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(respuesta);

        var sut = CrearSut();

        var resultado = await sut.CargarSkipTimesAsync(101, 3, 1400, RutaEpisodioFicticia());

        resultado.Should().HaveCount(2);
        resultado.Should().Contain(r => r.SkipType == "op");
        resultado.Should().Contain(r => r.SkipType == "ed");
    }

    [Fact]
    public async Task CargarSkipTimesAsync_ConReferenciaQueNoAplicaAlEpisodio_DeberiaIgnorarlaYCaerAAniSkip()
    {
        // OP1 solo aplica a los episodios 1-16; se pide el episodio 20.
        var opLocal = new TemaLocalDisponible("OP", "OP1", 1, "1-16", @"C:\Music\1\OP_OP1_v1_ep1-16.mp3");
        _themesDownloadMock.Setup(t => t.ListarDescargasLocales(101)).Returns(new List<TemaLocalDisponible> { opLocal });

        _aniSkipMock.Setup(a => a.ObtenerMalIdDesdeAniListAsync(101, It.IsAny<CancellationToken>())).ReturnsAsync((int?)null);

        var sut = CrearSut();

        var resultado = await sut.CargarSkipTimesAsync(101, 20, 1400, RutaEpisodioFicticia());

        resultado.Should().BeEmpty();
        _pythonBridgeMock.Verify(
            p => p.ExecuteCommandAsync<It.IsAnyType, PluginDaemonResponse<SkipTimesCoordinator.AudioReferenceSkipResult>>(
                It.IsAny<string>(), It.IsAny<It.IsAnyType>(), It.IsAny<CancellationToken>()),
            Times.Never, "OP1 no cubre el episodio 20: no debería intentar comparar contra esa referencia");
        _aniSkipMock.Verify(a => a.ObtenerMalIdDesdeAniListAsync(101, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CargarSkipTimesAsync_SinDescargasLocales_DeberiaSeguirFuncionandoSinLanzar()
    {
        _themesDownloadMock.Setup(t => t.ListarDescargasLocales(It.IsAny<int>())).Returns(new List<TemaLocalDisponible>());
        _aniSkipMock.Setup(a => a.ObtenerMalIdDesdeAniListAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync((int?)null);

        var sut = CrearSut();

        var resultado = await sut.CargarSkipTimesAsync(101, 1, 1400, RutaEpisodioFicticia());

        resultado.Should().BeEmpty();
    }
}
