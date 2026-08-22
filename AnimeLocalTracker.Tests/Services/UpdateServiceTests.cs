using System;
using System.Threading.Tasks;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

public class UpdateServiceTests
{
    private readonly Mock<IDialogService> _dialogMock = new();

    [Fact]
    public void UpdateService_ObtenerVersionActual_DeberiaRetornarFormatoValido()
    {
        // Arrange
        var sut = new UpdateService(_dialogMock.Object);

        // Act
        var version = sut.ObtenerVersionActual();

        // Assert
        version.Should().NotBeNullOrWhiteSpace();
        version.Should().StartWith("v");
    }

    [Fact]
    public void UpdateService_EstaInstaladoPorVelopack_EnTestRunner_DeberiaRetornarFalse()
    {
        // Arrange
        var sut = new UpdateService(_dialogMock.Object);

        // Act
        var isInstalled = sut.EstaInstaladoPorVelopack();

        // Assert
        isInstalled.Should().BeFalse("en tiempo de pruebas o depuración la aplicación no se ejecuta bajo el runtime instalado de Velopack");
    }

    [Fact]
    public async Task UpdateService_ComprobarActualizacionesManual_EnModoDesarrollo_DeberiaNotificarAlUsuario()
    {
        // Arrange
        var sut = new UpdateService(_dialogMock.Object);

        // Act
        var result = await sut.ComprobarActualizacionesAsync(esManual: true);

        // Assert
        result.Should().BeNull();
        _dialogMock.Verify(d => d.MostrarDialogoAsync(
            "Actualizaciones",
            It.Is<string>(s => s.Contains("modo de desarrollo")),
            false,
            "CodeTags",
            "#9C27B0"), Times.Once);
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
    public async Task UpdateService_ObtenerInfoUltimaVersionAsync_DeberiaRetornarInformacionValida()
    {
        // Arrange
        var sut = new UpdateService(_dialogMock.Object);

        // Act
        var releaseInfo = await sut.ObtenerInfoUltimaVersionAsync(forzarActualizacion: false);

        // Assert
        releaseInfo.Should().NotBeNull();
        releaseInfo.Version.Should().NotBeNullOrWhiteSpace();
        releaseInfo.NotasVersion.Should().NotBeNullOrWhiteSpace();
        releaseInfo.UrlRelease.Should().NotBeNullOrWhiteSpace();
    }
}
