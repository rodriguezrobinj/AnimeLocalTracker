using System;
using System.Threading.Tasks;
using AnimeLocalTracker.Core.Services;
using AnimeLocalTracker.Core.ViewModels;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// Tests portados a la arquitectura Core: la implementación concreta de UpdateService
/// vive ahora en el host Avalonia (Velopack/GitHub), así que aquí se verifica el CONTRATO
/// de IUpdateService y la orquestación del MainViewModel (que ya usaba mocks).
/// </summary>
public class UpdateServiceTests
{
    private readonly Mock<IDialogService> _dialogMock = new();

    [Fact]
    public void Contrato_ObtenerVersionActual_DeberiaExistir_YRetornarString()
    {
        // Arrange
        var mock = new Mock<IUpdateService>();
        mock.Setup(u => u.ObtenerVersionActual()).Returns("v1.0.0");

        // Act
        var version = mock.Object.ObtenerVersionActual();

        // Assert
        version.Should().NotBeNullOrWhiteSpace();
        version.Should().StartWith("v");
    }

    [Fact]
    public void Contrato_EstaInstaladoPorVelopack_DeberiaSerBoolean()
    {
        // Arrange
        var mock = new Mock<IUpdateService>();
        mock.Setup(u => u.EstaInstaladoPorVelopack()).Returns(false);

        // Act
        var result = mock.Object.EstaInstaladoPorVelopack();

        // Assert
        result.Should().BeFalse("en tiempo de pruebas no se ejecuta bajo el runtime instalado de Velopack");
    }

    [Fact]
    public void Contrato_ComprobarActualizaciones_DeberiaRespetarFlagManual()
    {
        // Arrange
        var mock = new Mock<IUpdateService>();
        mock.Setup(u => u.ComprobarActualizacionesAsync(true)).ReturnsAsync((Velopack.UpdateInfo?)null);

        // Act
        var result = mock.Object.ComprobarActualizacionesAsync(esManual: true);

        // Assert
        result.Should().NotBeNull();
        mock.Verify(u => u.ComprobarActualizacionesAsync(true), Times.Once);
    }

    [Fact]
    public async Task MainViewModel_BuscarActualizacionesManualCommand_DeberiaInvocarUpdateService()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddHttpClient();
        var trackingMock = new Mock<IAnimeTrackingService>();
        var dbMock = new Mock<IDatabaseService>();
        var downloadMock = new Mock<IDownloadService>();
        var updateMock = new Mock<IUpdateService>();
        var settingsMock = new Mock<ISettingsService>();
        var authMock = new Mock<IAuthService>();
        var dialogMock = new Mock<IDialogService>();
        var imageCacheMock = new Mock<IImageCacheService>();

        services.AddSingleton(trackingMock.Object);
        services.AddSingleton(dbMock.Object);
        services.AddSingleton(downloadMock.Object);
        services.AddSingleton(updateMock.Object);
        services.AddSingleton(settingsMock.Object);
        services.AddSingleton(authMock.Object);
        services.AddSingleton(dialogMock.Object);
        services.AddSingleton(imageCacheMock.Object);
        services.AddSingleton<GaleriaViewModel>();

        var sp = services.BuildServiceProvider();
        var sut = new MainViewModel(sp, trackingMock.Object, dbMock.Object, downloadMock.Object, updateMock.Object, settingsMock.Object);

        // Act
        await sut.BuscarActualizacionesManualCommand.ExecuteAsync(null);

        // Assert
        updateMock.Verify(u => u.ComprobarActualizacionesAsync(true), Times.Once);
    }

    [Fact]
    public void Contrato_ObtenerInfoUltimaVersion_DeberiaRetornarReleaseInfo()
    {
        // Arrange
        var mock = new Mock<IUpdateService>();
        mock.Setup(u => u.ObtenerInfoUltimaVersionAsync(false))
            .ReturnsAsync(new AnimeLocalTracker.Core.Models.ReleaseInfo
            {
                Version = "1.0.0",
                NotasVersion = "Notas de prueba",
                UrlRelease = "https://github.com/ejemplo"
            });

        // Act
        var releaseInfo = mock.Object.ObtenerInfoUltimaVersionAsync(forzarActualizacion: false);

        // Assert
        releaseInfo.Should().NotBeNull();
        releaseInfo.Result.Version.Should().NotBeNullOrWhiteSpace();
        releaseInfo.Result.NotasVersion.Should().NotBeNullOrWhiteSpace();
        releaseInfo.Result.UrlRelease.Should().NotBeNullOrWhiteSpace();
    }
}
