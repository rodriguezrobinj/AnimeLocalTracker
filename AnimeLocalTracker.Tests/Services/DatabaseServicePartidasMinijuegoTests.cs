using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using FluentAssertions;
using SQLite;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>Persistencia de las partidas de minijuegos (migración v11, guardado y borrado total). Usa una base temporal.</summary>
public class DatabaseServicePartidasMinijuegoTests : IDisposable
{
    private static readonly int[] PuntosEsperados = [0, 100, 200];

    private readonly string _rutaDb;
    private readonly DatabaseService _sut;

    public DatabaseServicePartidasMinijuegoTests()
    {
        _rutaDb = Path.Combine(Path.GetTempPath(), $"AnimeTracker_Partidas_{Guid.NewGuid():N}.db");
        _sut = new DatabaseService(_rutaDb);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _sut.Dispose();
        try { if (File.Exists(_rutaDb)) File.Delete(_rutaDb); } catch { /* ignore */ }
    }

    [Fact]
    public async Task Migracion_CreaLaTablaYSuIndiceExplicito()
    {
        await _sut.InicializarBaseDatosAsync();

        using var conexion = new SQLiteConnection(_rutaDb);
        conexion.ExecuteScalar<int>("PRAGMA user_version;").Should().BeGreaterThanOrEqualTo(11);
        conexion.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'PartidaMinijuego';").Should().Be(1);
        conexion.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'IX_PartidaMinijuego_JuegoPuntos';").Should().Be(1);
    }

    [Fact]
    public async Task Guardar_YObtener_ConservaTodosLosCampos()
    {
        await _sut.InicializarBaseDatosAsync();
        var fecha = new DateTime(2026, 9, 26, 18, 45, 0, DateTimeKind.Utc);

        await _sut.GuardarPartidaMinijuegoAsync(new PartidaMinijuego
        {
            JuegoId = "adivina_anime", FechaUtc = fecha, Puntos = 760, Rondas = 10, Aciertos = 9, RachaMaxima = 6
        });

        var guardadas = await _sut.ObtenerPartidasMinijuegoAsync();
        var p = guardadas.Should().ContainSingle().Subject;
        p.Id.Should().BeGreaterThan(0);
        p.JuegoId.Should().Be("adivina_anime");
        p.FechaUtc.Ticks.Should().Be(fecha.Ticks);
        (p.Puntos, p.Rondas, p.Aciertos, p.RachaMaxima).Should().Be((760, 10, 9, 6));
    }

    [Fact]
    public async Task Guardar_VariasPartidas_NoLasMezclaNiLasSobrescribe()
    {
        await _sut.InicializarBaseDatosAsync();

        for (int i = 0; i < 3; i++)
            await _sut.GuardarPartidaMinijuegoAsync(new PartidaMinijuego { JuegoId = "adivina_oped", FechaUtc = DateTime.UtcNow, Puntos = i * 100, Rondas = 10 });

        var guardadas = await _sut.ObtenerPartidasMinijuegoAsync();
        guardadas.Should().HaveCount(3);
        guardadas.Select(p => p.Id).Should().OnlyHaveUniqueItems();
        guardadas.Select(p => p.Puntos).Should().BeEquivalentTo(PuntosEsperados);
    }

    [Fact]
    public async Task Guardar_ConJuegoVacioONulo_SeIgnora()
    {
        await _sut.InicializarBaseDatosAsync();

        await _sut.GuardarPartidaMinijuegoAsync(new PartidaMinijuego { JuegoId = "  ", FechaUtc = DateTime.UtcNow });
        await _sut.GuardarPartidaMinijuegoAsync(null!);

        (await _sut.ObtenerPartidasMinijuegoAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task VaciarBiblioteca_TambienBorraLasPartidasDeMinijuegos()
    {
        await _sut.InicializarBaseDatosAsync();
        await _sut.GuardarPartidaMinijuegoAsync(new PartidaMinijuego { JuegoId = "adivina_anime", FechaUtc = DateTime.UtcNow, Puntos = 500, Rondas = 10 });

        await _sut.VaciarBibliotecaAsync();

        (await _sut.ObtenerPartidasMinijuegoAsync()).Should().BeEmpty("\"Borrar todos mis datos\" incluye los récords");
    }
}
