using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Logros;
using AnimeLocalTracker.Services.Minijuegos;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services.Logros;

/// <summary>Familias de logros de la categoría Minijuegos: métricas calculadas desde las partidas y su evaluación.</summary>
public class LogrosMinijuegosTests
{
    private static readonly string[] IdsMinijuegos =
        ["mini_partidas", "mini_aciertos", "mini_puntuacion", "mini_perfectas", "mini_racha", "mini_oped"];

    private static PartidaMinijuego Partida(int puntos, int aciertos, int rondas = 10, int racha = 0, string juego = JuegosMinijuego.AdivinaAnime) => new()
    {
        JuegoId = juego, FechaUtc = DateTime.UtcNow, Puntos = puntos, Aciertos = aciertos, Rondas = rondas, RachaMaxima = racha
    };

    private static Dictionary<string, double> Metricas(params PartidaMinijuego[] partidas) =>
        MotorLogros.CalcularMetricas(new List<AnimeItem>(), new List<RegistroEpisodio>(), partidas);

    [Fact]
    public void Catalogo_DeberiaTenerLasSeisFamiliasDeMinijuegosConCincoNivelesCadaUna()
    {
        var familias = CatalogoLogros.Todos.Where(l => l.Categoria == CategoriaLogro.Minijuegos).ToList();

        familias.Select(l => l.Id).Should().BeEquivalentTo(IdsMinijuegos);
        familias.Should().OnlyContain(l => l.NivelesTotales == 5 && !l.Secreto);
    }

    [Fact]
    public void Metricas_SinPartidas_DeberianSerCeroYEstarTodasPresentes()
    {
        var metricas = MotorLogros.CalcularMetricas(new List<AnimeItem>(), new List<RegistroEpisodio>());

        foreach (var id in IdsMinijuegos) metricas[id].Should().Be(0, id);
    }

    [Fact]
    public void Metricas_DeberianAgregarLasPartidas()
    {
        var m = Metricas(
            Partida(300, 4, racha: 2),
            Partida(1000, 10, racha: 10),
            Partida(800, 8, racha: 5, juego: JuegosMinijuego.AdivinaOpEd),
            Partida(200, 3, rondas: 3, racha: 3, juego: JuegosMinijuego.AdivinaOpEd));

        m["mini_partidas"].Should().Be(4);
        m["mini_aciertos"].Should().Be(25);
        m["mini_puntuacion"].Should().Be(1000);
        m["mini_racha"].Should().Be(10);
        m["mini_perfectas"].Should().Be(1, "la de 3 rondas acertadas es demasiado corta para contar");
        m["mini_oped"].Should().Be(11, "solo los aciertos de Adivina el OP/ED");
    }

    [Fact]
    public void Evaluar_DeberiaDesbloquearNivelesSegunLosUmbrales()
    {
        var metricas = Metricas(Partida(720, 9, racha: 5));

        var resumen = MotorLogros.Evaluar(metricas, new Dictionary<string, (int Nivel, DateTime? FechaUtc)>());

        resumen.Logros.Single(l => l.Id == "mini_partidas").NivelActual.Should().Be(1);
        resumen.Logros.Single(l => l.Id == "mini_aciertos").NivelActual.Should().Be(0, "hacen falta 10 aciertos");
        resumen.Logros.Single(l => l.Id == "mini_puntuacion").NivelActual.Should().Be(3, "300, 500 y 700 superados");
        resumen.Logros.Single(l => l.Id == "mini_racha").NivelActual.Should().Be(2, "3 y 5 superados");
        resumen.Logros.Single(l => l.Id == "mini_oped").NivelActual.Should().Be(0);
    }

    [Fact]
    public void Evaluar_UnNivelYaConseguidoNoSePierdeAunqueDesaparezcanLasPartidas()
    {
        var guardados = new Dictionary<string, (int Nivel, DateTime? FechaUtc)> { ["mini_partidas"] = (2, DateTime.UtcNow) };

        var resumen = MotorLogros.Evaluar(Metricas(), guardados);

        resumen.Logros.Single(l => l.Id == "mini_partidas").NivelActual.Should().Be(2);
    }

    // ── Servicio: las partidas se leen de la base de datos y avisan al desbloquear ──

    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<IDialogService> _dialogos = new();
    private readonly List<LogroDesbloqueado> _tabla = new();

    private LogrosService CrearServicio(List<PartidaMinijuego> partidas)
    {
        _db.Setup(d => d.ObtenerLogrosDesbloqueadosAsync()).ReturnsAsync(() => _tabla.ToList());
        _db.Setup(d => d.GuardarLogrosDesbloqueadosAsync(It.IsAny<IEnumerable<LogroDesbloqueado>>()))
            .Returns<IEnumerable<LogroDesbloqueado>>(nuevos =>
            {
                foreach (var n in nuevos)
                    if (!_tabla.Any(t => t.LogroId == n.LogroId && t.Nivel == n.Nivel)) _tabla.Add(n);
                return Task.CompletedTask;
            });
        _db.Setup(d => d.ObtenerPartidasMinijuegoAsync()).ReturnsAsync(() => partidas.ToList());
        return new LogrosService(_db.Object, _dialogos.Object);
    }

    [Fact]
    public async Task Servicio_DespuesDeLaPrimeraEvaluacion_TerminarUnaPartidaAvisaDelLogroNuevo()
    {
        var partidas = new List<PartidaMinijuego>();
        var sut = CrearServicio(partidas);
        await sut.EvaluarAsync(new List<AnimeItem>(), new List<RegistroEpisodio>()); // evaluación inicial silenciosa
        _dialogos.Invocations.Clear();

        partidas.Add(Partida(500, 5, racha: 3));
        var resumen = await sut.EvaluarAsync(new List<AnimeItem>(), new List<RegistroEpisodio>());

        resumen.Logros.Single(l => l.Id == "mini_partidas").NivelActual.Should().Be(1);
        _tabla.Should().Contain(t => t.LogroId == "mini_partidas" && t.Nivel == 1 && t.FechaUtc != null, "se guarda con la fecha real del desbloqueo");
        _dialogos.Verify(d => d.MostrarToast(It.IsAny<string>(), It.IsAny<string>(), "TrophyAward", It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task Servicio_SiLaBaseNoDevuelvePartidas_NoFalla()
    {
        var sut = CrearServicio([]);
        _db.Setup(d => d.ObtenerPartidasMinijuegoAsync()).ReturnsAsync((List<PartidaMinijuego>)null!);

        var act = async () => await sut.EvaluarAsync(new List<AnimeItem>(), new List<RegistroEpisodio>());

        await act.Should().NotThrowAsync();
    }
}
