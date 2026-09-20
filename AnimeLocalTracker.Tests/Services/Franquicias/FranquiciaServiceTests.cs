#pragma warning disable CA1861 // Datos constantes de prueba: un arreglo por llamada es lo más legible aquí
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Franquicias;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services.Franquicias;

public class FranquiciaServiceTests
{
    private readonly Mock<IDatabaseService> _dbMock = new();
    private readonly Mock<IAnimeTrackingService> _trackingMock = new();
    private readonly List<RelacionAnimeSync> _sincronizados = new();
    private readonly List<RelacionAnime> _aristas = new();

    public FranquiciaServiceTests()
    {
        _dbMock.Setup(d => d.ObtenerRelacionesSincronizadasAsync()).ReturnsAsync(() => _sincronizados.ToList());
        _dbMock.Setup(d => d.ObtenerRelacionesAnimeAsync()).ReturnsAsync(() => _aristas.ToList());
        _dbMock.Setup(d => d.GuardarRelacionesAnimeAsync(It.IsAny<IReadOnlyDictionary<int, List<RelacionAnime>>>()))
            .Returns<IReadOnlyDictionary<int, List<RelacionAnime>>>(porAnime =>
            {
                foreach (var (id, lista) in porAnime)
                {
                    _sincronizados.RemoveAll(s => s.AnimeId == id);
                    _sincronizados.Add(new RelacionAnimeSync { AnimeId = id, FechaUtc = DateTime.UtcNow });
                    _aristas.RemoveAll(a => a.AnimeId == id);
                    _aristas.AddRange(lista);
                }
                return Task.CompletedTask;
            });
    }

    private FranquiciaService CrearSut() => new(_dbMock.Object, _trackingMock.Object);

    private static RelacionAnime Rel(int desde, int hacia, string tipo) => new() { AnimeId = desde, RelacionadoId = hacia, Tipo = tipo };

    /// <summary>El "AniList" simulado: cada id devuelve sus aristas conocidas (o ninguna).</summary>
    private void SimularAniList(Dictionary<int, List<RelacionAnime>> red) =>
        _trackingMock.Setup(t => t.ObtenerRelacionesLoteAsync(It.IsAny<IEnumerable<int>>()))
            .ReturnsAsync((IEnumerable<int> ids) =>
                ids.ToDictionary(id => id, id => red.TryGetValue(id, out var aristas) ? aristas : new List<RelacionAnime>()));

    [Fact]
    public async Task Sincronizar_PrimeraVez_ConsultaLaBibliotecaYGuarda()
    {
        SimularAniList(new() { [1] = new() { Rel(1, 2, "SEQUEL") } });
        var sut = CrearSut();

        var huboDatos = await sut.SincronizarAsync(new[] { 1 });

        huboDatos.Should().BeTrue();
        _sincronizados.Select(s => s.AnimeId).Should().Contain(1);
        _aristas.Should().Contain(a => a.AnimeId == 1 && a.RelacionadoId == 2);
    }

    [Fact]
    public async Task Sincronizar_ExploraLosVecinosFueraDeLaBibliotecaParaEncontrarPuentes()
    {
        // Biblioteca: 1 y 3. La 2 (que no tienes) las une: 1 → 2 → 3
        SimularAniList(new()
        {
            [1] = new() { Rel(1, 2, "SEQUEL") },
            [2] = new() { Rel(2, 1, "PREQUEL"), Rel(2, 3, "SEQUEL") },
            [3] = new() { Rel(3, 2, "PREQUEL") }
        });
        var sut = CrearSut();

        await sut.SincronizarAsync(new[] { 1, 3 });
        var mapa = await sut.ObtenerMapaAsync(new[] { 1, 3 });

        _sincronizados.Select(s => s.AnimeId).Should().Contain(2, "el nodo puente también se consultó");
        mapa[1].Should().Be(mapa[3]);
    }

    [Fact]
    public async Task Sincronizar_NoSigueRelacionesQueNoSonDeFranquicia()
    {
        SimularAniList(new() { [1] = new() { Rel(1, 99, "CHARACTER"), Rel(1, 98, "ADAPTATION") } });
        var sut = CrearSut();

        await sut.SincronizarAsync(new[] { 1 });

        _trackingMock.Verify(t => t.ObtenerRelacionesLoteAsync(It.Is<IEnumerable<int>>(ids => ids.Contains(99) || ids.Contains(98))), Times.Never);
    }

    [Fact]
    public async Task Sincronizar_LoYaSincronizadoRecientemente_NoVuelveAConsultar()
    {
        _sincronizados.Add(new RelacionAnimeSync { AnimeId = 1, FechaUtc = DateTime.UtcNow.AddDays(-2) });
        SimularAniList(new());
        var sut = CrearSut();

        var huboDatos = await sut.SincronizarAsync(new[] { 1 });

        huboDatos.Should().BeFalse();
        _trackingMock.Verify(t => t.ObtenerRelacionesLoteAsync(It.IsAny<IEnumerable<int>>()), Times.Never);
    }

    [Fact]
    public async Task Sincronizar_LoCaducado_SeRefresca()
    {
        _sincronizados.Add(new RelacionAnimeSync { AnimeId = 1, FechaUtc = DateTime.UtcNow.AddDays(-45) });
        SimularAniList(new() { [1] = new() { Rel(1, 2, "SEQUEL") } });
        var sut = CrearSut();

        var huboDatos = await sut.SincronizarAsync(new[] { 1 });

        huboDatos.Should().BeTrue();
        _trackingMock.Verify(t => t.ObtenerRelacionesLoteAsync(It.Is<IEnumerable<int>>(ids => ids.Contains(1))), Times.AtLeastOnce);
    }

    [Fact]
    public async Task Sincronizar_SoloPideLoQueFalta()
    {
        _sincronizados.Add(new RelacionAnimeSync { AnimeId = 1, FechaUtc = DateTime.UtcNow });
        SimularAniList(new());
        var sut = CrearSut();

        await sut.SincronizarAsync(new[] { 1, 2 });

        _trackingMock.Verify(t => t.ObtenerRelacionesLoteAsync(It.Is<IEnumerable<int>>(ids => ids.Contains(2) && !ids.Contains(1))), Times.Once);
    }

    [Fact]
    public async Task Sincronizar_SinConexion_NoLanzaYNoInsisteEnSeguida()
    {
        _trackingMock.Setup(t => t.ObtenerRelacionesLoteAsync(It.IsAny<IEnumerable<int>>()))
            .ReturnsAsync(new Dictionary<int, List<RelacionAnime>>());
        var sut = CrearSut();

        var primero = await sut.SincronizarAsync(new[] { 1 });
        var segundo = await sut.SincronizarAsync(new[] { 1 });

        primero.Should().BeFalse();
        segundo.Should().BeFalse();
        _trackingMock.Verify(t => t.ObtenerRelacionesLoteAsync(It.IsAny<IEnumerable<int>>()), Times.Once, "tras un fallo no se reintenta de inmediato");
    }

    [Fact]
    public async Task Sincronizar_UnaCadenaInterminable_SeDetieneEnElMaximoDeRondas()
    {
        // Cada anime N tiene una secuela N+1, sin fin
        _trackingMock.Setup(t => t.ObtenerRelacionesLoteAsync(It.IsAny<IEnumerable<int>>()))
            .ReturnsAsync((IEnumerable<int> ids) => ids.ToDictionary(id => id, id => new List<RelacionAnime> { Rel(id, id + 1, "SEQUEL") }));
        var sut = CrearSut();

        await sut.SincronizarAsync(new[] { 1 });

        _trackingMock.Verify(t => t.ObtenerRelacionesLoteAsync(It.IsAny<IEnumerable<int>>()), Times.Exactly(4));
    }

    [Fact]
    public async Task Sincronizar_UnErrorInesperado_SeAbsorbe()
    {
        _trackingMock.Setup(t => t.ObtenerRelacionesLoteAsync(It.IsAny<IEnumerable<int>>())).ThrowsAsync(new InvalidOperationException("boom"));
        var sut = CrearSut();

        var act = async () => await sut.SincronizarAsync(new[] { 1 });

        await act.Should().NotThrowAsync();
        (await sut.SincronizarAsync(new[] { 1 })).Should().BeFalse();
    }

    [Fact]
    public async Task ObtenerMapa_UsaLasRelacionesGuardadas_SinTocarLaRed()
    {
        _aristas.Add(Rel(1, 2, "SEQUEL"));
        var sut = CrearSut();

        var mapa = await sut.ObtenerMapaAsync(new[] { 1, 2, 3 });

        mapa[1].Should().Be(mapa[2]);
        mapa[3].Should().Be(3);
        _trackingMock.Verify(t => t.ObtenerRelacionesLoteAsync(It.IsAny<IEnumerable<int>>()), Times.Never);
    }
}
