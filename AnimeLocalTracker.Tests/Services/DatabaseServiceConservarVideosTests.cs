using System;
using System.Collections.Generic;
using System.IO;
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

            anime.ConservarVideos = true;
            await sut.ActualizarAnimeAsync(anime);
        }

        using var reabierta = new DatabaseService(_rutaDb);
        await reabierta.InicializarBaseDatosAsync();
        (await reabierta.ObtenerAnimePorIdAsync(1))!.ConservarVideos.Should().BeTrue();
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
