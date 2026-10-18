using System;
using System.IO;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using FluentAssertions;
using SQLite;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>Migración v25: lo último enviado a MyAnimeList por anime (SincronizacionMal).</summary>
public class DatabaseServiceSincronizacionMalTests : IDisposable
{
    private readonly string _rutaDb = Path.Combine(Path.GetTempPath(), $"AnimeTracker_Mal_{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        SQLiteAsyncConnection.ResetPool();
        foreach (var sufijo in new[] { "", "-wal", "-shm" })
        {
            try { if (File.Exists(_rutaDb + sufijo)) File.Delete(_rutaDb + sufijo); } catch { /* ignore */ }
        }
    }

    private static SincronizacionMal Enviado(int id, int progreso)
        => new() { AniListId = id, MalId = id + 1000, Estado = "watching", Progreso = progreso, Nota = 8, EnviadoUtc = DateTime.UtcNow };

    [Fact]
    public async Task GuardarYLeer_SustituyeLoAnteriorDelMismoAnime()
    {
        using var sut = new DatabaseService(_rutaDb);
        await sut.InicializarBaseDatosAsync();

        await sut.GuardarSincronizacionMalAsync(Enviado(1, 3));
        await sut.GuardarSincronizacionMalAsync(Enviado(1, 5));
        await sut.GuardarSincronizacionMalAsync(Enviado(2, 1));

        var todas = await sut.ObtenerSincronizacionesMalAsync();
        todas.Should().HaveCount(2);
        todas.Should().ContainSingle(s => s.AniListId == 1).Which.Progreso.Should().Be(5);
    }

    [Fact]
    public async Task Borrar_LaDejaVacia()
    {
        using var sut = new DatabaseService(_rutaDb);
        await sut.InicializarBaseDatosAsync();
        await sut.GuardarSincronizacionMalAsync(Enviado(1, 3));

        await sut.BorrarSincronizacionesMalAsync();

        (await sut.ObtenerSincronizacionesMalAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task VaciarBiblioteca_TambienLaVacia()
    {
        using var sut = new DatabaseService(_rutaDb);
        await sut.InicializarBaseDatosAsync();
        await sut.GuardarSincronizacionMalAsync(Enviado(1, 3));

        await sut.VaciarBibliotecaAsync();

        (await sut.ObtenerSincronizacionesMalAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ObtenerSeguimientosLocales_DevuelveTodos()
    {
        using var sut = new DatabaseService(_rutaDb);
        await sut.InicializarBaseDatosAsync();
        await sut.GuardarSeguimientoLocalAsync(new SeguimientoLocal { AniListId = 1, Estado = "CURRENT", Progreso = 3 });
        await sut.GuardarSeguimientoLocalAsync(new SeguimientoLocal { AniListId = 2, Estado = "COMPLETED", Progreso = 12, Pendiente = true });

        (await sut.ObtenerSeguimientosLocalesAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task BaseEnV24SinLaTabla_LaMigracionLaCreaYConservaLosAnimes()
    {
        using (var sut = new DatabaseService(_rutaDb))
        {
            await sut.InicializarBaseDatosAsync();
            await sut.GuardarAnimeAsync(new AnimeItem { AniListId = 1, Titulo = "De antes" });
        }
        SQLiteAsyncConnection.ResetPool();

        using (var vieja = new SQLiteConnection(_rutaDb))
        {
            vieja.Execute("DROP TABLE SincronizacionMal;");
            vieja.Execute("PRAGMA user_version = 24;");
        }

        using var reabierta = new DatabaseService(_rutaDb);
        await reabierta.InicializarBaseDatosAsync();

        (await reabierta.ObtenerAnimePorIdAsync(1))!.Titulo.Should().Be("De antes");
        (await reabierta.ObtenerSincronizacionesMalAsync()).Should().BeEmpty();
        using var conexion = new SQLiteConnection(_rutaDb);
        conexion.ExecuteScalar<int>("PRAGMA user_version;").Should().BeGreaterThanOrEqualTo(25);
    }
}
