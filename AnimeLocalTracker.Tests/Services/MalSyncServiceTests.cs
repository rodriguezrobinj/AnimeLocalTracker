using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Mal;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

public class MalSyncServiceTests
{
    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<IMalAuthService> _auth = new();
    private readonly Mock<IMalApiClient> _api = new();
    private readonly List<SincronizacionMal> _guardadas = new();

    private static readonly AnimeItem Frieren = new() { AniListId = 154587, MalId = 52991, Titulo = "Frieren", EstadoUsuario = "CURRENT", TotalEpisodios = 28 };
    private static readonly AnimeItem Erased = new() { AniListId = 21234, MalId = 31043, Titulo = "Erased", EstadoUsuario = "COMPLETED", TotalEpisodios = 12 };

    public MalSyncServiceTests()
    {
        _auth.SetupGet(a => a.Configurado).Returns(true);
        _auth.SetupGet(a => a.Conectado).Returns(true);
        _db.Setup(d => d.ObtenerTodosLosAnimesAsync()).ReturnsAsync(new List<AnimeItem> { Frieren, Erased });
        _db.Setup(d => d.ObtenerEpisodiosVistosPorAnimeAsync()).ReturnsAsync(new Dictionary<int, int> { [Frieren.AniListId] = 14, [Erased.AniListId] = 12 });
        _db.Setup(d => d.ObtenerSeguimientosLocalesAsync()).ReturnsAsync(new List<SeguimientoLocal>());
        _db.Setup(d => d.ObtenerSincronizacionesMalAsync()).ReturnsAsync(new List<SincronizacionMal>());
        _db.Setup(d => d.GuardarSincronizacionMalAsync(It.IsAny<SincronizacionMal>())).Callback<SincronizacionMal>(_guardadas.Add).Returns(Task.CompletedTask);
        _api.Setup(a => a.LeerAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync((ResultadoMal.Ok, (EstadoMal?)null));
        _api.Setup(a => a.ActualizarAsync(It.IsAny<int>(), It.IsAny<EstadoMal>(), It.IsAny<CancellationToken>())).ReturnsAsync(ResultadoMal.Ok);
    }

    private MalSyncService Servicio() => new(_db.Object, _auth.Object, _api.Object, TimeSpan.Zero, TimeProvider.System);

    [Fact]
    public async Task Pasada_EnviaLoQueNoEstaEnMalYLoGuarda()
    {
        var resumen = await Servicio().SincronizarAhoraAsync(CancellationToken.None);

        _api.Verify(a => a.ActualizarAsync(52991, new EstadoMal("watching", 14, 0), It.IsAny<CancellationToken>()), Times.Once);
        _api.Verify(a => a.ActualizarAsync(31043, new EstadoMal("completed", 12, 0), It.IsAny<CancellationToken>()), Times.Once);
        _guardadas.Should().HaveCount(2);
        resumen!.Enviados.Should().Be(2);
        resumen.Completa.Should().BeTrue();
    }

    [Fact]
    public async Task Pasada_LoYaEnviadoIgual_NoHacePeticiones()
    {
        _db.Setup(d => d.ObtenerSincronizacionesMalAsync()).ReturnsAsync(new List<SincronizacionMal>
        {
            new() { AniListId = Frieren.AniListId, MalId = 52991, Estado = "watching", Progreso = 14, Nota = 0 },
            new() { AniListId = Erased.AniListId, MalId = 31043, Estado = "completed", Progreso = 12, Nota = 0 },
        });

        var resumen = await Servicio().SincronizarAhoraAsync(CancellationToken.None);

        _api.Verify(a => a.LeerAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        resumen!.Revisados.Should().Be(0);
    }

    [Fact]
    public async Task Pasada_MalMasAdelantado_NoLoTocaPeroLoApunta()
    {
        _api.Setup(a => a.LeerAsync(52991, It.IsAny<CancellationToken>())).ReturnsAsync((ResultadoMal.Ok, new EstadoMal("watching", 20, 0)));

        await Servicio().SincronizarAhoraAsync(CancellationToken.None);

        _api.Verify(a => a.ActualizarAsync(52991, It.IsAny<EstadoMal>(), It.IsAny<CancellationToken>()), Times.Never);
        _guardadas.Should().Contain(s => s.AniListId == Frieren.AniListId, "así no se vuelve a preguntar mientras no cambie nada en la app");
    }

    [Fact]
    public async Task Pasada_LimiteDeMal_SeDetieneSinSeguir()
    {
        _api.Setup(a => a.LeerAsync(52991, It.IsAny<CancellationToken>())).ReturnsAsync((ResultadoMal.LimiteAlcanzado, (EstadoMal?)null));

        var resumen = await Servicio().SincronizarAhoraAsync(CancellationToken.None);

        _api.Verify(a => a.LeerAsync(31043, It.IsAny<CancellationToken>()), Times.Never);
        _guardadas.Should().BeEmpty();
        resumen!.Completa.Should().BeFalse();
    }

    [Fact]
    public async Task Pasada_ErrorEnUnAnime_SigueConElSiguienteSinApuntarlo()
    {
        _api.Setup(a => a.LeerAsync(52991, It.IsAny<CancellationToken>())).ReturnsAsync((ResultadoMal.Error, (EstadoMal?)null));

        await Servicio().SincronizarAhoraAsync(CancellationToken.None);

        _api.Verify(a => a.ActualizarAsync(31043, It.IsAny<EstadoMal>(), It.IsAny<CancellationToken>()), Times.Once);
        _guardadas.Should().ContainSingle().Which.AniListId.Should().Be(Erased.AniListId);
    }

    [Fact]
    public async Task SinConectar_NoHaceNada()
    {
        _auth.SetupGet(a => a.Conectado).Returns(false);

        (await Servicio().SincronizarAhoraAsync(CancellationToken.None)).Should().BeNull();
        _api.Verify(a => a.LeerAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UltimaPasada_QuedaDisponibleYSeAvisa()
    {
        var servicio = Servicio();
        ResumenSincronizacionMal? avisado = null;
        servicio.PasadaTerminada += r => avisado = r;

        var resumen = await servicio.SincronizarAhoraAsync(CancellationToken.None);

        servicio.UltimaPasada.Should().Be(resumen);
        avisado.Should().Be(resumen);
    }
}
