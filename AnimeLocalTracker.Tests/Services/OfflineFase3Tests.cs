using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>Fase 3 offline-first: cola de descargas que sobrevive a cerrar la app y estado de conexión con sincronización al volver.</summary>
public class OfflineFase3Tests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), "ALT_offline3_" + Guid.NewGuid().ToString("N"));
    private readonly TaskCompletionSource<string?> _resolucion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Mock<IVideoSourceResolver> _resolver = new();

    public OfflineFase3Tests()
    {
        Directory.CreateDirectory(_carpeta);
        // La búsqueda del enlace no termina mientras dura la prueba: la descarga se queda "en curso" sin tocar la red.
        _resolver.Setup(r => r.BuscarUrlEpisodioAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(_resolucion.Task);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _resolucion.TrySetResult(null);
        try { Directory.Delete(_carpeta, true); } catch { /* best-effort */ }
    }

    private static readonly string[] TitulosAlternativos = ["Sousou no Frieren"];

    private string RutaCola => Path.Combine(_carpeta, "cola_descargas.json");
    private string CarpetaAnime => Path.Combine(_carpeta, "Frieren");

    private DownloadService Servicio(string? rutaCola)
    {
        var fabrica = new Mock<IHttpClientFactory>();
        fabrica.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient());
        var ajustes = new Mock<ISettingsService>();
        ajustes.Setup(s => s.ObtenerConfiguracion()).Returns(new AppSettings { DescargasSimultaneas = 2 });
        return new DownloadService(fabrica.Object, sourceResolver: _resolver.Object, settingsService: ajustes.Object, rutaColaPendiente: rutaCola);
    }

    // === Cola de descargas ===

    [Fact]
    public async Task LaCola_SeGuardaAlAnadir_YSeRestauraEnOtraSesion_YSeVaciaAlCancelar()
    {
        var sesion1 = Servicio(RutaCola);
        await sesion1.IniciarDescargaEpisodioAsync(7, "Frieren", CarpetaAnime, 5, TitulosAlternativos);

        File.Exists(RutaCola).Should().BeTrue();
        File.ReadAllText(RutaCola).Should().Contain("Sousou no Frieren");

        // "Se cierra la app" y se vuelve a abrir.
        var sesion2 = Servicio(RutaCola);
        sesion2.RestaurarColaPendiente().Should().Be(1);
        var activa = sesion2.ObtenerDescargasActivas().Single();
        activa.AniListId.Should().Be(7);
        activa.NumeroEpisodio.Should().Be(5);

        sesion2.CancelarDescarga(7, 5);
        sesion1.CancelarDescarga(7, 5);
        File.ReadAllText(RutaCola).Should().Be("[]");
    }

    [Fact]
    public void UnaDescargaPausada_SeRestauraEnPausa_SinEmpezarADescargar()
    {
        File.WriteAllText(RutaCola, $$"""
            [{"AniListId":7,"AnimeTitulo":"Frieren","CarpetaDestino":{{System.Text.Json.JsonSerializer.Serialize(CarpetaAnime)}},"NumeroEpisodio":6,
              "Titulos":["Frieren"],"Automatica":false,"Pausada":true,"Progreso":42.5,"Orden":3,"Torrent":null}]
            """);
        var sut = Servicio(RutaCola);

        sut.RestaurarColaPendiente().Should().Be(1);

        var activa = sut.ObtenerDescargasActivas().Single();
        activa.IsPaused.Should().BeTrue();
        activa.Progreso.Should().Be(42.5);
        _resolver.Verify(r => r.BuscarUrlEpisodioAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void UnEpisodioQueYaTermino_NoSeVuelveADescargar()
    {
        Directory.CreateDirectory(CarpetaAnime);
        File.WriteAllText(Path.Combine(CarpetaAnime, "Episodio 06.mp4"), "video");
        File.WriteAllText(RutaCola, $$"""
            [{"AniListId":7,"AnimeTitulo":"Frieren","CarpetaDestino":{{System.Text.Json.JsonSerializer.Serialize(CarpetaAnime)}},"NumeroEpisodio":6,
              "Titulos":["Frieren"],"Automatica":false,"Pausada":false,"Progreso":99,"Orden":1,"Torrent":null}]
            """);
        var sut = Servicio(RutaCola);

        sut.RestaurarColaPendiente().Should().Be(0);
        sut.ObtenerDescargasActivas().Should().BeEmpty();
    }

    [Fact]
    public async Task SinRutaDeCola_NoSeEscribeNada()
    {
        var sut = Servicio(null);
        await sut.IniciarDescargaEpisodioAsync(7, "Frieren", CarpetaAnime, 5);

        sut.RestaurarColaPendiente().Should().Be(0);
        Directory.GetFiles(_carpeta, "*.json").Should().BeEmpty();
        sut.CancelarDescarga(7, 5);
    }

    [Fact]
    public void UnArchivoDeColaDanado_NoRompeElArranque()
    {
        File.WriteAllText(RutaCola, "{esto no es json");

        Servicio(RutaCola).RestaurarColaPendiente().Should().Be(0);
    }

    // === Estado de conexión ===

    private sealed class RedFalsa : IConectividadRed
    {
        public bool HayInternet { get; set; }
        public Task<bool> EsperarInternetAsync(TimeSpan maximo, CancellationToken ct) => Task.FromResult(HayInternet);
    }

    private static (Mock<IDatabaseService> Db, Mock<IAuthService> Auth, Mock<ISyncService> Sync) Dependencias()
    {
        var db = new Mock<IDatabaseService>();
        db.Setup(d => d.ObtenerEpisodiosNoSincronizadosAsync()).ReturnsAsync(new List<RegistroEpisodio> { new(), new() });
        db.Setup(d => d.ObtenerSeguimientosPendientesAsync()).ReturnsAsync(new List<SeguimientoLocal> { new() });
        var auth = new Mock<IAuthService>();
        auth.Setup(a => a.EstaAutenticado()).Returns(true);
        var sync = new Mock<ISyncService>();
        sync.Setup(s => s.SincronizarPendientesAsync()).ReturnsAsync((3, 3))
            .Callback(() =>
            {
                db.Setup(d => d.ObtenerEpisodiosNoSincronizadosAsync()).ReturnsAsync(new List<RegistroEpisodio>());
                db.Setup(d => d.ObtenerSeguimientosPendientesAsync()).ReturnsAsync(new List<SeguimientoLocal>());
            });
        return (db, auth, sync);
    }

    [Fact]
    public async Task SiLaRedSeCaeConLaAppAbierta_YWindowsSigueDiciendoQueHayInternet_SeDetectaIgual()
    {
        // Caso real (2026-09-29, 20:01): las consultas fallaban con "Host desconocido" y la app nunca mostraba el aviso.
        var red = new RedFalsa { HayInternet = true };
        var guardia = new GuardiaConexion(red);
        bool hayInternet = true;
        int sondas = 0;
        var (db, auth, sync) = Dependencias();
        using var sut = new EstadoConexionService(guardia, db.Object, auth.Object, sync.Object,
            _ => { sondas++; return Task.FromResult(hayInternet); }, TimeSpan.Zero);

        await sut.EvaluarAsync(); // arranque: se comprueba una vez
        sut.SinConexion.Should().BeFalse();
        await sut.EvaluarAsync();
        sondas.Should().Be(1, "con todo en orden no se comprueba nada (hasta el sondeo periódico)");

        hayInternet = false;
        guardia.RegistrarFalloDeRed(); // una petición falló sin respuesta
        await sut.EvaluarAsync();
        sondas.Should().Be(3, "la comprobación fallida se confirma con otra en la misma evaluación");
        sut.SinConexion.Should().BeTrue("en una sola evaluación: el cambio tiene que ser inmediato");
        guardia.DebeBloquear().Should().BeTrue("confirmado: nada sale hasta que la comprobación vuelva a encontrar red");
        guardia.PareceSinConexion.Should().BeTrue();

        hayInternet = true;
        await sut.EvaluarAsync(); // estando sin conexión se comprueba en cada evaluación
        sut.SinConexion.Should().BeFalse("una comprobación que funciona basta para volver");
        guardia.DebeBloquear().Should().BeFalse();
        sync.Verify(s => s.SincronizarPendientesAsync(), Times.Once);
    }

    [Fact]
    public async Task UnaPeticionQueFalla_CambiaElEstadoAlMomento_SinEsperarAlSiguienteCiclo()
    {
        var guardia = new GuardiaConexion(new RedFalsa { HayInternet = true });
        bool hayInternet = true;
        var (db, auth, sync) = Dependencias();
        using var sut = new EstadoConexionService(guardia, db.Object, auth.Object, sync.Object, _ => Task.FromResult(hayInternet), TimeSpan.Zero);
        sut.Iniciar();
        await EsperarHastaAsync(() => sut.CambiosPendientes == 3, TimeSpan.FromSeconds(2)); // primera evaluación hecha

        hayInternet = false;
        var reloj = System.Diagnostics.Stopwatch.StartNew();
        guardia.RegistrarFalloDeRed();
        await EsperarHastaAsync(() => sut.SinConexion, TimeSpan.FromSeconds(2));

        sut.SinConexion.Should().BeTrue();
        reloj.Elapsed.Should().BeLessThan(EstadoConexionService.Intervalo, "no espera al ciclo de evaluación");
        guardia.DebeBloquear().Should().BeTrue("desde ya, nada intenta ir a internet");
    }

    private static async Task EsperarHastaAsync(Func<bool> condicion, TimeSpan maximo)
    {
        var reloj = System.Diagnostics.Stopwatch.StartNew();
        while (!condicion() && reloj.Elapsed < maximo) await Task.Delay(20);
    }

    [Fact]
    public async Task AlVolverLaConexion_SincronizaLoPendiente_YAvisa()
    {
        var red = new RedFalsa { HayInternet = false };
        bool hayInternet = false;
        var (db, auth, sync) = Dependencias();
        using var sut = new EstadoConexionService(new GuardiaConexion(red), db.Object, auth.Object, sync.Object, _ => Task.FromResult(hayInternet), TimeSpan.Zero);
        var estados = new List<EstadoConexionMensaje>();
        int recuperadas = 0;
        var receptor = new object();
        WeakReferenceMessenger.Default.Register<EstadoConexionMensaje>(receptor, (_, m) => estados.Add(m));
        WeakReferenceMessenger.Default.Register<ConexionRecuperadaMensaje>(receptor, (_, _) => recuperadas++);
        try
        {
            await sut.EvaluarAsync();
            sut.SinConexion.Should().BeTrue();
            sut.CambiosPendientes.Should().Be(3);
            sync.Verify(s => s.SincronizarPendientesAsync(), Times.Never);

            hayInternet = true;
            await sut.EvaluarAsync();

            sut.SinConexion.Should().BeFalse();
            sync.Verify(s => s.SincronizarPendientesAsync(), Times.Once);
            sut.CambiosPendientes.Should().Be(0);
            recuperadas.Should().Be(1);
            estados.Select(e => (e.SinConexion, e.CambiosPendientes)).Should().Equal(new[] { (true, 3), (false, 3), (false, 0) },
                "la vuelta se avisa al momento y el recuento, tras sincronizar");
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(receptor);
        }
    }

    [Fact]
    public async Task SiWindowsParpadeaPeroLaComprobacionEncuentraRed_NoSeMarcaSinConexion()
    {
        var (db, auth, sync) = Dependencias();
        using var sut = new EstadoConexionService(new GuardiaConexion(new RedFalsa { HayInternet = false }), db.Object, auth.Object, sync.Object, _ => Task.FromResult(true), TimeSpan.Zero);

        for (int i = 0; i < 4; i++) await sut.EvaluarAsync();

        sut.SinConexion.Should().BeFalse();
    }

    [Fact]
    public async Task SinCuentaDeAniList_NoHayCambiosPendientes()
    {
        var db = new Mock<IDatabaseService>();
        db.Setup(d => d.ObtenerEpisodiosNoSincronizadosAsync()).ReturnsAsync(new List<RegistroEpisodio> { new() });
        using var sut = new EstadoConexionService(new GuardiaConexion(new RedFalsa()), db.Object, Mock.Of<IAuthService>(), Mock.Of<ISyncService>(), _ => Task.FromResult(false), TimeSpan.Zero);

        await sut.EvaluarAsync();

        sut.CambiosPendientes.Should().Be(0);
    }
}
