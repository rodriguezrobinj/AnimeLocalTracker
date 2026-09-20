using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>Fechas automáticas de inicio y fin de AniList según el visionado real (reglas + cableado en reproducción y sync).</summary>
public class SeguimientoFechasTests
{
    private static readonly DateTime Dia1 = new(2026, 9, 1, 20, 0, 0, DateTimeKind.Local);
    private static readonly DateTime Dia12 = new(2026, 9, 12, 21, 30, 0, DateTimeKind.Local);

    private static AnimeItem Anime(string estado, int total) => new() { AniListId = 7, Titulo = "Frieren", Estado = estado, TotalEpisodios = total };

    private static RegistroEpisodio Visto(int ep, DateTime? cuando) => new() { AniListId = 7, NumeroEpisodio = ep, VistoLocal = true, UltimaReproduccion = cuando };

    private static AniListMediaList Remoto(string status = "CURRENT", int progreso = 0, AniListFuzzyDate? inicio = null, AniListFuzzyDate? fin = null)
        => new() { Status = status, Progress = progreso, StartedAt = inicio, CompletedAt = fin };

    // ── Fecha de inicio ──

    [Fact]
    public void Inicio_PrimerVisionadoReal_UsaElDiaLocalDeLaReproduccion()
    {
        var (inicio, fin) = SeguimientoFechas.Calcular(Anime("RELEASING", 28), new[] { Visto(1, Dia1) }, remotoPrevio: null, episodioMaximo: 1);

        inicio.Should().Be(Dia1.Date);
        fin.Should().BeNull("un anime en emisión nunca tiene fecha de fin");
    }

    [Fact]
    public void Inicio_ConVariosEpisodios_UsaLaReproduccionMasAntigua()
    {
        var (inicio, _) = SeguimientoFechas.Calcular(Anime("RELEASING", 28), new[] { Visto(2, Dia12), Visto(1, Dia1) }, Remoto(), 2);

        inicio.Should().Be(Dia1.Date);
    }

    [Fact]
    public void Inicio_NoSeGuardaSiAniListYaTieneFechaDeInicio()
    {
        var remoto = Remoto(inicio: new AniListFuzzyDate { Year = 2020, Month = 1, Day = 2 });

        var (inicio, _) = SeguimientoFechas.Calcular(Anime("RELEASING", 28), new[] { Visto(1, Dia1) }, remoto, 1);

        inicio.Should().BeNull("nunca se pisa una fecha que el usuario ya tenga");
    }

    [Fact]
    public void Inicio_NoSeGuardaSiAniListYaTeniaProgreso()
    {
        // Venía viendo la serie (o la importó): no es su primer visionado.
        var (inicio, _) = SeguimientoFechas.Calcular(Anime("RELEASING", 28), new[] { Visto(6, Dia12) }, Remoto(progreso: 5), 6);

        inicio.Should().BeNull();
    }

    [Fact]
    public void SinReproduccionReal_ElMarcadoManualNoInventaFechas()
    {
        // UltimaReproduccion = null: marcado manual como visto (no es visionado real).
        var (inicio, fin) = SeguimientoFechas.Calcular(Anime("FINISHED", 12), new[] { Visto(1, null), Visto(12, null) }, null, 12);

        inicio.Should().BeNull();
        fin.Should().BeNull();
    }

    // ── Fecha de fin ──

    [Fact]
    public void Fin_UltimoEpisodioOficialDeUnAnimeFinalizado_GuardaElDiaDeEseEpisodio()
    {
        var registros = new[] { Visto(1, Dia1), Visto(11, Dia1.AddDays(9)), Visto(12, Dia12) };

        var (inicio, fin) = SeguimientoFechas.Calcular(Anime("FINISHED", 12), registros, Remoto(progreso: 11, inicio: new AniListFuzzyDate { Year = 2026, Month = 9, Day = 1 }), 12);

        fin.Should().Be(Dia12.Date);
        inicio.Should().BeNull("ya tenía fecha de inicio");
    }

    [Fact]
    public void Fin_AnimeEnEmision_NoSeGuardaAunqueSeaElUltimoDeLaLista()
    {
        // 8 episodios emitidos de 12: el 8 es "el último de la lista", pero no el final oficial de la serie.
        var (_, fin) = SeguimientoFechas.Calcular(Anime("RELEASING", 8), new[] { Visto(8, Dia12) }, Remoto(progreso: 7), 8);

        fin.Should().BeNull();
    }

    [Fact]
    public void Fin_AnimeFinalizadoPeroSinAcabarlo_NoSeGuarda()
    {
        var (_, fin) = SeguimientoFechas.Calcular(Anime("FINISHED", 12), new[] { Visto(11, Dia12) }, Remoto(progreso: 10), 11);

        fin.Should().BeNull();
    }

    [Fact]
    public void Fin_ConTotalDesconocido_NoSeGuarda()
    {
        var (_, fin) = SeguimientoFechas.Calcular(Anime("FINISHED", 0), new[] { Visto(12, Dia12) }, Remoto(progreso: 11), 12);

        fin.Should().BeNull();
    }

    [Fact]
    public void Fin_NoSeGuardaSiAniListYaTieneFechaDeFin()
    {
        var remoto = Remoto(progreso: 11, fin: new AniListFuzzyDate { Year = 2025, Month = 3, Day = 3 });

        var (_, fin) = SeguimientoFechas.Calcular(Anime("FINISHED", 12), new[] { Visto(12, Dia12) }, remoto, 12);

        fin.Should().BeNull();
    }

    [Theory]
    [InlineData("COMPLETED")]
    [InlineData("REPEATING")]
    public void UnRevisionadoNoTocaLasFechas(string estadoRemoto)
    {
        var (inicio, fin) = SeguimientoFechas.Calcular(Anime("FINISHED", 12), new[] { Visto(1, Dia1), Visto(12, Dia12) }, Remoto(estadoRemoto, progreso: 12), 12);

        inicio.Should().BeNull();
        fin.Should().BeNull();
    }

    [Fact]
    public void Maraton_ElMismoDia_DaInicioYFinEnLaMismaFecha()
    {
        var (inicio, fin) = SeguimientoFechas.Calcular(Anime("FINISHED", 3), new[] { Visto(1, Dia1), Visto(2, Dia1.AddHours(1)), Visto(3, Dia1.AddHours(2)) }, null, 3);

        inicio.Should().Be(Dia1.Date);
        fin.Should().Be(Dia1.Date);
    }

    [Fact]
    public void Fechas_UtcSinKindSeConvierteAlDiaLocal()
    {
        // sqlite devuelve Kind=Unspecified: se trata como UTC y se pasa al día del usuario.
        var utc = DateTime.SpecifyKind(new DateTime(2026, 9, 1, 12, 0, 0), DateTimeKind.Unspecified);
        var esperado = DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().Date;

        var (inicio, _) = SeguimientoFechas.Calcular(Anime("RELEASING", 12), new[] { Visto(1, utc) }, null, 1);

        inicio.Should().Be(esperado);
    }

    // ── Cableado ──

    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<IAnimeTrackingService> _tracking = new();

    [Fact]
    public async Task Aplicar_EnviaSoloLasFechasQueFaltan()
    {
        _db.Setup(d => d.ObtenerRegistrosPorAnimeAsync(7)).ReturnsAsync(new List<RegistroEpisodio> { Visto(1, Dia1) });
        _db.Setup(d => d.ObtenerAnimePorIdAsync(7)).ReturnsAsync(Anime("RELEASING", 28));
        _tracking.Setup(t => t.GuardarFechasSeguimientoAsync(7, It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), "tok", It.IsAny<bool>())).ReturnsAsync(true);

        bool ok = await SeguimientoFechas.AplicarAsync(_db.Object, _tracking.Object, 7, null, 1, "tok");

        ok.Should().BeTrue();
        _tracking.Verify(t => t.GuardarFechasSeguimientoAsync(7, Dia1.Date, null, "tok", false), Times.Once);
    }

    [Fact]
    public async Task Aplicar_SinNadaQueGuardar_NoLlamaAAniList()
    {
        _db.Setup(d => d.ObtenerRegistrosPorAnimeAsync(7)).ReturnsAsync(new List<RegistroEpisodio> { Visto(1, null) });
        _db.Setup(d => d.ObtenerAnimePorIdAsync(7)).ReturnsAsync(Anime("RELEASING", 28));

        bool ok = await SeguimientoFechas.AplicarAsync(_db.Object, _tracking.Object, 7, null, 1, "tok");

        ok.Should().BeFalse();
        _tracking.Verify(t => t.GuardarFechasSeguimientoAsync(It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task Aplicar_UnFalloDeRedNoSePropaga()
    {
        _db.Setup(d => d.ObtenerRegistrosPorAnimeAsync(7)).ThrowsAsync(new InvalidOperationException("db"));

        bool ok = await SeguimientoFechas.AplicarAsync(_db.Object, _tracking.Object, 7, null, 1, "tok");

        ok.Should().BeFalse();
    }

    [Fact]
    public async Task Reproduccion_DelUltimoEpisodioOficial_SubeProgresoYLuegoFechaDeFinYCompletado()
    {
        var auth = new Mock<IAuthService>();
        auth.Setup(a => a.ObtenerTokenGuardado()).Returns("tok");
        var previo = Remoto(progreso: 11, inicio: new AniListFuzzyDate { Year = 2026, Month = 9, Day = 1 });
        _tracking.Setup(t => t.ObtenerSeguimientoUsuarioAsync(7, "tok")).ReturnsAsync(previo);
        _tracking.Setup(t => t.ActualizarProgresoAsync(7, 12, "tok")).ReturnsAsync(true);
        _tracking.Setup(t => t.GuardarFechasSeguimientoAsync(7, It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), "tok", It.IsAny<bool>())).ReturnsAsync(true);
        _db.Setup(d => d.ObtenerRegistrosPorAnimeAsync(7)).ReturnsAsync(new List<RegistroEpisodio> { Visto(1, Dia1), Visto(12, DateTime.UtcNow) });
        _db.Setup(d => d.ObtenerAnimePorIdAsync(7)).ReturnsAsync(Anime("FINISHED", 12));
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.ObtenerConfiguracion()).Returns(new AppSettings());
        var sut = new PlaybackStateService(_db.Object, _tracking.Object, auth.Object, settings.Object);

        await sut.MarcarComoVistoYSincronizarAsync(7, 12, @"C:\v\12.mkv", 1400);

        _tracking.Verify(t => t.GuardarFechasSeguimientoAsync(7, null, DateTime.Today, "tok", true), Times.Once);
    }

    [Fact]
    public async Task MarcadoManual_NoLeeElSeguimientoNiGuardaFechas()
    {
        var auth = new Mock<IAuthService>();
        auth.Setup(a => a.ObtenerTokenGuardado()).Returns("tok");
        _tracking.Setup(t => t.ActualizarProgresoAsync(7, 5, "tok")).ReturnsAsync(true);
        _db.Setup(d => d.ObtenerRegistrosPorAnimeAsync(7)).ReturnsAsync(new List<RegistroEpisodio>());
        var sut = new PlaybackStateService(_db.Object, _tracking.Object, auth.Object);

        await sut.MarcarComoVistoYSincronizarAsync(7, 5, @"C:\v\5.mkv", 1400, registrarReproduccion: false);

        _tracking.Verify(t => t.GuardarFechasSeguimientoAsync(It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        _tracking.Verify(t => t.ObtenerSeguimientoUsuarioAsync(It.IsAny<int>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Reproduccion_SiNoSeSubeElProgreso_NoSeGuardanFechas()
    {
        var auth = new Mock<IAuthService>();
        auth.Setup(a => a.ObtenerTokenGuardado()).Returns("tok");
        _tracking.Setup(t => t.ActualizarProgresoAsync(7, 1, "tok")).ReturnsAsync(false);
        _db.Setup(d => d.ObtenerRegistrosPorAnimeAsync(7)).ReturnsAsync(new List<RegistroEpisodio>());
        var sut = new PlaybackStateService(_db.Object, _tracking.Object, auth.Object);

        await sut.MarcarComoVistoYSincronizarAsync(7, 1, @"C:\v\1.mkv", 1400);

        _tracking.Verify(t => t.GuardarFechasSeguimientoAsync(It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task Sincronizacion_DeEpisodiosVistosSinConexion_TambienGuardaLasFechas()
    {
        // Vio los 3 episodios (serie de 3, finalizada) sin internet: al volver la red se suben progreso y fechas.
        var auth = new Mock<IAuthService>();
        auth.Setup(a => a.EstaAutenticado()).Returns(true);
        auth.Setup(a => a.ObtenerToken()).Returns("tok");
        var pendientes = new List<RegistroEpisodio> { Visto(1, Dia1), Visto(2, Dia1.AddDays(1)), Visto(3, Dia1.AddDays(2)) };
        _db.Setup(d => d.ObtenerEpisodiosNoSincronizadosAsync()).ReturnsAsync(pendientes);
        _db.Setup(d => d.ObtenerRegistrosPorAnimeAsync(7)).ReturnsAsync(pendientes);
        _db.Setup(d => d.ObtenerAnimePorIdAsync(7)).ReturnsAsync(Anime("FINISHED", 3));
        _tracking.Setup(t => t.ObtenerSeguimientoUsuarioAsync(7, "tok")).ReturnsAsync((AniListMediaList?)null);
        _tracking.Setup(t => t.ActualizarProgresoAsync(7, 3, "tok")).ReturnsAsync(true);
        _tracking.Setup(t => t.GuardarFechasSeguimientoAsync(7, It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), "tok", It.IsAny<bool>())).ReturnsAsync(true);
        using var sync = new SyncService(_db.Object, _tracking.Object, auth.Object);

        var (exitosos, _) = await sync.SincronizarPendientesAsync();

        exitosos.Should().Be(3);
        _tracking.Verify(t => t.GuardarFechasSeguimientoAsync(7, Dia1.Date, Dia1.AddDays(2).Date, "tok", true), Times.Once);
    }

    [Fact]
    public async Task Aplicar_AlCompletarLaSerie_ActualizaTambienElEstadoDeLaCopiaLocal()
    {
        var anime = Anime("FINISHED", 12);
        anime.EstadoUsuario = "CURRENT";
        _db.Setup(d => d.ObtenerRegistrosPorAnimeAsync(7)).ReturnsAsync(new List<RegistroEpisodio> { Visto(1, Dia1), Visto(12, Dia12) });
        _db.Setup(d => d.ObtenerAnimePorIdAsync(7)).ReturnsAsync(anime);
        _tracking.Setup(t => t.GuardarFechasSeguimientoAsync(7, It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), "tok", It.IsAny<bool>())).ReturnsAsync(true);

        await SeguimientoFechas.AplicarAsync(_db.Object, _tracking.Object, 7, Remoto(progreso: 11, inicio: new AniListFuzzyDate { Year = 2026, Month = 9, Day = 1 }), 12, "tok");

        anime.EstadoUsuario.Should().Be("COMPLETED");
        _db.Verify(d => d.ActualizarAnimeAsync(anime), Times.Once);
    }

    [Fact]
    public async Task Aplicar_SoloInicio_NoMarcaCompletadoNiToca_ElEstadoLocal()
    {
        var anime = Anime("RELEASING", 28);
        anime.EstadoUsuario = "PLANNING";
        _db.Setup(d => d.ObtenerRegistrosPorAnimeAsync(7)).ReturnsAsync(new List<RegistroEpisodio> { Visto(1, Dia1) });
        _db.Setup(d => d.ObtenerAnimePorIdAsync(7)).ReturnsAsync(anime);
        _tracking.Setup(t => t.GuardarFechasSeguimientoAsync(7, It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), "tok", It.IsAny<bool>())).ReturnsAsync(true);

        await SeguimientoFechas.AplicarAsync(_db.Object, _tracking.Object, 7, null, 1, "tok");

        anime.EstadoUsuario.Should().Be("PLANNING");
        _tracking.Verify(t => t.GuardarFechasSeguimientoAsync(7, Dia1.Date, null, "tok", false), Times.Once);
        _db.Verify(d => d.ActualizarAnimeAsync(It.IsAny<AnimeItem>()), Times.Never);
    }
}
