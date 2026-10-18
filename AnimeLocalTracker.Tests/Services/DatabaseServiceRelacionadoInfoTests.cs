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

/// <summary>Migración v27: datos de AniList de las partes de una saga (orden de visionado de la ficha).</summary>
public class DatabaseServiceRelacionadoInfoTests : IDisposable
{
    private readonly string _rutaDb = Path.Combine(Path.GetTempPath(), $"AnimeTracker_RelInfo_{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        SQLiteAsyncConnection.ResetPool();
        foreach (var sufijo in new[] { "", "-wal", "-shm" })
        {
            try { if (File.Exists(_rutaDb + sufijo)) File.Delete(_rutaDb + sufijo); } catch { /* ignore */ }
        }
    }

    private static AnimeRelacionadoInfo Info(int id, string titulo) =>
        new() { AniListId = id, Titulo = titulo, Formato = "MOVIE", Anio = 2020, Mes = 10, Episodios = 1, UrlPortada = "https://x/y.jpg", FechaUtc = DateTime.UtcNow };

    [Fact]
    public async Task GuardarYLeer_SustituyeLaDelMismoAnime()
    {
        using var sut = new DatabaseService(_rutaDb);
        await sut.InicializarBaseDatosAsync();

        await sut.GuardarRelacionadosInfoAsync(new[] { Info(1, "Viejo"), Info(2, "Otro") });
        await sut.GuardarRelacionadosInfoAsync(new[] { Info(1, "Nuevo") });

        var todas = await sut.ObtenerRelacionadosInfoAsync();
        todas.Should().HaveCount(2);
        todas.Single(i => i.AniListId == 1).Titulo.Should().Be("Nuevo");
        todas.Single(i => i.AniListId == 1).Mes.Should().Be(10);
    }

    [Fact]
    public async Task VaciarBiblioteca_TambienLaVacia()
    {
        using var sut = new DatabaseService(_rutaDb);
        await sut.InicializarBaseDatosAsync();
        await sut.GuardarRelacionadosInfoAsync(new[] { Info(1, "X") });

        await sut.VaciarBibliotecaAsync();

        (await sut.ObtenerRelacionadosInfoAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task BaseEnV26SinLaTabla_LaMigracionLaCreaYConservaLosAnimes()
    {
        using (var sut = new DatabaseService(_rutaDb))
        {
            await sut.InicializarBaseDatosAsync();
            await sut.GuardarAnimeAsync(new AnimeItem { AniListId = 1, Titulo = "De antes" });
        }
        SQLiteAsyncConnection.ResetPool();

        using (var vieja = new SQLiteConnection(_rutaDb))
        {
            vieja.Execute("DROP TABLE AnimeRelacionadoInfo;");
            vieja.Execute("PRAGMA user_version = 26;");
        }

        using (var reabierta = new DatabaseService(_rutaDb))
        {
            await reabierta.InicializarBaseDatosAsync();
            (await reabierta.ObtenerAnimePorIdAsync(1))!.Titulo.Should().Be("De antes");
            await reabierta.GuardarRelacionadosInfoAsync(new[] { Info(5, "Película") });
        }
        SQLiteAsyncConnection.ResetPool();

        // Reabrir otra vez no cambia nada.
        using var otraVez = new DatabaseService(_rutaDb);
        await otraVez.InicializarBaseDatosAsync();
        (await otraVez.ObtenerRelacionadosInfoAsync()).Should().ContainSingle(i => i.AniListId == 5);
        using var conexion = new SQLiteConnection(_rutaDb);
        conexion.ExecuteScalar<int>("PRAGMA user_version;").Should().BeGreaterThanOrEqualTo(27);
    }
}
