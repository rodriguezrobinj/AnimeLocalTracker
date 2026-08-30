using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Python;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

public class HoverThumbnailServiceTests
{
    private readonly Mock<IPythonBridgeService> _pythonBridgeMock = new();
    private readonly Mock<IDatabaseService> _dbMock = new();
    private readonly Mock<IAnimeTrackingService> _trackingMock = new();
    private readonly Mock<IAuthService> _authMock = new();

    [Fact]
    public async Task ObtenerMiniaturaHoverAsync_RutaVaciaONoExistente_RetornaNull()
    {
        var sut = new HoverThumbnailService(_pythonBridgeMock.Object);

        var resVacio = await sut.ObtenerMiniaturaHoverAsync("", 10);
        var resInexistente = await sut.ObtenerMiniaturaHoverAsync("C:\\NoExiste\\Video.mkv", 10);

        resVacio.Should().BeNull();
        resInexistente.Should().BeNull();
    }

    [Fact]
    public async Task ObtenerMiniaturaHoverAsync_SegundosNegativos_RetornaNull()
    {
        var sut = new HoverThumbnailService(_pythonBridgeMock.Object);

        var tempFile = Path.GetTempFileName();
        try
        {
            var res = await sut.ObtenerMiniaturaHoverAsync(tempFile, -5);
            res.Should().BeNull();
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void BucketIntervaloSegundos_DeberiaSerCuatro()
    {
        var sut = new HoverThumbnailService(_pythonBridgeMock.Object);
        sut.BucketIntervaloSegundos.Should().Be(4);
    }

    [Fact]
    public void LimpiarCacheMemoria_NoLanzaExcepcion()
    {
        var sut = new HoverThumbnailService(_pythonBridgeMock.Object);
        var act = () => sut.LimpiarCacheMemoria();
        act.Should().NotThrow();
    }

    [Fact]
    public void ReproductorViewModel_ActualizarHoverPreview_PermaneceDesactivado()
    {
        var hoverMock = new Mock<IHoverThumbnailService>();
        hoverMock.Setup(h => h.BucketIntervaloSegundos).Returns(1);

        var vm = new ReproductorViewModel(
            _dbMock.Object,
            _trackingMock.Object,
            _authMock.Object,
            hoverThumbnailService: hoverMock.Object);

        vm.TotalSeconds = 1440;
        vm.CargarVideo("C:\\Anime\\Ep01.mkv", 1, "Test Anime", 1);

        vm.ActualizarHoverPreview(125.4, 250);

        // Timeline Hover desactivado
        vm.MostrarHoverPreview.Should().BeFalse();

        vm.OcultarHoverPreview();
        vm.MostrarHoverPreview.Should().BeFalse();
    }
}
