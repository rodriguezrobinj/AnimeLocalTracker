using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

public class PipNavigationTests : IDisposable
{
    private readonly Mock<INavigationService> _navigationServiceMock = new();
    private readonly Mock<IAnimeTrackingService> _trackingMock = new();
    private readonly Mock<IDownloadService> _downloadMock = new();
    private readonly Mock<IUpdateService> _updateMock = new();
    private readonly Mock<IDatabaseService> _dbMock = new();
    private readonly Mock<ISettingsService> _settingsMock = new();
    private readonly Mock<IAuthService> _authMock = new();
    private readonly AnimeLibraryService _libraryService;
    private readonly GaleriaViewModel _galeriaVm;
    private readonly string _tempFolder;

    public PipNavigationTests()
    {
        _tempFolder = Path.Combine(Path.GetTempPath(), "AnimeLocalTracker_Mini_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempFolder);
        _settingsMock.Setup(s => s.ObtenerRutaBaseAnimes()).Returns(_tempFolder);
        _updateMock.Setup(u => u.ObtenerVersionActual()).Returns("1.0.0-test");

        _dbMock.Setup(d => d.ObtenerTodosLosAnimesAsync()).ReturnsAsync(new List<AnimeItem>());
        _downloadMock.Setup(d => d.ObtenerDescargasActivas()).Returns(new List<DescargaItem>());
        _libraryService = new AnimeLibraryService(_dbMock.Object, _settingsMock.Object);

        _galeriaVm = new GaleriaViewModel(
            _trackingMock.Object,
            _dbMock.Object,
            _authMock.Object,
            new Mock<IDialogService>().Object,
            new Mock<IHttpClientFactory>().Object,
            new Mock<IImageCacheService>().Object,
            new Mock<IFileScannerService>().Object);
        _navigationServiceMock.Setup(n => n.ObtenerGaleria()).Returns(_galeriaVm);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { if (Directory.Exists(_tempFolder)) Directory.Delete(_tempFolder, true); } catch { }
    }

    private MainViewModel CreateMainVm()
    {
        return new MainViewModel(
            _navigationServiceMock.Object,
            _trackingMock.Object,
            _libraryService,
            _downloadMock.Object,
            _updateMock.Object);
    }

    private ReproductorViewModel CreateReproductorVm()
    {
        return new ReproductorViewModel(
            _dbMock.Object,
            _trackingMock.Object,
            _authMock.Object);
    }

    [Fact]
    public void AlternarModoMini_DeberiaAlternarFlagEsModoMini()
    {
        // Arrange
        using var repVm = CreateReproductorVm();
        repVm.EsModoMini.Should().BeFalse();

        // Act 1: pasar a mini
        repVm.AlternarModoMiniCommand.Execute(null);

        // Assert 1
        repVm.EsModoMini.Should().BeTrue();

        // Act 2: volver de mini
        repVm.AlternarModoMiniCommand.Execute(null);

        // Assert 2
        repVm.EsModoMini.Should().BeFalse();
    }

    [Fact]
    public void MinimizarAMini_DeberiaActivarEsModoMini()
    {
        // Arrange
        using var repVm = CreateReproductorVm();
        repVm.EsModoMini.Should().BeFalse();

        // Act
        repVm.MinimizarAMiniCommand.Execute(null);

        // Assert
        repVm.EsModoMini.Should().BeTrue();
    }

    [Fact]
    public void RestaurarFormatoHabitual_DeberiaDesactivarEsModoMini()
    {
        // Arrange
        using var repVm = CreateReproductorVm();
        repVm.EsModoMini = true;

        // Act
        repVm.RestaurarFormatoHabitualCommand.Execute(null);

        // Assert
        repVm.EsModoMini.Should().BeFalse();
    }

    [Fact]
    public void Cerrar_DeberiaDesactivarModoMiniYDisponer()
    {
        // Arrange
        using var repVm = CreateReproductorVm();
        repVm.EsModoMini = true;

        // Act
        repVm.CerrarCommand.Execute(null);

        // Assert
        repVm.EsModoMini.Should().BeFalse();
    }

    [Fact]
    public void Receive_VolverDelReproductor_DeberiaLimpiarReproductorActivo()
    {
        // Arrange
        var mainVm = CreateMainVm();
        using var repVm = CreateReproductorVm();
        mainVm.ReproductorActivo = repVm;

        // Act
        mainVm.Receive(new NavegarMensaje_VolverDelReproductor());

        // Assert
        mainVm.ReproductorActivo.Should().BeNull();
        mainVm.VistaActual.Should().BeSameAs(_galeriaVm);
    }

    [Fact]
    public void OnVistaActualChanged_ConReproductorActivoEnFormatoHabitual_DeberiaPasarAModoMini()
    {
        // Arrange
        var mainVm = CreateMainVm();
        using var repVm = CreateReproductorVm();
        repVm.EsModoMini = false;
        mainVm.ReproductorActivo = repVm;

        var mockDescargasVm = new DescargasViewModel(_downloadMock.Object);
        _navigationServiceMock.Setup(n => n.ObtenerDescargas()).Returns(mockDescargasVm);

        // Act: usuario cambia a pestaña de descargas mientras el reproductor está abierto
        mainVm.NavegarDescargasCommand.Execute(null);

        // Assert: no se interrumpe la reproducción, sino que se acopla a la esquina
        repVm.EsModoMini.Should().BeTrue();
        mainVm.VistaActual.Should().BeSameAs(mockDescargasVm);
    }
}
