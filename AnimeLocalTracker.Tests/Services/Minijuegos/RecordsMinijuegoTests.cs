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

namespace AnimeLocalTracker.Tests.Services.Minijuegos;

public class RecordsMinijuegoTests
{
    private static PartidaMinijuego Partida(int puntos, int aciertos = 5, int rondas = 10, int racha = 3, string juego = JuegosMinijuego.AdivinaAnime, int dia = 1) => new()
    {
        JuegoId = juego,
        FechaUtc = new DateTime(2026, 9, dia, 12, 0, 0, DateTimeKind.Utc),
        Puntos = puntos,
        Aciertos = aciertos,
        Rondas = rondas,
        RachaMaxima = racha
    };

    [Fact]
    public void Calcular_SinPartidas_DeberiaDevolverVacio()
    {
        var r = RecordsMinijuego.Calcular([]);

        r.Should().Be(RecordsMinijuego.Vacio);
        r.HayPartidas.Should().BeFalse();
        r.PrecisionPorcentaje.Should().Be(0);
    }

    [Fact]
    public void Calcular_DeberiaTomarLaMejorPuntuacionYSumarLosTotales()
    {
        var r = RecordsMinijuego.Calcular([Partida(300, 4, 10, 2), Partida(760, 9, 10, 6, dia: 3), Partida(500, 7, 10, 4, dia: 2)]);

        r.MejorPuntuacion.Should().Be(760);
        r.FechaMejorUtc.Should().Be(new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc));
        r.Partidas.Should().Be(3);
        r.Aciertos.Should().Be(20);
        r.Rondas.Should().Be(30);
        r.MejorRacha.Should().Be(6);
    }

    [Fact]
    public void Calcular_ConEmpateEnLaMejorPuntuacion_DeberiaQuedarseConLaPrimeraQueLaLogro()
    {
        var r = RecordsMinijuego.Calcular([Partida(500, dia: 9), Partida(500, dia: 2), Partida(100, dia: 1)]);

        r.FechaMejorUtc.Should().Be(new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc));
    }

    [Theory]
    [InlineData(7, 10, 70)]
    [InlineData(2, 3, 67)]
    [InlineData(1, 8, 13)]
    [InlineData(0, 10, 0)]
    public void PrecisionPorcentaje_DeberiaRedondearElPorcentajeDeAciertos(int aciertos, int rondas, int esperado)
    {
        RecordsMinijuego.Calcular([Partida(0, aciertos, rondas)]).PrecisionPorcentaje.Should().Be(esperado);
    }

    [Theory]
    [InlineData(10, 10, true)]
    [InlineData(5, 5, true)]
    [InlineData(9, 10, false)]
    [InlineData(4, 4, false)]   // una partida muy corta no cuenta como "perfecta"
    [InlineData(1, 1, false)]
    public void EsPerfecta_DeberiaExigirTodasLasRondasAcertadasYUnMinimoDeRondas(int aciertos, int rondas, bool esperado)
    {
        RecordsMinijuego.EsPerfecta(Partida(0, aciertos, rondas)).Should().Be(esperado);
    }

    [Fact]
    public void Calcular_DeberiaContarLasPartidasPerfectas()
    {
        var r = RecordsMinijuego.Calcular([Partida(1000, 10, 10), Partida(700, 10, 10), Partida(400, 6, 10), Partida(100, 3, 3)]);

        r.PartidasPerfectas.Should().Be(2);
    }
}

public class MinijuegosRecordsServiceTests
{
    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<ILogrosService> _logros = new();
    private readonly List<PartidaMinijuego> _tabla = new();

    public MinijuegosRecordsServiceTests()
    {
        _db.Setup(d => d.ObtenerPartidasMinijuegoAsync()).ReturnsAsync(() => _tabla.ToList());
        _db.Setup(d => d.GuardarPartidaMinijuegoAsync(It.IsAny<PartidaMinijuego>()))
            .Returns<PartidaMinijuego>(p => { _tabla.Add(p); return Task.CompletedTask; });
        _logros.Setup(l => l.EvaluarAsync(It.IsAny<bool>())).ReturnsAsync(ResumenLogros.Vacio);
    }

    private MinijuegosRecordsService CrearSut(bool conLogros = true) => new(_db.Object, conLogros ? _logros.Object : null);

    private static PartidaMinijuego Partida(int puntos, string juego = JuegosMinijuego.AdivinaAnime) => new()
    {
        JuegoId = juego, FechaUtc = DateTime.UtcNow, Puntos = puntos, Rondas = 10, Aciertos = puntos / 100, RachaMaxima = 2
    };

    [Fact]
    public async Task Registrar_LaPrimeraPartida_NoDeberiaSerRecord()
    {
        var resultado = await CrearSut().RegistrarAsync(Partida(600));

        resultado.EsNuevoRecord.Should().BeFalse("no hay ninguna marca anterior que superar");
        resultado.Records.MejorPuntuacion.Should().Be(600);
        resultado.Records.Partidas.Should().Be(1);
        _tabla.Should().HaveCount(1);
    }

    [Fact]
    public async Task Registrar_UnaPartidaMejor_DeberiaSerRecordEIndicarElAnterior()
    {
        var sut = CrearSut();
        await sut.RegistrarAsync(Partida(400));

        var resultado = await sut.RegistrarAsync(Partida(700));

        resultado.EsNuevoRecord.Should().BeTrue();
        resultado.MejorAnterior.Should().Be(400);
        resultado.Records.MejorPuntuacion.Should().Be(700);
        resultado.Records.Partidas.Should().Be(2);
    }

    [Theory]
    [InlineData(700)]   // empate: no supera
    [InlineData(300)]
    [InlineData(0)]
    public async Task Registrar_UnaPartidaIgualOPeor_NoDeberiaSerRecord(int puntos)
    {
        var sut = CrearSut();
        await sut.RegistrarAsync(Partida(700));

        var resultado = await sut.RegistrarAsync(Partida(puntos));

        resultado.EsNuevoRecord.Should().BeFalse();
        resultado.Records.MejorPuntuacion.Should().Be(700);
    }

    [Fact]
    public async Task Registrar_DeberiaComprobarElRecordSoloContraLasPartidasDelMismoJuego()
    {
        var sut = CrearSut();
        await sut.RegistrarAsync(Partida(900, JuegosMinijuego.AdivinaOpEd));

        var resultado = await sut.RegistrarAsync(Partida(300, JuegosMinijuego.AdivinaAnime));

        resultado.EsNuevoRecord.Should().BeFalse("es la primera partida de Adivina el anime");
        resultado.Records.MejorPuntuacion.Should().Be(300, "el récord de Adivina el anime no se mezcla con el del OP/ED");
        resultado.Records.Partidas.Should().Be(1);
    }

    [Fact]
    public async Task Registrar_DeberiaEvaluarLosLogrosConAvisos()
    {
        await CrearSut().RegistrarAsync(Partida(500));

        _logros.Verify(l => l.EvaluarAsync(true), Times.Once);
    }

    [Fact]
    public async Task Registrar_SiLaEvaluacionDeLogrosFalla_LaPartidaSeGuardaIgualYNoSePropagaElError()
    {
        _logros.Setup(l => l.EvaluarAsync(It.IsAny<bool>())).ThrowsAsync(new InvalidOperationException("db ocupada"));

        var resultado = await CrearSut().RegistrarAsync(Partida(500));

        _tabla.Should().HaveCount(1);
        resultado.Records.Partidas.Should().Be(1);
    }

    [Fact]
    public async Task Registrar_SinServicioDeLogros_FuncionaIgual()
    {
        var resultado = await CrearSut(conLogros: false).RegistrarAsync(Partida(500));

        resultado.Records.Partidas.Should().Be(1);
    }

    [Fact]
    public async Task Obtener_DeberiaCalcularLosRecordsSoloDeEseJuego()
    {
        var sut = CrearSut();
        await sut.RegistrarAsync(Partida(300));
        await sut.RegistrarAsync(Partida(800));
        await sut.RegistrarAsync(Partida(950, JuegosMinijuego.AdivinaOpEd));

        var anime = await sut.ObtenerAsync(JuegosMinijuego.AdivinaAnime);
        var oped = await sut.ObtenerAsync(JuegosMinijuego.AdivinaOpEd);
        var ninguno = await sut.ObtenerAsync("otro");

        anime.MejorPuntuacion.Should().Be(800);
        anime.Partidas.Should().Be(2);
        oped.MejorPuntuacion.Should().Be(950);
        ninguno.HayPartidas.Should().BeFalse();
    }

    [Fact]
    public async Task Obtener_SiLaBaseDevuelveNulo_DeberiaDarRecordsVacios()
    {
        _db.Setup(d => d.ObtenerPartidasMinijuegoAsync()).ReturnsAsync((List<PartidaMinijuego>)null!);

        (await CrearSut().ObtenerAsync(JuegosMinijuego.AdivinaAnime)).Should().Be(RecordsMinijuego.Vacio);
    }
}
