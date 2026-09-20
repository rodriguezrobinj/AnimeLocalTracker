using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using FluentAssertions;
using SQLite;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>Persistencia de los niveles de logros (migración v5, sin duplicados, y limpieza total).</summary>
public class DatabaseServiceLogrosTests : IDisposable
{
    private readonly string _rutaDb;
    private readonly DatabaseService _sut;

    public DatabaseServiceLogrosTests()
    {
        _rutaDb = Path.Combine(Path.GetTempPath(), $"AnimeTracker_Logros_{Guid.NewGuid():N}.db");
        _sut = new DatabaseService(_rutaDb);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _sut.Dispose();
        try { if (File.Exists(_rutaDb)) File.Delete(_rutaDb); } catch { /* ignore */ }
    }

    [Fact]
    public async Task Migracion_CreaLaTablaYElIndiceUnico()
    {
        await _sut.InicializarBaseDatosAsync();

        using var conexion = new SQLiteConnection(_rutaDb);
        conexion.ExecuteScalar<int>("PRAGMA user_version;").Should().BeGreaterThanOrEqualTo(5);
        conexion.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'LogroDesbloqueado';").Should().Be(1);
        conexion.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'IX_LogroDesbloqueado_Unico';").Should().Be(1);
    }

    [Fact]
    public async Task Guardar_YObtener_ConservaNivelYFechaIncluidaLaNula()
    {
        await _sut.InicializarBaseDatosAsync();
        var fecha = new DateTime(2026, 9, 12, 15, 30, 0, DateTimeKind.Utc);

        await _sut.GuardarLogrosDesbloqueadosAsync(new[]
        {
            new LogroDesbloqueado { LogroId = "horas", Nivel = 2, FechaUtc = fecha },
            new LogroDesbloqueado { LogroId = "racha", Nivel = 1, FechaUtc = null }
        });

        var guardados = await _sut.ObtenerLogrosDesbloqueadosAsync();
        guardados.Should().HaveCount(2);
        guardados.Single(l => l.LogroId == "horas").Nivel.Should().Be(2);
        guardados.Single(l => l.LogroId == "horas").FechaUtc!.Value.Ticks.Should().Be(fecha.Ticks);
        guardados.Single(l => l.LogroId == "racha").FechaUtc.Should().BeNull();
    }

    [Fact]
    public async Task Guardar_IgnoraDuplicadosDelMismoNivel_SinLanzar()
    {
        await _sut.InicializarBaseDatosAsync();
        var original = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        await _sut.GuardarLogrosDesbloqueadosAsync(new[] { new LogroDesbloqueado { LogroId = "horas", Nivel = 1, FechaUtc = original } });

        var act = async () => await _sut.GuardarLogrosDesbloqueadosAsync(new[]
        {
            new LogroDesbloqueado { LogroId = "horas", Nivel = 1, FechaUtc = DateTime.UtcNow },   // duplicado
            new LogroDesbloqueado { LogroId = "horas", Nivel = 2, FechaUtc = DateTime.UtcNow }    // nuevo
        });

        await act.Should().NotThrowAsync();
        var guardados = await _sut.ObtenerLogrosDesbloqueadosAsync();
        guardados.Should().HaveCount(2);
        guardados.Single(l => l.Nivel == 1).FechaUtc!.Value.Ticks.Should().Be(original.Ticks, "el primer desbloqueo conserva su fecha");
    }

    [Fact]
    public async Task Guardar_ListaVacia_NoHaceNada()
    {
        await _sut.InicializarBaseDatosAsync();

        await _sut.GuardarLogrosDesbloqueadosAsync(Array.Empty<LogroDesbloqueado>());

        (await _sut.ObtenerLogrosDesbloqueadosAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task VaciarBiblioteca_TambienBorraLosLogros()
    {
        await _sut.InicializarBaseDatosAsync();
        await _sut.GuardarLogrosDesbloqueadosAsync(new[] { new LogroDesbloqueado { LogroId = "horas", Nivel = 1, FechaUtc = DateTime.UtcNow } });

        await _sut.VaciarBibliotecaAsync();

        (await _sut.ObtenerLogrosDesbloqueadosAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task UnaBaseAntiguaEnV4_MigraALaV5SinPerderDatos()
    {
        // Simula una base creada antes de existir los logros: esquema base + user_version = 4
        using (var vieja = new SQLiteConnection(_rutaDb))
        {
            vieja.CreateTable<AnimeItem>();
            vieja.CreateTable<RegistroEpisodio>();
            vieja.Insert(new AnimeItem { AniListId = 7, Titulo = "Conservado" });
            vieja.Execute("PRAGMA user_version = 4;");
        }

        await _sut.InicializarBaseDatosAsync();

        (await _sut.ObtenerTodosLosAnimesAsync()).Should().ContainSingle(a => a.AniListId == 7);
        (await _sut.ObtenerLogrosDesbloqueadosAsync()).Should().BeEmpty();
        using var conexion = new SQLiteConnection(_rutaDb);
        conexion.ExecuteScalar<int>("PRAGMA user_version;").Should().BeGreaterThanOrEqualTo(5);
    }
}
