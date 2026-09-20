using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Logros;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services.Logros;

public class LogrosServiceTests
{
    private readonly Mock<IDatabaseService> _dbMock = new();
    private readonly Mock<IDialogService> _dialogMock = new();
    private readonly List<LogroDesbloqueado> _tabla = new();

    public LogrosServiceTests()
    {
        // La "tabla" en memoria imita el INSERT OR IGNORE con índice único (LogroId, Nivel).
        _dbMock.Setup(d => d.ObtenerLogrosDesbloqueadosAsync()).ReturnsAsync(() => _tabla.ToList());
        _dbMock.Setup(d => d.GuardarLogrosDesbloqueadosAsync(It.IsAny<IEnumerable<LogroDesbloqueado>>()))
            .Returns<IEnumerable<LogroDesbloqueado>>(nuevos =>
            {
                foreach (var n in nuevos)
                {
                    if (!_tabla.Any(t => t.LogroId == n.LogroId && t.Nivel == n.Nivel)) _tabla.Add(n);
                }
                return Task.CompletedTask;
            });
    }

    private LogrosService CrearSut() => new(_dbMock.Object, _dialogMock.Object);

    private static AnimeItem Anime(int id) => new() { AniListId = id, Titulo = $"Anime {id}", TotalEpisodios = 12 };

    private static List<RegistroEpisodio> Vistos(int cantidad, int anime = 1) =>
        Enumerable.Range(1, cantidad)
            .Select(i => new RegistroEpisodio
            {
                AniListId = anime, NumeroEpisodio = i, VistoLocal = true, TotalSegundos = 1400,
                UltimaReproduccion = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Local).AddDays(i)
            })
            .ToList();

    private void VerificarToasts(Times veces) =>
        _dialogMock.Verify(d => d.MostrarToast(It.IsAny<string>(), It.IsAny<string>(), "TrophyAward", It.IsAny<string>()), veces);

    [Fact]
    public async Task PrimeraEvaluacion_GuardaLoQueYaCumplesSinFechaYSinAvisos()
    {
        var sut = CrearSut();

        var resumen = await sut.EvaluarAsync(new List<AnimeItem> { Anime(1) }, Vistos(30));

        resumen.NivelesDesbloqueados.Should().BeGreaterThan(0);
        VerificarToasts(Times.Never());

        var conNivel = _tabla.Where(t => t.LogroId != LogrosService.MarcadorBase).ToList();
        conNivel.Should().NotBeEmpty();
        conNivel.Should().OnlyContain(t => t.FechaUtc == null, "no se conoce la fecha real y no se inventa");
        _tabla.Should().ContainSingle(t => t.LogroId == LogrosService.MarcadorBase, "marca que la evaluación inicial ya se hizo");
    }

    [Fact]
    public async Task DespuesDeLaPrimeraVez_UnNivelNuevoSeGuardaConFechaYAvisaUnaVez()
    {
        var sut = CrearSut();
        await sut.EvaluarAsync(new List<AnimeItem> { Anime(1) }, Vistos(4));   // línea base: episodios=4 (< 25)
        _dialogMock.Invocations.Clear();

        // Ahora llega al primer nivel de "episodios" (25): 1 solo nivel nuevo
        await sut.EvaluarAsync(new List<AnimeItem> { Anime(1) }, Vistos(25));

        var nuevo = _tabla.Single(t => t.LogroId == "episodios" && t.Nivel == 1);
        nuevo.FechaUtc.Should().NotBeNull().And.Subject.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(10));
        VerificarToasts(Times.Once());
    }

    [Fact]
    public async Task NoRepiteElAvisoSiElNivelYaEstabaGuardado()
    {
        var sut = CrearSut();
        await sut.EvaluarAsync(new List<AnimeItem> { Anime(1) }, Vistos(4));
        await sut.EvaluarAsync(new List<AnimeItem> { Anime(1) }, Vistos(25));
        _dialogMock.Invocations.Clear();

        await sut.EvaluarAsync(new List<AnimeItem> { Anime(1) }, Vistos(25));

        VerificarToasts(Times.Never());
    }

    [Fact]
    public async Task MuchosNivelesNuevosDeGolpe_SeResumenEnUnSoloAviso()
    {
        var sut = CrearSut();
        await sut.EvaluarAsync(new List<AnimeItem>(), new List<RegistroEpisodio>());   // línea base vacía
        _dialogMock.Invocations.Clear();

        // Importar una biblioteca grande: desbloquea decenas de niveles a la vez
        var animes = Enumerable.Range(1, 30).Select(Anime).ToList();
        var registros = Enumerable.Range(1, 30).SelectMany(a => Vistos(12, a)).ToList();
        await sut.EvaluarAsync(animes, registros);

        _tabla.Count(t => t.LogroId != LogrosService.MarcadorBase && t.FechaUtc != null).Should().BeGreaterThan(3);
        VerificarToasts(Times.Once());
    }

    [Fact]
    public async Task NotificarFalse_GuardaPeroNoAvisa()
    {
        var sut = CrearSut();
        await sut.EvaluarAsync(new List<AnimeItem> { Anime(1) }, Vistos(4));

        await sut.EvaluarAsync(new List<AnimeItem> { Anime(1) }, Vistos(25), notificar: false);

        _tabla.Should().Contain(t => t.LogroId == "episodios" && t.Nivel == 1);
        VerificarToasts(Times.Never());
    }

    [Fact]
    public async Task UnNivelGuardadoSeConservaAunqueLaBibliotecaSeVacie()
    {
        var sut = CrearSut();
        await sut.EvaluarAsync(new List<AnimeItem> { Anime(1) }, Vistos(30));

        var resumen = await sut.EvaluarAsync(new List<AnimeItem>(), new List<RegistroEpisodio>());

        resumen.Logros.Single(l => l.Id == "episodios").NivelActual.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task EvaluarSinArgumentos_LeeLaBibliotecaDeLaBaseDeDatos()
    {
        _dbMock.Setup(d => d.ObtenerTodosLosAnimesAsync()).ReturnsAsync(new List<AnimeItem> { Anime(1) });
        _dbMock.Setup(d => d.ObtenerTodosLosRegistrosAsync()).ReturnsAsync(Vistos(30));
        var sut = CrearSut();

        var resumen = await sut.EvaluarAsync();

        resumen.Logros.Single(l => l.Id == "episodios").Valor.Should().Be(30);
        _dbMock.Verify(d => d.ObtenerTodosLosAnimesAsync(), Times.Once);
    }

    [Fact]
    public async Task EvaluacionesSimultaneas_NoDuplicanNivelesNiAvisos()
    {
        var sut = CrearSut();
        await sut.EvaluarAsync(new List<AnimeItem> { Anime(1) }, Vistos(4));
        _dialogMock.Invocations.Clear();

        // Estadísticas y el reproductor evalúan a la vez tras llegar al nivel
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => sut.EvaluarAsync(new List<AnimeItem> { Anime(1) }, Vistos(25))));

        _tabla.Count(t => t.LogroId == "episodios" && t.Nivel == 1).Should().Be(1);
        VerificarToasts(Times.Once());
    }
}
