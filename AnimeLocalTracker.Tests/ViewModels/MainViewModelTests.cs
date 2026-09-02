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
public class MainViewModelTests : IDisposable
{
    private readonly Mock<INavigationService> _navigationServiceMock = new();
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

        // Vista por defecto al arrancar: el ctor pide la Galería al NavigationService
        _galeriaVm = new GaleriaViewModel(
            _trackingMock.Object,
            _dbMock.Object,
            new Mock<IAuthService>().Object,
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

    private MainViewModel CreateSut()
    {
        return new MainViewModel(
            _navigationServiceMock.Object,
            _trackingMock.Object,
            _libraryService,
            _downloadMock.Object,
            _updateMock.Object);
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

    // === NAVEGACIÓN ===

    [Fact]
    public void Receive_Descargas_DeberiaCambiarLaVistaActual()
    {
        // Arrange
        var descargasVm = new DescargasViewModel(_downloadMock.Object);
        _navigationServiceMock.Setup(n => n.ObtenerDescargas()).Returns(descargasVm);
        var sut = CreateSut();

        // Act
        sut.Receive(new AnimeLocalTracker.Messages.NavegarMensaje_Descargas());

        // Assert
        sut.VistaActual.Should().BeSameAs(descargasVm);
    }

    [Fact]
    public void Receive_Galeria_DeberiaCambiarALaGaleria()
    {
        // Arrange
        var sut = CreateSut();
        var otra = new DescargasViewModel(_downloadMock.Object);
        _navigationServiceMock.Setup(n => n.ObtenerDescargas()).Returns(otra);
        sut.Receive(new AnimeLocalTracker.Messages.NavegarMensaje_Descargas());
        sut.VistaActual.Should().BeSameAs(otra);

        // Act
        sut.Receive(new AnimeLocalTracker.Messages.NavegarMensaje_Galeria());

        // Assert
        sut.VistaActual.Should().BeSameAs(_galeriaVm);
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
        sut.ToastVisible.Should().BeTrue("se muestra el toast de confirmación");
    }

    [Fact]
    public async Task SeleccionarYCrearAnime_YaExiste_DeberiaNoGuardar()
    {
        // Arrange
        _dbMock.Setup(d => d.ObtenerTodosLosAnimesAsync())
            .ReturnsAsync(new List<AnimeItem> { new() { AniListId = 999, Titulo = "Test Anime" } });
        var sut = CreateSut();

        // Act
        await sut.SeleccionarYCrearAnimeCommand.ExecuteAsync(CrearMedia(999, "Test Anime"));

        // Assert
        _dbMock.Verify(d => d.GuardarAnimeAsync(It.IsAny<AnimeItem>()), Times.Never);
        sut.ToastVisible.Should().BeTrue();
        sut.ToastTitulo.Should().Be("Anime Existente");
    }

    [Fact]
    public void VersionAppTexto_DeberiaDelegarEnElUpdateService()
    {
        // Act
        var sut = CreateSut();

        // Assert
        sut.VersionAppTexto.Should().Be("1.0.0-test");
    }
}
