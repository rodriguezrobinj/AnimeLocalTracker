using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>Fase 2 offline-first: copias locales de la programación, del seguimiento y del perfil.</summary>
public class OfflineFase2Tests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), "ALT_offline2_" + Guid.NewGuid().ToString("N"));
    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<IAnimeTrackingService> _tracking = new();

    public OfflineFase2Tests() => Directory.CreateDirectory(_carpeta);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(_carpeta, true); } catch { /* best-effort */ }
    }

    private static long Unix(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeSeconds();
    private static readonly int[] Ids12 = [1, 2];
    private static readonly int[] Ids123 = [1, 2, 3];
    private static readonly DateTime Lunes = new(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);

    private static AiringEpisode Emision(int anime, int ep, DateTime utc, string titulo = "") =>
        new() { AniListId = anime, NumeroEpisodio = ep, FechaEmision = DateTimeOffset.FromUnixTimeSeconds(Unix(utc)).DateTime, Titulo = titulo };

    // === Programación de emisión ===

    [Fact]
    public async Task ConConexion_DevuelveLaDeAniList_YLaGuarda()
    {
        var lista = new List<AiringEpisode> { Emision(1, 5, Lunes.AddDays(2), "Frieren") };
        _tracking.Setup(t => t.ObtenerCalendarioEmisionAsync(It.IsAny<List<int>>(), 100, 200)).ReturnsAsync((true, lista));

        var r = await new ProgramacionEmisionService(_tracking.Object, _db.Object).ObtenerAsync(Ids12, 100, 200);

        r.DesdeCopiaLocal.Should().BeFalse();
        r.Episodios.Should().BeSameAs(lista);
        _db.Verify(d => d.GuardarEmisionesAsync(It.Is<IReadOnlyCollection<int>>(ids => ids.Count == 2), 100, 200,
            It.Is<IReadOnlyList<EmisionGuardada>>(g => g.Single().Episodio == 5 && g.Single().EmisionUnixUtc == Unix(Lunes.AddDays(2)) && g.Single().Titulo == "Frieren")), Times.Once);
    }

    [Fact]
    public async Task SinConexion_UsaLaGuardada_YCompletaConLaCuentaAtrasDeLaFicha()
    {
        long inicio = Unix(Lunes), fin = Unix(Lunes.AddDays(7));
        _tracking.Setup(t => t.ObtenerCalendarioEmisionAsync(It.IsAny<List<int>>(), inicio, fin)).ReturnsAsync((false, new List<AiringEpisode>()));
        _db.Setup(d => d.ObtenerEmisionesAsync(inicio, fin)).ReturnsAsync(new List<EmisionGuardada>
        {
            new() { AniListId = 1, Episodio = 5, EmisionUnixUtc = Unix(Lunes.AddDays(2)), Titulo = "Frieren" },
            new() { AniListId = 9, Episodio = 1, EmisionUnixUtc = Unix(Lunes.AddDays(3)) } // ya no está en la biblioteca
        });
        _db.Setup(d => d.ObtenerProximasEmisionesAsync()).ReturnsAsync(new List<ProximaEmisionLocal>
        {
            new() { AniListId = 1, Episodio = 5, EmisionUnixUtc = Unix(Lunes.AddDays(2)) },  // repetido
            new() { AniListId = 2, Episodio = 8, EmisionUnixUtc = Unix(Lunes.AddDays(4)) },  // solo en la cuenta atrás
            new() { AniListId = 2, Episodio = 0, EmisionUnixUtc = 0 },                        // sin episodio programado
            new() { AniListId = 3, Episodio = 2, EmisionUnixUtc = Unix(Lunes.AddDays(9)) }   // fuera de la semana
        });

        var r = await new ProgramacionEmisionService(_tracking.Object, _db.Object).ObtenerAsync(Ids123, inicio, fin);

        r.DesdeCopiaLocal.Should().BeTrue();
        r.Episodios.Select(e => (e.AniListId, e.NumeroEpisodio)).Should().BeEquivalentTo(new[] { (1, 5), (2, 8) });
        r.Episodios.Single(e => e.AniListId == 1).FechaEmision.Should().Be(new DateTime(2026, 9, 30), "mismo formato que da AniList: valor UTC");
    }

    [Fact]
    public async Task BaseDeDatos_LaVentanaConsultadaSeReemplaza_SinDuplicados_YLoViejoSeDescarta()
    {
        string ruta = Path.Combine(_carpeta, "bd.db");
        using var db = new DatabaseService(ruta);
        await db.InicializarBaseDatosAsync();
        long ahora = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long dia = 86400;

        await db.GuardarEmisionesAsync(Ids12, ahora - 7 * dia, ahora, new List<EmisionGuardada>
        {
            new() { AniListId = 1, Episodio = 4, EmisionUnixUtc = ahora - 3 * dia },
            new() { AniListId = 2, Episodio = 10, EmisionUnixUtc = ahora - 2 * dia },
            new() { AniListId = 2, Episodio = 1, EmisionUnixUtc = ahora - 90 * dia } // más de 60 días: se descarta
        });
        // Nueva consulta de la misma ventana: AniList movió el ep 10 y ya no trae el 4.
        await db.GuardarEmisionesAsync(Ids12, ahora - 7 * dia, ahora, new List<EmisionGuardada>
        {
            new() { AniListId = 2, Episodio = 10, EmisionUnixUtc = ahora - 1 * dia }
        });

        var guardadas = await db.ObtenerEmisionesAsync(ahora - 100 * dia, ahora);
        guardadas.Select(g => (g.AniListId, g.Episodio, g.EmisionUnixUtc)).Should().BeEquivalentTo(new[] { (2, 10, ahora - dia) });
    }

    [Fact]
    public async Task BaseDeDatos_SeguimientoLocal_YSusPendientes()
    {
        using var db = new DatabaseService(Path.Combine(_carpeta, "bd2.db"));
        await db.InicializarBaseDatosAsync();

        await db.GuardarSeguimientoLocalAsync(new SeguimientoLocal { AniListId = 1, Estado = "PAUSED", Progreso = 3, Puntaje = 7, Pendiente = true, FechaInicio = new DateTime(2026, 9, 1) });
        await db.GuardarSeguimientoLocalAsync(new SeguimientoLocal { AniListId = 2, Estado = "CURRENT", Pendiente = false });

        (await db.ObtenerSeguimientosPendientesAsync()).Select(s => s.AniListId).Should().Equal(1);
        var uno = await db.ObtenerSeguimientoLocalAsync(1);
        uno!.Estado.Should().Be("PAUSED");
        uno.FechaInicio.Should().Be(new DateTime(2026, 9, 1));
    }

    // === Editor de seguimiento de la Ficha ===

    private DetalleViewModel Ficha(string? token, AnimeItem anime, Mock<IDialogService> dialogos)
    {
        var auth = new Mock<IAuthService>();
        auth.Setup(a => a.ObtenerTokenGuardado()).Returns(token!);
        var sut = new DetalleViewModel(_tracking.Object, _db.Object, auth.Object, Mock.Of<IFileScannerService>(), dialogos.Object, Mock.Of<IDownloadService>());
        sut.AnimeSeleccionado = anime;
        return sut;
    }

    private static AnimeItem Anime() => new() { AniListId = 7, Titulo = "Frieren", TotalEpisodios = 28, EstadoUsuario = "PAUSED", EpisodiosVistos = 12 };

    [Fact]
    public async Task Editor_SinConexion_AbreConLoGuardado_NoConValoresInventados()
    {
        _db.Setup(d => d.ObtenerSeguimientoLocalAsync(7)).ReturnsAsync(new SeguimientoLocal { AniListId = 7, Estado = "COMPLETED", Progreso = 28, Puntaje = 9, FechaFin = new DateTime(2026, 9, 20) });
        _tracking.Setup(t => t.ObtenerSeguimientoUsuarioAsync(7, "tok")).ReturnsAsync((AniListMediaList?)null);
        var sut = Ficha("tok", Anime(), new Mock<IDialogService>());

        await sut.AbrirEditorSeguimientoCommand.ExecuteAsync(null);

        sut.MostrandoEditorSeguimiento.Should().BeTrue();
        sut.EditEstadoVisual.Should().Be(LocalizationService.T("Estado_Finalizado"));
        sut.EditProgreso.Should().Be(28);
        sut.EditPuntaje.Should().Be(9);
        sut.EditFechaFin.Should().Be(new DateTime(2026, 9, 20));
    }

    [Fact]
    public async Task Editor_SinNadaGuardado_UsaElEstadoDeLaBiblioteca()
    {
        var sut = Ficha(null, Anime(), new Mock<IDialogService>());

        await sut.AbrirEditorSeguimientoCommand.ExecuteAsync(null);

        sut.EditEstadoVisual.Should().Be(LocalizationService.T("Estado_EnPausa"));
        sut.EditProgreso.Should().Be(12);
    }

    [Fact]
    public async Task Editor_ConUnCambioSinEnviar_NoLoPisaLoDeAniList()
    {
        _db.Setup(d => d.ObtenerSeguimientoLocalAsync(7)).ReturnsAsync(new SeguimientoLocal { AniListId = 7, Estado = "DROPPED", Progreso = 5, Pendiente = true });
        _tracking.Setup(t => t.ObtenerSeguimientoUsuarioAsync(7, "tok")).ReturnsAsync(new AniListMediaList { Status = "CURRENT", Progress = 3 });
        var sut = Ficha("tok", Anime(), new Mock<IDialogService>());

        await sut.AbrirEditorSeguimientoCommand.ExecuteAsync(null);

        sut.EditEstadoVisual.Should().Be(LocalizationService.T("Estado_Abandonado"));
        sut.EditProgreso.Should().Be(5);
    }

    [Fact]
    public async Task Editor_ConConexion_TraeLoDeAniList_YLoGuardaEnLocal()
    {
        _tracking.Setup(t => t.ObtenerSeguimientoUsuarioAsync(7, "tok")).ReturnsAsync(new AniListMediaList
        {
            Status = "COMPLETED", Progress = 28, Score = 8, StartedAt = new AniListFuzzyDate { Year = 2026, Month = 8, Day = 2 }
        });
        var sut = Ficha("tok", Anime(), new Mock<IDialogService>());

        await sut.AbrirEditorSeguimientoCommand.ExecuteAsync(null);

        sut.EditProgreso.Should().Be(28);
        sut.EditFechaInicio.Should().Be(new DateTime(2026, 8, 2));
        _db.Verify(d => d.GuardarSeguimientoLocalAsync(It.Is<SeguimientoLocal>(s => s.Estado == "COMPLETED" && s.Puntaje == 8 && !s.Pendiente)), Times.Once);
    }

    [Fact]
    public async Task Guardar_SinConexion_QuedaEnLaBibliotecaYPendiente()
    {
        _tracking.Setup(t => t.GuardarSeguimientoUsuarioAsync(7, It.IsAny<string>(), It.IsAny<int>(), It.IsAny<float>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), "tok"))
            .ReturnsAsync(false);
        var guardados = new List<SeguimientoLocal>();
        _db.Setup(d => d.GuardarSeguimientoLocalAsync(It.IsAny<SeguimientoLocal>())).Callback<SeguimientoLocal>(s => guardados.Add(s)).Returns(Task.CompletedTask);
        var dialogos = new Mock<IDialogService>();
        var anime = Anime();
        var sut = Ficha("tok", anime, dialogos);
        await sut.AbrirEditorSeguimientoCommand.ExecuteAsync(null);
        sut.EditEstadoVisual = LocalizationService.T("Estado_Finalizado");
        sut.EditProgreso = 28;

        await sut.GuardarEditorSeguimientoCommand.ExecuteAsync(null);

        anime.EstadoUsuario.Should().Be("COMPLETED");
        anime.EpisodiosVistos.Should().Be(28);
        _db.Verify(d => d.ActualizarAnimeAsync(anime), Times.Once);
        guardados.Last().Should().Match<SeguimientoLocal>(s => s.Estado == "COMPLETED" && s.Progreso == 28 && s.Pendiente);
        dialogos.Verify(d => d.MostrarToast(LocalizationService.T("Det_SeguimientoPendienteTitulo"), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        sut.MostrandoEditorSeguimiento.Should().BeFalse();
    }

    [Fact]
    public async Task Guardar_ConConexion_NoQuedaPendiente()
    {
        _tracking.Setup(t => t.GuardarSeguimientoUsuarioAsync(7, It.IsAny<string>(), It.IsAny<int>(), It.IsAny<float>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), "tok"))
            .ReturnsAsync(true);
        var guardados = new List<SeguimientoLocal>();
        _db.Setup(d => d.GuardarSeguimientoLocalAsync(It.IsAny<SeguimientoLocal>())).Callback<SeguimientoLocal>(s => guardados.Add(new SeguimientoLocal { Pendiente = s.Pendiente })).Returns(Task.CompletedTask);
        var sut = Ficha("tok", Anime(), new Mock<IDialogService>());

        await sut.GuardarEditorSeguimientoCommand.ExecuteAsync(null);

        guardados.Select(g => g.Pendiente).Should().Equal(true, false);
    }

    [Fact]
    public async Task Guardar_SinCuentaDeAniList_SeGuardaEnLocal_SinPendienteNiError()
    {
        var dialogos = new Mock<IDialogService>();
        var sut = Ficha(null, Anime(), dialogos);

        await sut.GuardarEditorSeguimientoCommand.ExecuteAsync(null);

        _db.Verify(d => d.GuardarSeguimientoLocalAsync(It.Is<SeguimientoLocal>(s => !s.Pendiente)), Times.Once);
        _tracking.Verify(t => t.GuardarSeguimientoUsuarioAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<float>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string>()), Times.Never);
        dialogos.Verify(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    // === Sincronización de lo guardado sin conexión ===

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Sync_EnviaLosSeguimientosPendientes_YSoloLosCierraSiNadieLosVolvioACambiar(bool cambiadoMientras, bool sigue)
    {
        var modificado = new DateTime(2026, 9, 29, 20, 0, 0);
        var pendiente = new SeguimientoLocal { AniListId = 7, Estado = "COMPLETED", Progreso = 28, Puntaje = 9, Pendiente = true, ModificadoUtc = modificado };
        _db.Setup(d => d.ObtenerSeguimientosPendientesAsync()).ReturnsAsync(new List<SeguimientoLocal> { pendiente });
        _db.Setup(d => d.ObtenerSeguimientoLocalAsync(7)).ReturnsAsync(new SeguimientoLocal
        {
            AniListId = 7, Estado = "COMPLETED", Pendiente = true, ModificadoUtc = cambiadoMientras ? modificado.AddMinutes(1) : modificado
        });
        _db.Setup(d => d.ObtenerEpisodiosNoSincronizadosAsync()).ReturnsAsync(new List<RegistroEpisodio>());
        _tracking.Setup(t => t.GuardarSeguimientoUsuarioAsync(7, "COMPLETED", 28, 9, null, null, "tok")).ReturnsAsync(true);
        var auth = new Mock<IAuthService>();
        auth.Setup(a => a.EstaAutenticado()).Returns(true);
        auth.Setup(a => a.ObtenerToken()).Returns("tok");
        using var sync = new SyncService(_db.Object, _tracking.Object, auth.Object);

        var (exitosos, pendientes) = await sync.SincronizarPendientesAsync();

        (exitosos, pendientes).Should().Be((1, 1));
        _db.Verify(d => d.GuardarSeguimientoLocalAsync(It.Is<SeguimientoLocal>(s => !s.Pendiente)), sigue ? Times.Never() : Times.Once());
    }

    // === Perfil de AniList ===

    private sealed class Transporte(HttpStatusCode codigo, byte[] cuerpo) : HttpMessageHandler
    {
        public int Pedidas;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Pedidas++;
            return Task.FromResult(new HttpResponseMessage(codigo) { Content = new ByteArrayContent(cuerpo) });
        }
    }

    private sealed class Fabrica(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 };

    [Fact]
    public async Task Perfil_ConConexionSeGuarda_YSinConexionSaleElGuardado()
    {
        var transporte = new Transporte(HttpStatusCode.OK, Png);
        _tracking.SetupSequence(t => t.ObtenerPerfilUsuarioAsync("tok"))
            .ReturnsAsync(new AniListUser { Name = "Robin", Avatar = new AniListAvatar { Large = "https://s4.anilist.co/file/avatar.png" } })
            .ReturnsAsync((AniListUser?)null);
        var sut = new PerfilAniListService(_tracking.Object, new Fabrica(transporte), _carpeta);

        var enLinea = await sut.ObtenerAsync("tok");
        var sinRed = await sut.ObtenerAsync("tok");

        enLinea!.Nombre.Should().Be("Robin");
        sinRed!.Nombre.Should().Be("Robin");
        sinRed.Avatar.Should().StartWith(_carpeta, "el avatar sale del archivo guardado, no de la URL");
        File.ReadAllBytes(sinRed.Avatar!).Should().Equal(Png);
        transporte.Pedidas.Should().Be(1);
    }

    [Fact]
    public async Task Perfil_AvatarDeOtroSitio_NoSeDescarga()
    {
        var transporte = new Transporte(HttpStatusCode.OK, Png);
        _tracking.Setup(t => t.ObtenerPerfilUsuarioAsync("tok"))
            .ReturnsAsync(new AniListUser { Name = "Robin", Avatar = new AniListAvatar { Large = "https://evil.example/a.png" } });

        var perfil = await new PerfilAniListService(_tracking.Object, new Fabrica(transporte), _carpeta).ObtenerAsync("tok");

        transporte.Pedidas.Should().Be(0);
        perfil!.Avatar.Should().Be("https://evil.example/a.png");
    }

    [Fact]
    public async Task Perfil_SinNadaGuardadoYSinConexion_EsNulo()
    {
        _tracking.Setup(t => t.ObtenerPerfilUsuarioAsync("tok")).ReturnsAsync((AniListUser?)null);

        (await new PerfilAniListService(_tracking.Object, new Fabrica(new Transporte(HttpStatusCode.OK, Png)), _carpeta).ObtenerAsync("tok")).Should().BeNull();
    }
}
