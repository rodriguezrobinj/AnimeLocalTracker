using System;
using System.IO;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>Cuenta atrás del próximo episodio: caché local, revisión escalonada de la programación y formato.</summary>
public class ProximaEmisionServiceTests : IDisposable
{
    private static readonly DateTime Ahora = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<IAnimeTrackingService> _tracking = new();
    private readonly string _rutaDb = Path.Combine(Path.GetTempPath(), $"AnimeTracker_Proxima_{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { if (File.Exists(_rutaDb)) File.Delete(_rutaDb); } catch { /* ignore */ }
    }

    private static ProximaEmisionLocal Local(TimeSpan hastaEmision, TimeSpan haceConsultado, int episodio = 12) => new()
    {
        AniListId = 5,
        Episodio = episodio,
        EmisionUnixUtc = new DateTimeOffset(Ahora + hastaEmision).ToUnixTimeSeconds(),
        ConsultadoUtc = Ahora - haceConsultado
    };

    // ── Regla de revisión escalonada ──

    [Fact]
    public void SinCopiaLocal_ConsultaSiempre() => ProximaEmisionService.DebeConsultar(null, Ahora).Should().BeTrue();

    [Theory]
    [InlineData(10 * 24, 23, false)]   // falta mucho: se revisa cada 24 h
    [InlineData(10 * 24, 25, true)]
    [InlineData(48, 5, false)]         // entre 6 h y 3 días: cada 6 h
    [InlineData(48, 7, true)]
    [InlineData(3, 0.5, false)]        // menos de 6 h: cada hora
    [InlineData(3, 1.5, true)]
    public void EmisionFutura_SoloSeConsultaCuandoTocaSegunLaCercania(double horasHastaEmision, double horasDesdeConsulta, bool esperado)
    {
        var local = Local(TimeSpan.FromHours(horasHastaEmision), TimeSpan.FromHours(horasDesdeConsulta));

        ProximaEmisionService.DebeConsultar(local, Ahora).Should().Be(esperado);
    }

    [Fact]
    public void UnaCopiaFrescaNoConsultaAniList_AunqueHayaPasadoMuchoTiempoDeContador()
    {
        // Consultado hace 1 minuto, emisión en 5 días: el contador corre solo en local.
        ProximaEmisionService.DebeConsultar(Local(TimeSpan.FromDays(5), TimeSpan.FromMinutes(1)), Ahora).Should().BeFalse();
    }

    [Fact]
    public void TrasLaEmision_SiSeConsultoAntesDeEmitirse_ConsultaEnSeguida()
    {
        // Emitió hace 10 min y la última consulta fue ANTES: hay que buscar el episodio siguiente ya.
        var local = Local(TimeSpan.FromMinutes(-10), TimeSpan.FromHours(3));

        ProximaEmisionService.DebeConsultar(local, Ahora).Should().BeTrue();
    }

    [Fact]
    public void TrasLaEmision_SiAniListAunNoActualizo_ReintentaCada15Minutos()
    {
        var recien = Local(TimeSpan.FromMinutes(-20), TimeSpan.FromMinutes(5));
        var hace20 = Local(TimeSpan.FromMinutes(-40), TimeSpan.FromMinutes(20));

        ProximaEmisionService.DebeConsultar(recien, Ahora).Should().BeFalse();
        ProximaEmisionService.DebeConsultar(hace20, Ahora).Should().BeTrue();
    }

    [Fact]
    public void SinProgramacion_SeRevisaUnaVezAlDia()
    {
        ProximaEmisionService.DebeConsultar(Local(TimeSpan.Zero, TimeSpan.FromHours(2), episodio: 0), Ahora).Should().BeFalse();
        ProximaEmisionService.DebeConsultar(Local(TimeSpan.Zero, TimeSpan.FromHours(25), episodio: 0), Ahora).Should().BeTrue();
    }

    [Fact]
    public void ConsultadoUtcSinKind_SeTrataComoUtc()
    {
        // sqlite-net devuelve Kind=Unspecified.
        var local = Local(TimeSpan.FromDays(5), TimeSpan.FromHours(30));
        local.ConsultadoUtc = DateTime.SpecifyKind(local.ConsultadoUtc, DateTimeKind.Unspecified);

        ProximaEmisionService.DebeConsultar(local, Ahora).Should().BeTrue();
    }

    // ── Servicio ──

    private ProximaEmisionService CrearSut() => new(_db.Object, _tracking.Object);

    [Fact]
    public async Task ConCopiaFresca_NoLlamaAAniList()
    {
        _db.Setup(d => d.ObtenerProximaEmisionAsync(5)).ReturnsAsync(new ProximaEmisionLocal
        {
            AniListId = 5, Episodio = 12,
            EmisionUnixUtc = DateTimeOffset.UtcNow.AddDays(4).ToUnixTimeSeconds(),
            ConsultadoUtc = DateTime.UtcNow.AddMinutes(-5)
        });

        var resultado = await CrearSut().ObtenerAsync(5, "RELEASING");

        resultado.Should().NotBeNull();
        resultado!.Episodio.Should().Be(12);
        _tracking.Verify(t => t.ObtenerProximaEmisionAsync(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task SinCopia_ConsultaYGuardaLaEmision()
    {
        long unix = DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeSeconds();
        _db.Setup(d => d.ObtenerProximaEmisionAsync(5)).ReturnsAsync((ProximaEmisionLocal?)null);
        _tracking.Setup(t => t.ObtenerProximaEmisionAsync(5)).ReturnsAsync((true, new AniListNextAiringEpisode { Episode = 7, AiringAt = unix }));

        var resultado = await CrearSut().ObtenerAsync(5, "RELEASING");

        resultado!.Episodio.Should().Be(7);
        resultado.EmisionUtc.Should().Be(DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime);
        _db.Verify(d => d.GuardarProximaEmisionAsync(It.Is<ProximaEmisionLocal>(p => p.AniListId == 5 && p.Episodio == 7 && p.EmisionUnixUtc == unix)), Times.Once);
    }

    [Fact]
    public async Task UnCambioDeProgramacion_ActualizaLaCopiaLocal()
    {
        // La copia decía "episodio 12 en 2 días"; AniList lo retrasó una semana.
        _db.Setup(d => d.ObtenerProximaEmisionAsync(5)).ReturnsAsync(new ProximaEmisionLocal
        {
            AniListId = 5, Episodio = 12,
            EmisionUnixUtc = DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeSeconds(),
            ConsultadoUtc = DateTime.UtcNow.AddHours(-8) // toca revisar (más de 6 h con menos de 3 días)
        });
        long retrasada = DateTimeOffset.UtcNow.AddDays(9).ToUnixTimeSeconds();
        _tracking.Setup(t => t.ObtenerProximaEmisionAsync(5)).ReturnsAsync((true, new AniListNextAiringEpisode { Episode = 12, AiringAt = retrasada }));

        var resultado = await CrearSut().ObtenerAsync(5, "RELEASING");

        resultado!.EmisionUtc.Should().Be(DateTimeOffset.FromUnixTimeSeconds(retrasada).UtcDateTime);
    }

    [Fact]
    public async Task SiFallaLaRed_ConservaLaCopiaYNoReintentaEnCadaVisita()
    {
        var local = new ProximaEmisionLocal
        {
            AniListId = 5, Episodio = 12,
            EmisionUnixUtc = DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeSeconds(),
            ConsultadoUtc = DateTime.UtcNow.AddHours(-8)
        };
        _db.Setup(d => d.ObtenerProximaEmisionAsync(5)).ReturnsAsync(local);
        _tracking.Setup(t => t.ObtenerProximaEmisionAsync(5)).ReturnsAsync((false, (AniListNextAiringEpisode?)null));

        var resultado = await CrearSut().ObtenerAsync(5, "RELEASING");

        resultado!.Episodio.Should().Be(12, "sin red se sigue mostrando la cuenta atrás guardada");
        _db.Verify(d => d.GuardarProximaEmisionAsync(It.Is<ProximaEmisionLocal>(p => p.ConsultadoUtc > DateTime.UtcNow.AddMinutes(-1))), Times.Once);
    }

    [Fact]
    public async Task AnimeSinProximoEpisodio_DevuelveNulo()
    {
        _db.Setup(d => d.ObtenerProximaEmisionAsync(5)).ReturnsAsync((ProximaEmisionLocal?)null);
        _tracking.Setup(t => t.ObtenerProximaEmisionAsync(5)).ReturnsAsync((true, (AniListNextAiringEpisode?)null));

        (await CrearSut().ObtenerAsync(5, "RELEASING")).Should().BeNull();
    }

    [Theory]
    [InlineData("FINISHED")]
    [InlineData("CANCELLED")]
    [InlineData("")]
    public async Task AnimeQueNoEstaEnEmision_NoConsultaNada(string estado)
    {
        (await CrearSut().ObtenerAsync(5, estado)).Should().BeNull();

        _db.Verify(d => d.ObtenerProximaEmisionAsync(It.IsAny<int>()), Times.Never);
        _tracking.Verify(t => t.ObtenerProximaEmisionAsync(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Forzar_IgnoraLaAntiguedadDeLaCopia()
    {
        _db.Setup(d => d.ObtenerProximaEmisionAsync(5)).ReturnsAsync(new ProximaEmisionLocal
        {
            AniListId = 5, Episodio = 12,
            EmisionUnixUtc = DateTimeOffset.UtcNow.AddDays(4).ToUnixTimeSeconds(),
            ConsultadoUtc = DateTime.UtcNow
        });
        _tracking.Setup(t => t.ObtenerProximaEmisionAsync(5)).ReturnsAsync((true, new AniListNextAiringEpisode { Episode = 12, AiringAt = DateTimeOffset.UtcNow.AddDays(4).ToUnixTimeSeconds() }));

        await CrearSut().ObtenerAsync(5, "RELEASING", forzar: true);

        _tracking.Verify(t => t.ObtenerProximaEmisionAsync(5), Times.Once);
    }

    // ── Base de datos ──

    [Fact]
    public async Task BaseDeDatos_GuardaYReemplazaLaCopiaLocal_YVaciarBibliotecaLaBorra()
    {
        using var db = new DatabaseService(_rutaDb);
        await db.InicializarBaseDatosAsync();

        await db.GuardarProximaEmisionAsync(new ProximaEmisionLocal { AniListId = 5, Episodio = 3, EmisionUnixUtc = 1000, ConsultadoUtc = Ahora });
        await db.GuardarProximaEmisionAsync(new ProximaEmisionLocal { AniListId = 5, Episodio = 4, EmisionUnixUtc = 2000, ConsultadoUtc = Ahora });

        var guardada = await db.ObtenerProximaEmisionAsync(5);
        guardada!.Episodio.Should().Be(4, "una segunda escritura reemplaza a la primera");
        guardada.EmisionUnixUtc.Should().Be(2000);
        (await db.ObtenerProximaEmisionAsync(99)).Should().BeNull();

        await db.VaciarBibliotecaAsync();
        (await db.ObtenerProximaEmisionAsync(5)).Should().BeNull();
    }

    // ── Formato del contador ──

    [Theory]
    [InlineData(3 * 24 + 5, 12, "3 d 5 h")]
    [InlineData(24, 0, "1 d 0 h")]
    public void Formato_DesdeUnDia_MuestraDiasYHoras(int horas, int minutos, string esperado)
    {
        DetalleViewModel.FormatearCuentaAtras(TimeSpan.FromHours(horas) + TimeSpan.FromMinutes(minutos)).Should().Be(esperado);
    }

    [Fact]
    public void Formato_MenosDeUnDia_MuestraHoraMinutoSegundo()
    {
        DetalleViewModel.FormatearCuentaAtras(new TimeSpan(9, 37, 29)).Should().Be("09:37:29");
        DetalleViewModel.FormatearCuentaAtras(TimeSpan.FromSeconds(5)).Should().Be("00:00:05");
        DetalleViewModel.FormatearCuentaAtras(TimeSpan.FromSeconds(-30)).Should().Be("00:00:00");
    }

    // ── Refresco desde "Actualizar biblioteca" (misma consulta, sin peticiones extra) ──

    [Fact]
    public void CopiaDesdeConsulta_GuardaLaHoraDeUnAnimeEnEmision()
    {
        var copia = ProximaEmisionService.CopiaDesdeConsulta(5, "RELEASING", new AniListNextAiringEpisode { Episode = 12, AiringAt = 1789997400 }, Ahora);

        copia.Should().NotBeNull();
        copia!.Episodio.Should().Be(12);
        copia.EmisionUnixUtc.Should().Be(1789997400);
        copia.ConsultadoUtc.Should().Be(Ahora);
    }

    [Fact]
    public void CopiaDesdeConsulta_EnEmisionSinProximoEpisodio_GuardaQueNoHayProgramacion()
    {
        var copia = ProximaEmisionService.CopiaDesdeConsulta(5, "RELEASING", null, Ahora);

        copia!.Episodio.Should().Be(0);
        copia.EmisionUnixUtc.Should().Be(0);
    }

    [Theory]
    [InlineData("FINISHED")]
    [InlineData("CANCELLED")]
    [InlineData(null)]
    public void CopiaDesdeConsulta_IgnoraLosAnimesQueNoEstanEnEmision(string? estado)
    {
        ProximaEmisionService.CopiaDesdeConsulta(5, estado, new AniListNextAiringEpisode { Episode = 12, AiringAt = 1789997400 }, Ahora).Should().BeNull();
    }

    [Fact]
    public void CopiaDesdeConsulta_IgnoraDatosSinHoraDeEmision()
    {
        // Respuesta de una consulta antigua que aún no pedía airingAt: no se pisa una copia buena con un dato vacío.
        ProximaEmisionService.CopiaDesdeConsulta(5, "RELEASING", new AniListNextAiringEpisode { Episode = 12, AiringAt = 0 }, Ahora).Should().BeNull();
    }
}
