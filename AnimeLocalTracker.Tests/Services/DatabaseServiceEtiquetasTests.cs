using System;
using System.IO;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using FluentAssertions;
using SQLite;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>Migración v24: columna Etiquetas (tags de AniList) en AnimeItem.</summary>
public class DatabaseServiceEtiquetasTests : IDisposable
{
    private readonly string _rutaDb = Path.Combine(Path.GetTempPath(), $"AnimeTracker_Etiquetas_{Guid.NewGuid():N}.db");

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
    public async Task BaseNueva_GuardaYLeeLasEtiquetas_YLlegaALaV24()
    {
        using var sut = new DatabaseService(_rutaDb);
        await sut.InicializarBaseDatosAsync();
        await sut.GuardarAnimeAsync(new AnimeItem { AniListId = 9253, Titulo = "Steins;Gate", Etiquetas = """[{"n":"Time Manipulation","r":95}]""" });

        (await sut.ObtenerAnimePorIdAsync(9253))!.Etiquetas.Should().Be("""[{"n":"Time Manipulation","r":95}]""");
        using var conexion = new SQLiteConnection(_rutaDb);
        conexion.ExecuteScalar<int>("PRAGMA user_version;").Should().BeGreaterThanOrEqualTo(24);
    }

    [Fact]
    public async Task BaseEnV23SinLaColumna_LaMigracionLaAgregaYConservaLosAnimes()
    {
        using (var sut = new DatabaseService(_rutaDb))
        {
            await sut.InicializarBaseDatosAsync();
            await sut.GuardarAnimeAsync(new AnimeItem { AniListId = 1, Titulo = "De antes", Generos = "Drama" });
        }
        SQLiteAsyncConnection.ResetPool();

        // Una base creada antes de esta función: sin la columna y en la versión 23.
        using (var vieja = new SQLiteConnection(_rutaDb))
        {
            vieja.Execute("ALTER TABLE AnimeItem DROP COLUMN Etiquetas;");
            vieja.Execute("PRAGMA user_version = 23;");
        }

        using var reabierta = new DatabaseService(_rutaDb);
        await reabierta.InicializarBaseDatosAsync();

        var anime = await reabierta.ObtenerAnimePorIdAsync(1);
        anime!.Titulo.Should().Be("De antes");
        anime.Generos.Should().Be("Drama");
        anime.Etiquetas.Should().BeNullOrEmpty("hasta la próxima actualización con AniList no hay tags");
        using var conexion = new SQLiteConnection(_rutaDb);
        conexion.ExecuteScalar<int>("PRAGMA user_version;").Should().BeGreaterThanOrEqualTo(24);
    }

    private const string Guardadas = """[{"n":"Time Manipulation","r":95}]""";

    private async Task<DatabaseService> BaseConUnAnimeEtiquetado()
    {
        var sut = new DatabaseService(_rutaDb);
        await sut.InicializarBaseDatosAsync();
        await sut.GuardarAnimeAsync(new AnimeItem { AniListId = 1, Titulo = "Steins;Gate", Etiquetas = Guardadas });
        return sut;
    }

    [Fact]
    public async Task ActualizarAnime_ConUnaCopiaSinEtiquetas_NoLasBorra()
    {
        // Las lecturas "ligeras" (Historial, Calendario…) no traen la columna: guardar esa copia no puede borrar los tags.
        using var sut = await BaseConUnAnimeEtiquetado();

        await sut.ActualizarAnimeAsync(new AnimeItem { AniListId = 1, Titulo = "Steins;Gate (otro título)", Etiquetas = null });

        var anime = await sut.ObtenerAnimePorIdAsync(1);
        anime!.Titulo.Should().Be("Steins;Gate (otro título)");
        anime.Etiquetas.Should().Be(Guardadas);
    }

    [Fact]
    public async Task ActualizarAnimesPorLotes_ConCopiasSinEtiquetas_NoLasBorra()
    {
        using var sut = await BaseConUnAnimeEtiquetado();

        await sut.ActualizarAnimesAsync(new[] { new AnimeItem { AniListId = 1, Titulo = "Steins;Gate", Etiquetas = null } });

        (await sut.ObtenerAnimePorIdAsync(1))!.Etiquetas.Should().Be(Guardadas);
    }

    [Fact]
    public async Task GuardarAnime_ReemplazandoSinEtiquetas_NoLasBorra()
    {
        using var sut = await BaseConUnAnimeEtiquetado();

        await sut.GuardarAnimeAsync(new AnimeItem { AniListId = 1, Titulo = "Steins;Gate", Etiquetas = null });

        (await sut.ObtenerAnimePorIdAsync(1))!.Etiquetas.Should().Be(Guardadas);
    }

    [Fact]
    public async Task ActualizarAnime_ConEtiquetasNuevas_LasReemplaza()
    {
        using var sut = await BaseConUnAnimeEtiquetado();
        const string nuevas = """[{"n":"Thriller","r":80}]""";

        await sut.ActualizarAnimeAsync(new AnimeItem { AniListId = 1, Titulo = "Steins;Gate", Etiquetas = nuevas });

        (await sut.ObtenerAnimePorIdAsync(1))!.Etiquetas.Should().Be(nuevas);
    }

    [Fact]
    public async Task ReabrirDosVeces_NoCambiaNada()
    {
        using (var sut = new DatabaseService(_rutaDb))
        {
            await sut.InicializarBaseDatosAsync();
            await sut.GuardarAnimeAsync(new AnimeItem { AniListId = 1, Titulo = "A", Etiquetas = """[{"n":"Thriller","r":80}]""" });
        }
        SQLiteAsyncConnection.ResetPool();
        using (var otra = new DatabaseService(_rutaDb)) await otra.InicializarBaseDatosAsync();
        SQLiteAsyncConnection.ResetPool();

        using var tercera = new DatabaseService(_rutaDb);
        await tercera.InicializarBaseDatosAsync();

        (await tercera.ObtenerAnimePorIdAsync(1))!.Etiquetas.Should().Be("""[{"n":"Thriller","r":80}]""");
    }
}
