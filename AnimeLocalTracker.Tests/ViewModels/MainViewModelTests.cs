using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>
/// DEV-06: MainViewModel (el más grande y sin cubrir): navegación entre vistas,
/// búsqueda en vivo con debounce y alta de anime vía AnimeLibraryService.
/// </summary>
[Collection("NavigationServiceTests")]
public class MainViewModelTests : IDisposable
{
    private readonly Mock<IServiceProvider> _spMock = new();
    private readonly NavigationService _navigationService;
    private readonly Mock<IAnimeTrackingService> _trackingMock = new();
    private readonly Mock<IDownloadService> _downloadMock = new();
    private readonly Mock<IUpdateService> _updateMock = new();
    private readonly Mock<IDatabaseService> _dbMock = new();
    private readonly Mock<ISettingsService> _settingsMock = new();
    private readonly AnimeLibraryService _libraryService;
    private readonly GaleriaViewModel _galeriaVm;
    private readonly string _tempFolder;

    public MainViewModelTests()
    {
        _tempFolder = Path.Combine(Path.GetTempPath(), "AnimeLocalTracker_Main_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempFolder);
        _settingsMock.Setup(s => s.ObtenerRutaBaseAnimes()).Returns(_tempFolder);
        _updateMock.Setup(u => u.ObtenerVersionActual()).Returns("1.0.0-test");

        _dbMock.Setup(d => d.ObtenerTodosLosAnimesAsync()).ReturnsAsync(new List<AnimeItem>());
        _downloadMock.Setup(d => d.ObtenerDescargasActivas()).Returns(new List<DescargaItem>());
        _libraryService = new AnimeLibraryService(_dbMock.Object, _settingsMock.Object);

        // Vista por defecto al arrancar: el ctor pide la Galera al NavigationService
        _galeriaVm = new GaleriaViewModel(
            _trackingMock.Object,
            _dbMock.Object,
            new Mock<IAuthService>().Object,
            new Mock<IDialogService>().Object,
            new Mock<IHttpClientFactory>().Object,
            new Mock<IImageCacheService>().Object,
            new Mock<IFileScannerService>().Object);

        _spMock.Setup(sp => sp.GetService(typeof(GaleriaViewModel))).Returns(_galeriaVm);
        _navigationService = new NavigationService(_spMock.Object);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { if (Directory.Exists(_tempFolder)) Directory.Delete(_tempFolder, true); } catch { }
        CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.UnregisterAll(this);
        CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.UnregisterAll(_navigationService);
    }

    private MainViewModel CreateSut(IDialogService? dialogService = null)
    {
        return new MainViewModel(
            _navigationService,
            _trackingMock.Object,
            _libraryService,
            _downloadMock.Object,
            _updateMock.Object,
            dialogService ?? new Mock<IDialogService>().Object,
            Mock.Of<ISelectorTorrentService>(),
            Mock.Of<IDatabaseService>(),
            Mock.Of<IFileScannerService>(),
            new NewEpisodeNotifier(Mock.Of<IDatabaseService>(), Mock.Of<IFileScannerService>(), Mock.Of<ISettingsService>()),
            Mock.Of<ISystemTrayService>());
    }

    private static AniListMedia CrearMedia(int id, string titulo)
    {
        return new AniListMedia
        {
            Id = id,
            Title = new AniListTitle { Romaji = titulo, English = titulo },
            Status = "FINISHED",
            Episodes = 12,
            CoverImage = new AniListCoverImage { ExtraLarge = "https://example.com/cover.jpg" }
        };
    }

    // === NAVEGACION ===

    [Fact]
    public void Receive_Descargas_DeberiaCambiarLaVistaActual()
    {
        // Arrange
        var descargasVm = new DescargasViewModel(_downloadMock.Object);
        _spMock.Setup(sp => sp.GetService(typeof(DescargasViewModel))).Returns(descargasVm);
        var sut = CreateSut();

        // Act
        CommunityToolkit.Mvvm.Messaging.IMessengerExtensions.Send(CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default, new AnimeLocalTracker.Messages.NavegarMensaje_Descargas());

        // Assert
        sut.Navigation.VistaActual.Should().BeSameAs(descargasVm);
    }

    [Fact]
    public void Receive_Galeria_DeberiaCambiarALaGaleria()
    {
        // Arrange
        var sut = CreateSut();
        var otra = new DescargasViewModel(_downloadMock.Object);
        _spMock.Setup(sp => sp.GetService(typeof(DescargasViewModel))).Returns(otra);
        CommunityToolkit.Mvvm.Messaging.IMessengerExtensions.Send(CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default, new AnimeLocalTracker.Messages.NavegarMensaje_Descargas());
        sut.Navigation.VistaActual.Should().BeSameAs(otra);

        _spMock.Setup(sp => sp.GetService(typeof(GaleriaViewModel))).Returns(_galeriaVm);

        // Act
        CommunityToolkit.Mvvm.Messaging.IMessengerExtensions.Send(CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default, new AnimeLocalTracker.Messages.NavegarMensaje_Galeria());

        // Assert
        sut.Navigation.VistaActual.Should().BeSameAs(_galeriaVm);
    }

    // === BÚSQUEDA EN VIVO (debounce 400 ms) ===

    [Fact]
    public async Task BusquedaEnVivo_DeberiaPoblarResultadosTrasElDebounce()
    {
        // Arrange
        var resultados = new List<AniListMedia> { CrearMedia(1, "One Piece"), CrearMedia(2, "One Punch Man") };
        _trackingMock
            .Setup(t => t.BuscarAnimesEnVivoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(resultados);
        var sut = CreateSut();

        // Act
        sut.TextoBusqueda = "one";

        // Esperar debounce (400 ms) + llamada
        await Task.Delay(800);

        // Assert
        sut.ResultadosBusqueda.Should().HaveCount(2);
        sut.BusquedaSinResultados.Should().BeFalse();
    }

    [Fact]
    public async Task BusquedaEnVivo_SinResultados_DeberiaMarcarBusquedaSinResultados()
    {
        // Arrange
        _trackingMock
            .Setup(t => t.BuscarAnimesEnVivoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AniListMedia>());
        var sut = CreateSut();

        // Act
        sut.TextoBusqueda = "zzz";

        await Task.Delay(800);

        // Assert
        sut.ResultadosBusqueda.Should().BeEmpty();
        sut.BusquedaSinResultados.Should().BeTrue();
    }

    [Fact]
    public async Task BusquedaEnVivo_TerminoMenorA3Caracteres_DeberiaLimpiarResultados()
    {
        // Arrange
        _trackingMock
            .Setup(t => t.BuscarAnimesEnVivoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AniListMedia> { CrearMedia(1, "One Piece") });
        var sut = CreateSut();
        sut.TextoBusqueda = "onepiece"; // >= 3: dispara búsqueda
        await Task.Delay(800);
        sut.ResultadosBusqueda.Should().NotBeEmpty();

        // Act: texto corto cancela y limpia
        sut.TextoBusqueda = "ab";

        // Assert
        sut.ResultadosBusqueda.Should().BeEmpty();
        sut.IsSearching.Should().BeFalse();
    }

    // === ALTA DE ANIME (AnimeLibraryService) ===

    [Fact]
    public async Task SeleccionarYCrearAnime_AnimeNuevo_DeberiaGuardarYCrearCarpeta()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        await sut.SeleccionarYCrearAnimeCommand.ExecuteAsync(CrearMedia(999, "Test Anime"));

        // Assert: persistido con los campos correctos
        // Nota: el espacio NO es un carácter inválido en Windows → la carpeta conserva el espacio
        _dbMock.Verify(d => d.GuardarAnimeAsync(It.Is<AnimeItem>(a =>
            a.AniListId == 999 &&
            a.Titulo == "Test Anime" &&
            a.MalId == null &&
            a.TotalEpisodios == 12 &&
            a.RutaCarpeta == Path.Combine(_tempFolder, "Test Anime"))), Times.Once);

        Directory.Exists(Path.Combine(_tempFolder, "Test Anime")).Should().BeTrue();
    }

    [Fact]
    public async Task SeleccionarYCrearAnime_YaExiste_DeberiaNoGuardar()
    {
        // Arrange
        _dbMock.Setup(d => d.ObtenerTodosLosAnimesAsync())
            .ReturnsAsync(new List<AnimeItem> { new() { AniListId = 999, Titulo = "Test Anime" } });
        // PERF-03: la comprobación de existencia ya no carga la biblioteca completa
        _dbMock.Setup(d => d.ExisteAnimeAsync(It.IsAny<int>())).ReturnsAsync(true);
        var sut = CreateSut();

        // Act
        await sut.SeleccionarYCrearAnimeCommand.ExecuteAsync(CrearMedia(999, "Test Anime"));

        // Assert
        _dbMock.Verify(d => d.GuardarAnimeAsync(It.IsAny<AnimeItem>()), Times.Never);
    }

    [Fact]
    public void VersionAppTexto_DeberiaDelegarEnElUpdateService()
    {
        // Act
        var sut = CreateSut();

        // Assert
        sut.VersionAppTexto.Should().Be("1.0.0-test");
    }

    // ───────────── Avisos pedidos por mensaje (reproductor / calendario) ─────────────

    [Fact]
    public void MainViewModel_QuedaRegistradoComoReceptorDeAvisos()
    {
        // Regresión: un refactor de IDialogService dejó a MainViewModel sin registrar y los avisos
        // del reproductor y del calendario se enviaban al vacío (nadie los mostraba).
        var sut = CreateSut();

        CommunityToolkit.Mvvm.Messaging.IMessenger mensajero = CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default;
        CommunityToolkit.Mvvm.Messaging.IMessengerExtensions.IsRegistered<Messages.MostrarDialogoRequestMessage>(mensajero, sut)
            .Should().BeTrue();
    }

    [Fact]
    public async Task Receive_MostrarDialogoRequest_MuestraElAvisoPorIDialogService()
    {
        var dialogMock = new Mock<IDialogService>();
        dialogMock.Setup(d => d.MostrarDialogoAsync("Auto-Tracking", "Episodio 5 marcado como visto.", false, "CheckCircle", "#4CAF50"))
            .ReturnsAsync(true);
        var sut = CreateSut(dialogMock.Object);
        var mensaje = new Messages.MostrarDialogoRequestMessage("Auto-Tracking", "Episodio 5 marcado como visto.", false, "CheckCircle", "#4CAF50");

        sut.Receive(mensaje);

        mensaje.HasReceivedResponse.Should().BeTrue("quien envía el mensaje espera una respuesta");
        (await mensaje.Response).Should().BeTrue();
        dialogMock.Verify(d => d.MostrarDialogoAsync("Auto-Tracking", "Episodio 5 marcado como visto.", false, "CheckCircle", "#4CAF50"), Times.Once);
    }

    [Fact]
    public async Task Receive_MostrarDialogoRequest_LaConfirmacionDevuelveLaRespuestaDelUsuario()
    {
        var dialogMock = new Mock<IDialogService>();
        dialogMock.Setup(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(false);
        var sut = CreateSut(dialogMock.Object);
        var mensaje = new Messages.MostrarDialogoRequestMessage("Borrar", "¿Seguro?", true, "Alert", "#EF4444");

        sut.Receive(mensaje);

        (await mensaje.Response).Should().BeFalse();
    }

    [Fact]
    public void Receive_MostrarDialogoRequest_SiYaFueRespondidoNoResponderDeNuevo()
    {
        // MainViewModel es transient: con varias instancias registradas, solo la primera responde.
        var primero = new Mock<IDialogService>();
        primero.Setup(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
        var segundo = new Mock<IDialogService>();
        var sutA = CreateSut(primero.Object);
        var sutB = CreateSut(segundo.Object);
        var mensaje = new Messages.MostrarDialogoRequestMessage("T", "M", false, "InformationOutline", "#3F51B5");

        sutA.Receive(mensaje);
        var act = () => sutB.Receive(mensaje);

        act.Should().NotThrow();
        segundo.Verify(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }
}
