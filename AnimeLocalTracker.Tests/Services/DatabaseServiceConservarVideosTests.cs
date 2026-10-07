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

/// <summary>"Conservar los videos": se guarda con el anime, llega a bases ya existentes y la importación no la quita.</summary>
public class DatabaseServiceConservarVideosTests : IDisposable
{
    private readonly string _rutaDb = Path.Combine(Path.GetTempPath(), $"AnimeTracker_ConservarVideos_{Guid.NewGuid():N}.db");
    private readonly string _rutaJson = Path.Combine(Path.GetTempPath(), $"import_conservar_{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        SQLiteAsyncConnection.ResetPool();
        foreach (var sufijo in new[] { "", "-wal", "-shm" })
        {
            try { if (File.Exists(_rutaDb + sufijo)) File.Delete(_rutaDb + sufijo); } catch { /* ignore */ }
        }
        try { if (File.Exists(_rutaJson)) File.Delete(_rutaJson); } catch { /* ignore */ }
    }

    [Fact]
    public async Task SeGuardaConElAnime_YSobreviveAReabrirLaBase()
    {
        using (var sut = new DatabaseService(_rutaDb))
        {
            await sut.InicializarBaseDatosAsync();
            await sut.GuardarAnimeAsync(new AnimeItem { AniListId = 1, Titulo = "Frieren" });
            var anime = (await sut.ObtenerAnimePorIdAsync(1))!;
            anime.ConservarVideos.Should().BeFalse("por defecto ningún anime está protegido");

            await sut.GuardarConservarVideosAsync(1, true);
        }

        using var reabierta = new DatabaseService(_rutaDb);
        await reabierta.InicializarBaseDatosAsync();
        (await reabierta.ObtenerAnimePorIdAsync(1))!.ConservarVideos.Should().BeTrue();
    }

    [Fact]
    public async Task GuardarYObtener_SoloCambianEsaColumna()
    {
        using var sut = new DatabaseService(_rutaDb);
        await sut.InicializarBaseDatosAsync();
        await sut.GuardarAnimeAsync(new AnimeItem { AniListId = 1, Titulo = "Frieren", EsFavorito = true });

        (await sut.ObtenerConservarVideosAsync(1)).Should().BeFalse();
        await sut.GuardarConservarVideosAsync(1, true);

        (await sut.ObtenerConservarVideosAsync(1)).Should().BeTrue();
        var anime = (await sut.ObtenerAnimePorIdAsync(1))!;
        anime.Titulo.Should().Be("Frieren");
        anime.EsFavorito.Should().BeTrue("solo cambia esa columna");
        (await sut.ObtenerConservarVideosAsync(99)).Should().BeFalse("un anime que no existe no está protegido");
    }

    // Una pantalla con una copia vieja del anime (la Galería guarda la fila entera, también en el refresco automático de AniList)
    // no puede apagar la protección: la única forma de cambiarla es GuardarConservarVideosAsync.

    [Fact]
    public async Task ActualizarAnime_ConUnaCopiaVieja_NoApagaLaProteccion()
    {
        using var sut = new DatabaseService(_rutaDb);
        await sut.InicializarBaseDatosAsync();
        await sut.GuardarAnimeAsync(new AnimeItem { AniListId = 1, Titulo = "Frieren" });
        var copiaVieja = (await sut.ObtenerAnimePorIdAsync(1))!;
        await sut.GuardarConservarVideosAsync(1, true);

        copiaVieja.EsFavorito = true;
        await sut.ActualizarAnimeAsync(copiaVieja);

        (await sut.ObtenerConservarVideosAsync(1)).Should().BeTrue("una copia vieja no puede apagar la protección");
        copiaVieja.ConservarVideos.Should().BeTrue("la copia en memoria se pone al día");
        (await sut.ObtenerAnimePorIdAsync(1))!.EsFavorito.Should().BeTrue("el resto de cambios sí se guardan");
    }

    [Fact]
    public async Task ActualizarAnimes_ConCopiasViejas_NoApagaLaProteccion()
    {
        using var sut = new DatabaseService(_rutaDb);
        await sut.InicializarBaseDatosAsync();
        await sut.GuardarAnimeAsync(new AnimeItem { AniListId = 1, Titulo = "Protegido" });
        await sut.GuardarAnimeAsync(new AnimeItem { AniListId = 2, Titulo = "Normal" });
        var copias = await sut.ObtenerTodosLosAnimesAsync();
        await sut.GuardarConservarVideosAsync(1, true);

        foreach (var copia in copias) copia.EsFavorito = true;
        await sut.ActualizarAnimesAsync(copias);

        (await sut.ObtenerConservarVideosAsync(1)).Should().BeTrue();
        (await sut.ObtenerConservarVideosAsync(2)).Should().BeFalse("el otro anime no se protege por error");
        copias.Single(a => a.AniListId == 1).ConservarVideos.Should().BeTrue();
        (await sut.ObtenerAnimePorIdAsync(2))!.EsFavorito.Should().BeTrue("el resto de cambios sí se guardan");
    }

    [Fact]
    public async Task GuardarAnime_SobreUnoQueYaExisteProtegido_NoApagaLaProteccion()
    {
        using var sut = new DatabaseService(_rutaDb);
        await sut.InicializarBaseDatosAsync();
        await sut.GuardarAnimeAsync(new AnimeItem { AniListId = 1, Titulo = "Frieren" });
        await sut.GuardarConservarVideosAsync(1, true);

        await sut.GuardarAnimeAsync(new AnimeItem { AniListId = 1, Titulo = "Frieren (de nuevo)" }); // InsertOrReplace

        (await sut.ObtenerConservarVideosAsync(1)).Should().BeTrue();
        (await sut.ObtenerAnimePorIdAsync(1))!.Titulo.Should().Be("Frieren (de nuevo)");
    }

    [Fact]
    public async Task BaseEnV20SinLaColumna_LaMigracionLaAgregaYLosAnimesQuedanSinProteger()
    {
        using (var sut = new DatabaseService(_rutaDb))
        {
            await sut.InicializarBaseDatosAsync();
            await sut.GuardarAnimeAsync(new AnimeItem { AniListId = 1, Titulo = "De antes" });
        }
        SQLiteAsyncConnection.ResetPool();

        // Se reproduce una base creada antes de esta función: sin la columna y en la versión 20.
        using (var vieja = new SQLiteConnection(_rutaDb))
        {
            vieja.Execute("ALTER TABLE AnimeItem DROP COLUMN ConservarVideos;");
            vieja.Execute("PRAGMA user_version = 20;");
        }

        using var reabierta = new DatabaseService(_rutaDb);
        await reabierta.InicializarBaseDatosAsync();

        (await reabierta.ObtenerAnimePorIdAsync(1))!.ConservarVideos.Should().BeFalse();
        using var conexion = new SQLiteConnection(_rutaDb);
        conexion.ExecuteScalar<int>("PRAGMA user_version;").Should().BeGreaterThanOrEqualTo(21);
    }

    [Fact]
    public async Task ImportarUnJsonSinLaMarca_NoQuitaLaProteccionDeUnAnimeQueYaTenia()
    {
        using var sut = new DatabaseService(_rutaDb);
        await sut.InicializarBaseDatosAsync();
        await sut.GuardarAnimeAsync(new AnimeItem { AniListId = 7, Titulo = "Protegido", ConservarVideos = true });
        await sut.GuardarAnimeAsync(new AnimeItem { AniListId = 8, Titulo = "Normal" });

        // Una exportación hecha antes de existir la función (o por otra persona) trae ConservarVideos = false.
        var backup = new DatabaseService.BibliotecaBackup
        {
            Animes = new List<AnimeItem>
            {
                new() { AniListId = 7, Titulo = "Protegido (del archivo)" },
                new() { AniListId = 8, Titulo = "Normal (del archivo)" },
                new() { AniListId = 9, Titulo = "Nuevo", ConservarVideos = true },
            }
        };
        await File.WriteAllTextAsync(_rutaJson, System.Text.Json.JsonSerializer.Serialize(backup));

        await sut.ImportarBibliotecaJsonAsync(_rutaJson);

        (await sut.ObtenerAnimePorIdAsync(7))!.ConservarVideos.Should().BeTrue("importar no debe quitar una protección que ya existía");
        (await sut.ObtenerAnimePorIdAsync(7))!.Titulo.Should().Be("Protegido (del archivo)", "el resto de campos sí se fusiona como siempre");
        (await sut.ObtenerAnimePorIdAsync(8))!.ConservarVideos.Should().BeFalse();
        (await sut.ObtenerAnimePorIdAsync(9))!.ConservarVideos.Should().BeTrue("la marca de un anime nuevo del archivo se respeta");
    }
}
