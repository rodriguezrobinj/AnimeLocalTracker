using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>Avisos y descarga automática de episodios nuevos.</summary>
public class EmisionMonitorServiceTests
{
    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<IProximaEmisionService> _proxima = new();
    private readonly Mock<IDownloadService> _descargas = new();
    private readonly Mock<IFileScannerService> _escaner = new();
    private readonly Mock<IDialogService> _dialogos = new();
    private readonly Mock<ISystemTrayService> _bandeja = new();
    private readonly AnimeItem _anime = new() { AniListId = 7, Titulo = "Frieren", Estado = "RELEASING", RutaCarpeta = @"C:\Anime\Frieren", TotalEpisodios = 11 };

    public EmisionMonitorServiceTests()
    {
        _db.Setup(d => d.ObtenerAnimePorIdAsync(7)).ReturnsAsync(_anime);
        _escaner.Setup(e => e.EscanearEpisodiosAsync(It.IsAny<string>())).ReturnsAsync(new List<EpisodioItem>());
        double p = 0;
        _descargas.Setup(d => d.EstaDescargando(It.IsAny<int>(), It.IsAny<int>(), out p)).Returns(false);
        _bandeja.Setup(b => b.VentanaEnSegundoPlano).Returns(false); // ventana visible/activa por defecto: usa el toast interno
    }

    private EmisionMonitorService CrearSut() => new(_db.Object, _proxima.Object, _descargas.Object, _escaner.Object, _dialogos.Object, _bandeja.Object);

    private void Preferencias(params PreferenciaEmision[] prefs) =>
        _db.Setup(d => d.ObtenerPreferenciasEmisionActivasAsync()).ReturnsAsync(new List<PreferenciaEmision>(prefs));

    private void ProximaEmision(int episodio, TimeSpan desdeAhora) =>
        _proxima.Setup(p => p.ObtenerAsync(7, "RELEASING", false)).ReturnsAsync(new ProximaEmision(episodio, DateTime.UtcNow + desdeAhora));

    // ── Último episodio emitido ──

    [Fact]
    public void UltimoEmitido_EmisionFutura_EsElEpisodioAnterior()
    {
        var sut = CrearSut();
        sut.UltimoEmitido(new ProximaEmision(12, DateTime.UtcNow.AddDays(2)), _anime, DateTime.UtcNow).Should().Be(11);
    }

    [Fact]
    public void UltimoEmitido_EmisionYaPasada_EsEseEpisodio()
    {
        var sut = CrearSut();
        sut.UltimoEmitido(new ProximaEmision(12, DateTime.UtcNow.AddMinutes(-5)), _anime, DateTime.UtcNow).Should().Be(12);
    }

    [Fact]
    public void UltimoEmitido_SinProximaEmision_UsaElTotalSoloSiElAnimeTermino()
    {
        var sut = CrearSut();
        var terminado = new AnimeItem { Estado = "FINISHED", TotalEpisodios = 24 };

        sut.UltimoEmitido(null, terminado, DateTime.UtcNow).Should().Be(24);
        sut.UltimoEmitido(null, _anime, DateTime.UtcNow).Should().Be(0, "en emisión sin programación no se puede saber");
    }

    // ── Avisos ──

    [Fact]
    public async Task Aviso_CuandoSaleUnEpisodioNuevo_AvisaUnaSolaVez()
    {
        var pref = new PreferenciaEmision { AniListId = 7, Avisar = true, UltimoAvisado = 11 };
        Preferencias(pref);
        ProximaEmision(12, TimeSpan.FromMinutes(-3));
        var sut = CrearSut();

        await sut.EjecutarCicloAsync();
        await sut.EjecutarCicloAsync();

        _dialogos.Verify(d => d.MostrarToast(It.IsAny<string>(), It.Is<string>(m => m.Contains("Frieren") && m.Contains("12", StringComparison.Ordinal)), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        pref.UltimoAvisado.Should().Be(12);
        _db.Verify(d => d.GuardarPreferenciaEmisionAsync(It.Is<PreferenciaEmision>(p => p.UltimoAvisado == 12)), Times.Once);
    }

    [Fact]
    public async Task Aviso_SiElEpisodioAunNoSalio_NoAvisa()
    {
        Preferencias(new PreferenciaEmision { AniListId = 7, Avisar = true, UltimoAvisado = 11 });
        ProximaEmision(12, TimeSpan.FromHours(5));

        await CrearSut().EjecutarCicloAsync();

        _dialogos.Verify(d => d.MostrarToast(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Aviso_VariosEpisodiosMientrasLaAppEstabaCerrada_UnSoloAvisoResumen()
    {
        Preferencias(new PreferenciaEmision { AniListId = 7, Avisar = true, UltimoAvisado = 9 });
        ProximaEmision(12, TimeSpan.FromMinutes(-5)); // salieron el 10, el 11 y el 12

        await CrearSut().EjecutarCicloAsync();

        _dialogos.Verify(d => d.MostrarToast(It.IsAny<string>(), It.Is<string>(m => m.Contains('3') && m.Contains("10", StringComparison.Ordinal) && m.Contains("12", StringComparison.Ordinal)), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task SinNadaActivado_NoHaceNada()
    {
        Preferencias();

        await CrearSut().EjecutarCicloAsync();

        _proxima.Verify(p => p.ObtenerAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    // ── Descarga automática ──

    [Fact]
    public async Task Descarga_EpisodioNuevoDisponible_LoDescargaEnLaCarpetaDelAnime()
    {
        Preferencias(new PreferenciaEmision { AniListId = 7, AutoDescargar = true, UltimoDescargado = 11 });
        ProximaEmision(12, TimeSpan.FromMinutes(-30)); // pasado el margen de espera

        await CrearSut().EjecutarCicloAsync();

        _descargas.Verify(d => d.IniciarDescargaAutomaticaAsync(7, "Frieren", @"C:\Anime\Frieren", 12, It.IsAny<IEnumerable<string>?>()), Times.Once);
        _descargas.Verify(d => d.IniciarDescargaEpisodioAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IEnumerable<string>?>()), Times.Never);
    }

    [Fact]
    public async Task Descarga_RecienEmitido_EsperaElMargenDelServidor()
    {
        Preferencias(new PreferenciaEmision { AniListId = 7, AutoDescargar = true, UltimoDescargado = 11 });
        ProximaEmision(12, TimeSpan.FromMinutes(-2)); // salió hace 2 min: el servidor aún no lo tendrá

        await CrearSut().EjecutarCicloAsync();

        _descargas.Verify(d => d.IniciarDescargaAutomaticaAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IEnumerable<string>?>()), Times.Never);
    }

    [Fact]
    public async Task Descarga_NoLanzaLoMismoDosVecesSeguidas()
    {
        Preferencias(new PreferenciaEmision { AniListId = 7, AutoDescargar = true, UltimoDescargado = 11 });
        ProximaEmision(12, TimeSpan.FromMinutes(-30));
        var sut = CrearSut();

        await sut.EjecutarCicloAsync();
        await sut.EjecutarCicloAsync(); // el siguiente intento espera 10 min

        _descargas.Verify(d => d.IniciarDescargaAutomaticaAsync(7, It.IsAny<string>(), It.IsAny<string>(), 12, It.IsAny<IEnumerable<string>?>()), Times.Once);
    }

    [Fact]
    public async Task Descarga_SiElEpisodioYaEstaEnLaCarpeta_NoDescargaYLoDaPorResuelto()
    {
        var pref = new PreferenciaEmision { AniListId = 7, AutoDescargar = true, UltimoDescargado = 11 };
        Preferencias(pref);
        ProximaEmision(12, TimeSpan.FromMinutes(-30));
        _escaner.Setup(e => e.EscanearEpisodiosAsync(_anime.RutaCarpeta)).ReturnsAsync(new List<EpisodioItem> { new() { NumeroEpisodio = 12, Descargado = true } });

        await CrearSut().EjecutarCicloAsync();

        _descargas.Verify(d => d.IniciarDescargaAutomaticaAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IEnumerable<string>?>()), Times.Never);
        pref.UltimoDescargado.Should().Be(12);
    }

    [Fact]
    public async Task Descarga_SinCarpetaAsignada_NoIntentaNada()
    {
        _anime.RutaCarpeta = string.Empty;
        Preferencias(new PreferenciaEmision { AniListId = 7, AutoDescargar = true, UltimoDescargado = 11 });
        ProximaEmision(12, TimeSpan.FromMinutes(-30));

        await CrearSut().EjecutarCicloAsync();

        _descargas.Verify(d => d.IniciarDescargaAutomaticaAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IEnumerable<string>?>()), Times.Never);
    }

    [Fact]
    public async Task Descarga_AtrasadosSeBajanEnOrdenYDeUnoEnUno()
    {
        // La app estuvo cerrada: salieron el 4, 5, 6… hasta el 12. Se empieza por el 4 y no se lanza el 5 hasta resolver el 4.
        Preferencias(new PreferenciaEmision { AniListId = 7, AutoDescargar = true, UltimoDescargado = 3 });
        ProximaEmision(12, TimeSpan.FromMinutes(-30));

        await CrearSut().EjecutarCicloAsync();

        _descargas.Verify(d => d.IniciarDescargaAutomaticaAsync(7, It.IsAny<string>(), It.IsAny<string>(), 4, It.IsAny<IEnumerable<string>?>()), Times.Once);
        _descargas.Verify(d => d.IniciarDescargaAutomaticaAsync(7, It.IsAny<string>(), It.IsAny<string>(), It.Is<int>(e => e != 4), It.IsAny<IEnumerable<string>?>()), Times.Never);
    }

    [Fact]
    public async Task Descarga_SiElEpisodioSeEstaDescargando_EsperaSinLanzarOtro()
    {
        Preferencias(new PreferenciaEmision { AniListId = 7, AutoDescargar = true, UltimoDescargado = 11 });
        ProximaEmision(12, TimeSpan.FromMinutes(-30));
        double p = 40;
        _descargas.Setup(d => d.EstaDescargando(7, 12, out p)).Returns(true);

        await CrearSut().EjecutarCicloAsync();

        _descargas.Verify(d => d.IniciarDescargaAutomaticaAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IEnumerable<string>?>()), Times.Never);
    }

    [Fact]
    public async Task AvisoYDescarga_SonIndependientes()
    {
        var pref = new PreferenciaEmision { AniListId = 7, Avisar = false, AutoDescargar = true, UltimoAvisado = 0, UltimoDescargado = 11 };
        Preferencias(pref);
        ProximaEmision(12, TimeSpan.FromMinutes(-30));

        await CrearSut().EjecutarCicloAsync();

        _dialogos.Verify(d => d.MostrarToast(It.IsAny<string>(), It.IsAny<string>(), "BellRing", It.IsAny<string>()), Times.Never);
        pref.UltimoAvisado.Should().Be(0);
    }

    // ── Aviso al terminar una descarga automática ──

    [Fact]
    public async Task AlTerminarUnaDescargaAutomatica_AvisaPeroNoConLasManuales()
    {
        Preferencias(new PreferenciaEmision { AniListId = 7, AutoDescargar = true, UltimoDescargado = 11 });
        ProximaEmision(12, TimeSpan.FromMinutes(-30));
        var sut = CrearSut();
        await sut.EjecutarCicloAsync(); // marca el 7_12 como automática

        sut.Receive(new DescargaProgresoMensaje(7, 99, 100, false, true, false, @"C:\x.mp4", null, "Otro")); // manual: no
        sut.Receive(new DescargaProgresoMensaje(7, 12, 100, false, true, false, @"C:\x.mp4", null, "Frieren"));

        _dialogos.Verify(d => d.MostrarToast(It.IsAny<string>(), It.Is<string>(m => m.Contains("Frieren") && m.Contains("12")), "CloudDownloadOutline", It.IsAny<string>()), Times.Once);
        _dialogos.Verify(d => d.MostrarToast(It.IsAny<string>(), It.Is<string>(m => m.Contains("Otro")), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    // ── Ventana en segundo plano: notificación nativa en vez de toast interno ──

    [Fact]
    public async Task Aviso_ConLaVentanaEnSegundoPlano_UsaNotificacionNativaEnVezDeToast()
    {
        _bandeja.Setup(b => b.VentanaEnSegundoPlano).Returns(true);
        Preferencias(new PreferenciaEmision { AniListId = 7, Avisar = true, UltimoAvisado = 11 });
        ProximaEmision(12, TimeSpan.FromMinutes(-3));

        await CrearSut().EjecutarCicloAsync();

        _bandeja.Verify(b => b.MostrarNotificacion(It.IsAny<string>(), It.Is<string>(m => m.Contains("Frieren") && m.Contains("12", StringComparison.Ordinal))), Times.Once);
        _dialogos.Verify(d => d.MostrarToast(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task AlTerminarUnaDescargaAutomatica_ConLaVentanaEnSegundoPlano_UsaNotificacionNativa()
    {
        _bandeja.Setup(b => b.VentanaEnSegundoPlano).Returns(true);
        Preferencias(new PreferenciaEmision { AniListId = 7, AutoDescargar = true, UltimoDescargado = 11 });
        ProximaEmision(12, TimeSpan.FromMinutes(-30));
        var sut = CrearSut();
        await sut.EjecutarCicloAsync();

        sut.Receive(new DescargaProgresoMensaje(7, 12, 100, false, true, false, @"C:\x.mp4", null, "Frieren"));

        _bandeja.Verify(b => b.MostrarNotificacion(It.IsAny<string>(), It.Is<string>(m => m.Contains("Frieren") && m.Contains("12"))), Times.Once);
        _dialogos.Verify(d => d.MostrarToast(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }
}
