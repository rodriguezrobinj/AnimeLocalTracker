using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>
/// Mejoras del reproductor (investigación 2026-09-29): Esc a pantalla completa, salida única del reproductor, subtítulos por
/// defecto cuando el archivo no marca ninguna pista y el aviso de progreso de cada 3 s sin rehacer la Ficha ni releer la BD.
/// </summary>
public class ReproductorMejorasTests
{
    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<IAnimeTrackingService> _tracking = new();
    private readonly Mock<IAuthService> _auth = new();
    private readonly Mock<IPlaybackWindowModeCoordinator> _ventana = new();

    private ReproductorViewModel CrearSut() => new(
        _db.Object, _tracking.Object, _auth.Object,
        windowModeCoordinator: _ventana.Object);

    // === Esc y salida del reproductor ===

    [Fact]
    public void Esc_APantallaCompleta_VuelveAVentana_SinCerrarElEpisodio()
    {
        _ventana.Setup(v => v.EstaEnPantallaCompleta).Returns(true);
        _ventana.Setup(v => v.AlternarPantallaCompleta()).Returns("Fullscreen");
        using var sut = CrearSut();
        bool salio = false;
        var receptor = new object();
        WeakReferenceMessenger.Default.Register<NavegarMensaje_VolverDelReproductor>(receptor, (_, _) => salio = true);

        sut.TeclaCerrar();

        _ventana.Verify(v => v.AlternarPantallaCompleta(), Times.Once);
        salio.Should().BeFalse("con Esc a pantalla completa se sale de pantalla completa, no del episodio");
        WeakReferenceMessenger.Default.UnregisterAll(receptor);
    }

    [Fact]
    public void Esc_EnVentana_CierraElReproductor()
    {
        using var sut = CrearSut();
        bool salio = false;
        var receptor = new object();
        WeakReferenceMessenger.Default.Register<NavegarMensaje_VolverDelReproductor>(receptor, (_, _) => salio = true);

        sut.TeclaCerrar();

        salio.Should().BeTrue();
        WeakReferenceMessenger.Default.UnregisterAll(receptor);
    }

    [Fact]
    public void AlCerrar_LaVentanaVuelveASuTamanoNormal()
    {
        // Antes, cerrar a pantalla completa dejaba la Ficha a pantalla completa (sin barra de tareas).
        using var sut = CrearSut();
        var receptor = new object();
        WeakReferenceMessenger.Default.Register<NavegarMensaje_VolverDelReproductor>(receptor, (_, _) => { });

        sut.CerrarCommand.Execute(null);

        _ventana.Verify(v => v.SalirDePantallaCompleta(), Times.Once);
        _ventana.Verify(v => v.SalirModoMini(), Times.AtLeastOnce);
        WeakReferenceMessenger.Default.UnregisterAll(receptor);
    }

    [Fact]
    public void LosTooltipsDeNavegacionMuestranLaTeclaConfigurada()
    {
        using var sut = CrearSut();
        var lista = new List<EpisodioItem>
        {
            new() { NumeroEpisodio = 1, RutaCompleta = "C:\\Anime\\Ep01.mkv" },
            new() { NumeroEpisodio = 2, RutaCompleta = "C:\\Anime\\Ep02.mkv" },
            new() { NumeroEpisodio = 3, RutaCompleta = "C:\\Anime\\Ep03.mkv" }
        };

        sut.CargarVideo("C:\\Anime\\Ep02.mkv", 101, "Frieren", 2, lista);

        sut.EpisodioSiguienteTooltip.Should().Contain("3").And.Contain("(N)");
        sut.EpisodioAnteriorTooltip.Should().Contain("1").And.Contain("(B)");
    }
}

/// <summary>Pista de subtítulos que se activa sola cuando el archivo no marca ninguna.</summary>
public class SubtitulosPistaPorDefectoTests
{
    private static SubtitleCoordinator.PistaSubtitulos P(string? idioma, string? titulo = null, bool imagen = false) => new(imagen, idioma, titulo);

    [Fact]
    public void PrefiereElIdiomaDeLaApp()
    {
        var pistas = new[] { P("en"), P("pt"), P("es") };

        SubtitleCoordinator.ElegirPista(pistas, "es").Should().Be(2);
    }

    [Fact]
    public void SinElIdiomaDeLaApp_PrefiereIngles()
    {
        var pistas = new[] { P("pt"), P("fr"), P("en") };

        SubtitleCoordinator.ElegirPista(pistas, "es").Should().Be(2);
    }

    [Fact]
    public void EvitaLasPistasDeSoloCartelesYCanciones()
    {
        var pistas = new[] { P("es", "Signs & Songs"), P("es", "Diálogos"), P("es", "Forzados") };

        SubtitleCoordinator.ElegirPista(pistas, "es").Should().Be(1);
    }

    [Fact]
    public void PrefiereTextoAImagen_ADigualIdioma()
    {
        var pistas = new[] { P("es", imagen: true), P("es") };

        SubtitleCoordinator.ElegirPista(pistas, "es").Should().Be(1);
    }

    [Fact]
    public void SinIdiomasConocidos_TomaLaPrimera_YSinPistasNinguna()
    {
        SubtitleCoordinator.ElegirPista(new[] { P(null), P(null) }, "es").Should().Be(0);
        SubtitleCoordinator.ElegirPista(System.Array.Empty<SubtitleCoordinator.PistaSubtitulos>(), "es").Should().Be(-1);
    }
}

/// <summary>El aviso de progreso de cada 3 s del reproductor no debe rehacer la Ficha ni releer la base de datos.</summary>
public class AvisoProgresoReproductorTests
{
    private readonly Mock<IAnimeTrackingService> _tracking = new();
    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<IAuthService> _auth = new();
    private readonly Mock<IFileScannerService> _escaner = new();
    private readonly Mock<IDialogService> _dialogos = new();
    private readonly Mock<IDownloadService> _descargas = new();

    private async Task<DetalleViewModel> AbrirFichaAsync(string filtro = "Todos")
    {
        var anime = new AnimeItem { AniListId = 7, Titulo = "Anime de prueba", RutaCarpeta = "C:\\Anime\\Prueba", TotalEpisodios = 12 };
        var archivos = Enumerable.Range(1, 12).Select(n => new EpisodioItem { NumeroEpisodio = n, RutaCompleta = $"C:\\Anime\\Prueba\\Episodio {n}.mp4" }).ToList();
        _escaner.Setup(e => e.EscanearEpisodiosAsync(anime.RutaCarpeta)).ReturnsAsync(archivos);
        _db.Setup(d => d.ObtenerRegistrosPorAnimeAsync(7)).ReturnsAsync(new List<RegistroEpisodio>());

        var sut = new DetalleViewModel(_tracking.Object, _db.Object, _auth.Object, _escaner.Object, _dialogos.Object, _descargas.Object);
        await sut.InicializarAsync(anime);
        sut.FiltroEpisodios = filtro;
        return sut;
    }

    [Fact]
    public async Task ElProgresoPeriodico_ActualizaLaFila_SinRehacerLaLista()
    {
        var sut = await AbrirFichaAsync();
        int cambiosDeLista = 0;
        sut.EpisodiosDelAnime.CollectionChanged += (_, _) => cambiosDeLista++;
        var episodio = sut.EpisodiosDelAnime.Single(e => e.NumeroEpisodio == 5);

        sut.AplicarEpisodioActualizado(episodio, new EpisodioActualizadoMensaje(7, 5, false, 300, 1400, SoloProgreso: true));

        episodio.ProgresoSegundos.Should().Be(300);
        cambiosDeLista.Should().Be(0, "antes se vaciaba y rellenaba la lista entera cada 3 s (1180 filas en One Piece)");
        sut.TieneCapituloEnProgreso.Should().BeTrue();
    }

    [Fact]
    public async Task ConFiltroNoVistos_MarcarVisto_SiQuitaLaFilaDeLaLista()
    {
        var sut = await AbrirFichaAsync("No Vistos");
        var episodio = sut.EpisodiosDelAnime.Single(e => e.NumeroEpisodio == 5);

        sut.AplicarEpisodioActualizado(episodio, new EpisodioActualizadoMensaje(7, 5, true, 0, 1400));

        sut.EpisodiosDelAnime.Should().NotContain(e => e.NumeroEpisodio == 5);
    }

    [Fact]
    public async Task LaGaleria_IgnoraElProgresoPeriodico_YSoloReleeCuandoCambiaElVisto()
    {
        _db.Setup(d => d.ObtenerTodosLosAnimesAsync()).ReturnsAsync(new List<AnimeItem> { new() { AniListId = 7, Titulo = "Anime de prueba", TotalEpisodios = 12 } });
        _db.Setup(d => d.ObtenerTodosLosRegistrosAsync()).ReturnsAsync(new List<RegistroEpisodio>());
        _db.Setup(d => d.ObtenerRegistrosPorAnimeAsync(7)).ReturnsAsync(new List<RegistroEpisodio>());
        var cache = new Mock<IImageCacheService>();
        cache.Setup(c => c.ObtenerPortadaEnMemoria(It.IsAny<int>())).Returns((System.Windows.Media.ImageSource?)null);
        cache.Setup(c => c.ObtenerPortadaAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<int>())).ReturnsAsync((System.Windows.Media.ImageSource?)null);
        var galeria = new GaleriaViewModel(Mock.Of<IAnimeTrackingService>(), _db.Object, Mock.Of<IAuthService>(), Mock.Of<IDialogService>(),
            Mock.Of<System.Net.Http.IHttpClientFactory>(), cache.Object, Mock.Of<IFileScannerService>(), null);
        for (int i = 0; i < 200 && galeria.BibliotecaLocales.Count == 0; i++) await Task.Delay(10);
        galeria.BibliotecaLocales.Should().NotBeEmpty();

        galeria.Receive(new EpisodioActualizadoMensaje(7, 5, false, 300, 1400, SoloProgreso: true));
        await Task.Delay(100);
        _db.Verify(d => d.ObtenerRegistrosPorAnimeAsync(7), Times.Never, "el progreso de cada 3 s no cambia cuántos episodios están vistos");

        galeria.Receive(new EpisodioActualizadoMensaje(7, 5, true));
        for (int i = 0; i < 200 && _db.Invocations.All(inv => inv.Method.Name != nameof(IDatabaseService.ObtenerRegistrosPorAnimeAsync)); i++) await Task.Delay(10);
        _db.Verify(d => d.ObtenerRegistrosPorAnimeAsync(7), Times.Once);
    }
}
