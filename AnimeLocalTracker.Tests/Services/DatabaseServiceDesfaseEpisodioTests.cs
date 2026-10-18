using System;
using System.IO;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using FluentAssertions;
using SQLite;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>Migración v26: desfase de subtítulos y audio recordado por capítulo (DesfaseEpisodio).</summary>
public class DatabaseServiceDesfaseEpisodioTests : IDisposable
{
    private readonly string _rutaDb = Path.Combine(Path.GetTempPath(), $"AnimeTracker_Desfase_{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        SQLiteAsyncConnection.ResetPool();
        foreach (var sufijo in new[] { "", "-wal", "-shm" })
        {
            try { if (File.Exists(_rutaDb + sufijo)) File.Delete(_rutaDb + sufijo); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task GuardarYLeer_PorCapitulo()
    {
        using var sut = new DatabaseService(_rutaDb);
        await sut.InicializarBaseDatosAsync();

        await sut.GuardarDesfaseEpisodioAsync(1, 3, 250, -100);
        await sut.GuardarDesfaseEpisodioAsync(1, 3, 300, -100);

        var guardado = await sut.ObtenerDesfaseEpisodioAsync(1, 3);
        guardado!.SubtitulosMs.Should().Be(300);
        guardado.AudioMs.Should().Be(-100);
        (await sut.ObtenerDesfaseEpisodioAsync(1, 4)).Should().BeNull();
    }

    [Fact]
    public async Task GuardarCeroEnLosDos_BorraLaFila()
    {
        using var sut = new DatabaseService(_rutaDb);
        await sut.InicializarBaseDatosAsync();
        await sut.GuardarDesfaseEpisodioAsync(1, 3, 250, 0);

        await sut.GuardarDesfaseEpisodioAsync(1, 3, 0, 0);

        (await sut.ObtenerDesfaseEpisodioAsync(1, 3)).Should().BeNull();
    }

    [Fact]
    public async Task SinAnime_NoGuarda()
    {
        using var sut = new DatabaseService(_rutaDb);
        await sut.InicializarBaseDatosAsync();

        await sut.GuardarDesfaseEpisodioAsync(0, 3, 250, 0);

        (await sut.ObtenerDesfaseEpisodioAsync(0, 3)).Should().BeNull();
    }

    [Fact]
    public async Task VaciarBiblioteca_TambienLaVacia()
    {
        using var sut = new DatabaseService(_rutaDb);
        await sut.InicializarBaseDatosAsync();
        await sut.GuardarDesfaseEpisodioAsync(1, 3, 250, 0);

        await sut.VaciarBibliotecaAsync();

        (await sut.ObtenerDesfaseEpisodioAsync(1, 3)).Should().BeNull();
    }

    [Fact]
    public async Task BaseEnV25SinLaTabla_LaMigracionLaCreaYConservaLosAnimes()
    {
        using (var sut = new DatabaseService(_rutaDb))
        {
            await sut.InicializarBaseDatosAsync();
            await sut.GuardarAnimeAsync(new AnimeItem { AniListId = 1, Titulo = "De antes" });
        }
        SQLiteAsyncConnection.ResetPool();

        using (var vieja = new SQLiteConnection(_rutaDb))
        {
            vieja.Execute("DROP TABLE DesfaseEpisodio;");
            vieja.Execute("PRAGMA user_version = 25;");
        }

        using (var reabierta = new DatabaseService(_rutaDb))
        {
            await reabierta.InicializarBaseDatosAsync();
            (await reabierta.ObtenerAnimePorIdAsync(1))!.Titulo.Should().Be("De antes");
            await reabierta.GuardarDesfaseEpisodioAsync(1, 1, 50, 0);
        }
        SQLiteAsyncConnection.ResetPool();

        // Reabrir otra vez no cambia nada.
        using var otraVez = new DatabaseService(_rutaDb);
        await otraVez.InicializarBaseDatosAsync();
        (await otraVez.ObtenerDesfaseEpisodioAsync(1, 1))!.SubtitulosMs.Should().Be(50);
        using var conexion = new SQLiteConnection(_rutaDb);
        conexion.ExecuteScalar<int>("PRAGMA user_version;").Should().BeGreaterThanOrEqualTo(26);
    }
}
