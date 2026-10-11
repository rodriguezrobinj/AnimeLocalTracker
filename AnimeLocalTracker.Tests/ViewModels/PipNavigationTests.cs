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

[Collection("NavigationServiceTests")]
public class PipNavigationTests : IDisposable
{
    private readonly Mock<IServiceProvider> _spMock = new();
    private readonly NavigationService _navigationService;
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

    private MainViewModel CreateMainVm(IDialogService? dialogo = null)
    {
        return new MainViewModel(
            _navigationService,
            _trackingMock.Object,
            _libraryService,
            _downloadMock.Object,
            _updateMock.Object,
            dialogo ?? new Mock<IDialogService>().Object,
            Mock.Of<ISelectorTorrentService>(),
            Mock.Of<IDatabaseService>(),
            Mock.Of<IFileScannerService>(),
            new NewEpisodeNotifier(Mock.Of<IDatabaseService>(), Mock.Of<IFileScannerService>(), Mock.Of<ISettingsService>()),
            Mock.Of<ISystemTrayService>());
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
        mainVm.Navigation.ReproductorActivo = repVm;

        // Act
        CommunityToolkit.Mvvm.Messaging.IMessengerExtensions.Send(CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default, new NavegarMensaje_VolverDelReproductor());

        // Assert
        mainVm.Navigation.ReproductorActivo.Should().BeNull();
        mainVm.Navigation.VistaActual.Should().BeSameAs(_galeriaVm);
    }

    [Fact]
    public void OnVistaActualChanged_ConReproductorActivoEnFormatoHabitual_DeberiaPasarAModoMini()
    {
        // Arrange
        var mainVm = CreateMainVm();
        using var repVm = CreateReproductorVm();
        repVm.EsModoMini = false;
        mainVm.Navigation.ReproductorActivo = repVm;

        var mockDescargasVm = new DescargasViewModel(_downloadMock.Object);
        _spMock.Setup(sp => sp.GetService(typeof(DescargasViewModel))).Returns(mockDescargasVm);

        // Act: usuario cambia a pestaa de descargas mientras el reproductor estǭ abierto
        Pestanas.Descargas.Abrir();

        // Assert: no se interrumpe la reproduccin, sino que se acopla a la esquina
        repVm.EsModoMini.Should().BeTrue();
        mainVm.Navigation.VistaActual.Should().BeSameAs(mockDescargasVm);
    }

    // ── Mini reproductor en su propia ventana ──

    [Fact]
    public void EnModoMini_LaVentanaPrincipalNoDibujaElReproductor_YAlRestaurarVuelveADibujarlo()
    {
        var mainVm = CreateMainVm();
        using var repVm = CreateReproductorVm();
        mainVm.Navigation.ReproductorActivo = repVm;
        mainVm.Navigation.ReproductorEnVentanaPrincipal.Should().BeSameAs(repVm);
        var avisos = new List<string?>();
        mainVm.Navigation.PropertyChanged += (_, e) => avisos.Add(e.PropertyName);

        repVm.MinimizarAMiniCommand.Execute(null);
        mainVm.Navigation.ReproductorEnVentanaPrincipal.Should().BeNull("el video pasa a la ventana flotante y la app queda libre");
        mainVm.Navigation.ReproductorActivo.Should().BeSameAs(repVm, "el episodio sigue activo");

        repVm.RestaurarFormatoHabitualCommand.Execute(null);
        mainVm.Navigation.ReproductorEnVentanaPrincipal.Should().BeSameAs(repVm);
        avisos.Should().Contain(nameof(INavigationService.ReproductorEnVentanaPrincipal));
    }

    // ── El aviso de la ventana principal y el del reproductor no se pisan ──

    [Theory]
    [InlineData(false, false, false, false, false)] // no hay aviso
    [InlineData(true, false, false, false, true)]   // sin reproductor: como siempre
    [InlineData(true, true, false, false, true)]    // aviso del reproductor que llega con él ya cerrado (visto, espacio liberado)
    [InlineData(true, true, true, false, false)]    // video en la ventana: lo dibuja su vista, arriba (antes salía dos veces)
    [InlineData(true, false, true, false, false)]   // video en la ventana: los de fuera no interrumpen el episodio
    [InlineData(true, true, true, true, false)]     // mini reproductor: el suyo lo dibuja su ventana
    [InlineData(true, false, true, true, true)]     // mini reproductor: los de fuera, en la app
    public void ElAvisoDeLaVentanaPrincipal_SoloSeVeSiNoLoDibujaElReproductorNiInterrumpeElEpisodio(
        bool visible, bool delReproductor, bool hayReproductor, bool enMini, bool esperado)
    {
        MainViewModel.DebeVerseElToast(visible, delReproductor, hayReproductor, enMini).Should().Be(esperado);
    }

    [Fact]
    public void ToastEnVentana_SigueAlAvisoYAlReproductor()
    {
        var dialogo = new Mock<IDialogService>();
        dialogo.SetupGet(d => d.ToastVisible).Returns(true);
        var mainVm = CreateMainVm(dialogo.Object);
        using var repVm = CreateReproductorVm();
        var avisos = new List<string?>();
        mainVm.PropertyChanged += (_, e) => avisos.Add(e.PropertyName);
        mainVm.ToastEnVentana.Should().BeTrue();

        mainVm.Navigation.ReproductorActivo = repVm;
        mainVm.ToastEnVentana.Should().BeFalse("el video ocupa la ventana");
        avisos.Should().Contain(nameof(MainViewModel.ToastEnVentana));

        avisos.Clear();
        repVm.MinimizarAMiniCommand.Execute(null);
        mainVm.ToastEnVentana.Should().BeTrue("con el video en el mini, un aviso de fuera se ve en la app");
        avisos.Should().Contain(nameof(MainViewModel.ToastEnVentana));

        avisos.Clear();
        dialogo.SetupGet(d => d.ToastDelReproductor).Returns(true);
        dialogo.Raise(d => d.PropertyChanged += null, new System.ComponentModel.PropertyChangedEventArgs(nameof(IDialogService.ToastDelReproductor)));
        mainVm.ToastEnVentana.Should().BeFalse("el del reproductor ya lo dibuja la ventana mini");
        avisos.Should().Contain(nameof(MainViewModel.ToastEnVentana));

        mainVm.Navigation.ReproductorActivo = null;
        mainVm.ToastEnVentana.Should().BeFalse("el reproductor ya lo dibujó: al cerrarlo no se repite abajo");

        // Un aviso del reproductor que llega con él ya cerrado (espacio liberado al salir) sí se ve en la app.
        avisos.Clear();
        dialogo.Raise(d => d.PropertyChanged += null, new System.ComponentModel.PropertyChangedEventArgs(nameof(IDialogService.ToastMensaje)));
        mainVm.ToastEnVentana.Should().BeTrue();
        avisos.Should().Contain(nameof(MainViewModel.ToastEnVentana));
    }

    [Fact]
    public void NavegarConElMiniAbierto_YCerrarlo_SeQuedaEnLaVistaAdondeFueElUsuario()
    {
        var mainVm = CreateMainVm();
        using var repVm = CreateReproductorVm();
        mainVm.Navigation.ReproductorActivo = repVm;
        var descargasVm = new DescargasViewModel(_downloadMock.Object);
        _spMock.Setup(sp => sp.GetService(typeof(DescargasViewModel))).Returns(descargasVm);

        Pestanas.Descargas.Abrir(); // el episodio pasa al mini y el usuario sigue por Descargas
        CommunityToolkit.Mvvm.Messaging.IMessengerExtensions.Send(CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default, new NavegarMensaje_VolverDelReproductor());

        mainVm.Navigation.ReproductorActivo.Should().BeNull();
        mainVm.Navigation.VistaActual.Should().BeSameAs(descargasVm, "antes lo devolvía a la ficha desde la que abrió el episodio");
    }

    [Fact]
    public void RestaurarDesdeElMini_TraeLaVentanaPrincipalAlFrente()
    {
        var ventana = new Mock<IVentanaPrincipal>();
        using var repVm = new ReproductorViewModel(_dbMock.Object, _trackingMock.Object, _authMock.Object, ventanaPrincipal: ventana.Object);
        repVm.MinimizarAMiniCommand.Execute(null);

        repVm.RestaurarFormatoHabitualCommand.Execute(null);

        ventana.Verify(v => v.SalirModoPiP(), Times.Once);
        ventana.Verify(v => v.MostrarYActivar(), Times.Once, "si la app estaba minimizada, al restaurar se tiene que ver el episodio");
    }
}
