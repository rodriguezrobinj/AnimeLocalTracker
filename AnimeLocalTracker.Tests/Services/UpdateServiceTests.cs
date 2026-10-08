using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

[Collection("NavigationServiceTests")]
public class UpdateServiceTests : IDisposable
{
    private const string RespuestaGitHub =
        """{"tag_name":"v9.9.9","name":"Version de prueba","body":"Notas de prueba","html_url":"https://example.invalid/release","published_at":"2026-01-02T03:04:05Z"}""";

    private readonly Mock<IDialogService> _dialogMock = new();

    // La caché de la release va a una carpeta temporal: con la ruta por defecto (AppDataPaths) estas pruebas leían y
    // escribían release_info.json en los datos reales del usuario.
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), "ALT_update_" + Guid.NewGuid().ToString("N"));
    private string RutaCache => Path.Combine(_carpeta, "release_info.json");

    public void Dispose()
    {
        ArchivosTemporales.BorrarCarpeta(_carpeta);
        GC.SuppressFinalize(this);
    }

    private UpdateService Crear(HttpMessageHandler? red = null)
        => new(_dialogMock.Object, red == null ? null : new HttpClient(red), RutaCache);

    /// <summary>Sustituye a GitHub: responde lo que se le diga y cuenta las peticiones (ninguna prueba sale a internet).</summary>
    private sealed class RedFalsa(Func<HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int Peticiones { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Peticiones++;
            return Task.FromResult(responder());
        }
    }

    private static RedFalsa GitHubResponde() => new(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(RespuestaGitHub) });

    [Fact]
    public void UpdateService_ObtenerVersionActual_DeberiaRetornarFormatoValido()
    {
        // Arrange
        var sut = Crear();

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
        var sut = Crear();

        // Act
        var isInstalled = sut.EstaInstaladoPorVelopack();

        // Assert
        isInstalled.Should().BeFalse("en tiempo de pruebas o depuración la aplicación no se ejecuta bajo el runtime instalado de Velopack");
    }

    [Fact]
    public async Task UpdateService_ComprobarActualizacionesManual_EnModoDesarrollo_DeberiaNotificarAlUsuario()
    {
        // Arrange
        var sut = Crear();

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
        services.AddSingleton<AnimeLibraryService>();

        var sp = services.BuildServiceProvider();
        var navigationService = new NavigationService(sp);
        try
        {
            var sut = new MainViewModel(navigationService, trackingMock.Object, sp.GetRequiredService<AnimeLibraryService>(), downloadMock.Object, updateMock.Object, dialogMock.Object,
                Mock.Of<ISelectorTorrentService>(),
                Mock.Of<IDatabaseService>(), Mock.Of<IFileScannerService>(),
                new NewEpisodeNotifier(Mock.Of<IDatabaseService>(), Mock.Of<IFileScannerService>(), Mock.Of<ISettingsService>()),
                Mock.Of<ISystemTrayService>());

            // Act
            await sut.BuscarActualizacionesManualCommand.ExecuteAsync(null);

            // Assert
            updateMock.Verify(u => u.ComprobarActualizacionesAsync(true), Times.Once);
        }
        finally
        {
            // NavigationService se registra en WeakReferenceMessenger.Default (estático de
            // proceso) en su constructor — sin este unregister, la instancia sigue viva y
            // puede recibir mensajes de OTROS tests que corran después, lanzando excepciones
            // porque este ServiceProvider de prueba no tiene todos los ViewModels registrados
            // (fue la causa raíz confirmada de un fallo intermitente en CI: DescargasViewModel/
            // GaleriaViewModel sin registrar aquí, alcanzados por un mensaje de otro test).
            CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.UnregisterAll(navigationService);
        }
    }

    [Fact]
    public async Task ObtenerInfoUltimaVersionAsync_SinCache_ConsultaLaReleaseYLaGuardaEnLaRutaIndicada()
    {
        var red = GitHubResponde();
        var sut = Crear(red);

        var releaseInfo = await sut.ObtenerInfoUltimaVersionAsync(forzarActualizacion: false);

        releaseInfo.Version.Should().Be("v9.9.9");
        releaseInfo.Titulo.Should().Be("Version de prueba");
        releaseInfo.NotasVersion.Should().Be("Notas de prueba");
        releaseInfo.UrlRelease.Should().Be("https://example.invalid/release");
        red.Peticiones.Should().Be(1);
        File.Exists(RutaCache).Should().BeTrue("la caché se guarda en la ruta que recibe el servicio, no en los datos del usuario");
    }

    [Fact]
    public async Task ObtenerInfoUltimaVersionAsync_ConCacheGuardada_NoVuelveAConsultarLaRed()
    {
        var red = GitHubResponde();
        var sut = Crear(red);
        await sut.ObtenerInfoUltimaVersionAsync(forzarActualizacion: false);

        var releaseInfo = await sut.ObtenerInfoUltimaVersionAsync(forzarActualizacion: false);

        releaseInfo.Version.Should().Be("v9.9.9");
        red.Peticiones.Should().Be(1);
    }

    [Fact]
    public async Task ObtenerInfoUltimaVersionAsync_SinRedNiCache_DevuelveLaInformacionPorDefectoSinGuardarNada()
    {
        var sut = Crear(new RedFalsa(() => throw new HttpRequestException("sin conexión")));

        var releaseInfo = await sut.ObtenerInfoUltimaVersionAsync(forzarActualizacion: false);

        releaseInfo.Version.Should().StartWith("v");
        releaseInfo.NotasVersion.Should().NotBeNullOrWhiteSpace();
        File.Exists(RutaCache).Should().BeFalse();
    }
}
