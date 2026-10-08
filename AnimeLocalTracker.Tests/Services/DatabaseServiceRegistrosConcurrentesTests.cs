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

/// <summary>Un episodio nunca debe acabar con dos filas, aunque lo guarden varias partes de la app a la vez.</summary>
public class DatabaseServiceRegistrosConcurrentesTests : IDisposable
{
    private readonly string _rutaDb = Path.Combine(Path.GetTempPath(), $"AnimeTracker_Concurrentes_{Guid.NewGuid():N}.db");
    private readonly string _rutaJson = Path.Combine(Path.GetTempPath(), $"import_repetidos_{Guid.NewGuid():N}.json");

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

    private int Filas(int aniListId, int episodio)
    {
        using var conexion = new SQLiteConnection(_rutaDb);
        return conexion.ExecuteScalar<int>("SELECT COUNT(*) FROM RegistroEpisodio WHERE AniListId = ? AND NumeroEpisodio = ?", aniListId, episodio);
    }

    [Fact]
    public async Task GuardarRegistro_MuchasVecesAlMismoTiempo_DejaUnaSolaFila()
    {
        using var sut = new DatabaseService(_rutaDb);
        await sut.InicializarBaseDatosAsync();

        // Como el reproductor guardando el progreso mientras se generan las miniaturas y la ficha escanea la carpeta.
        var tareas = Enumerable.Range(0, 60).Select(i => Task.Run(() => sut.GuardarRegistroEpisodioAsync(new RegistroEpisodio
        {
            AniListId = 10, NumeroEpisodio = 5, ProgresoSegundos = i, RutaArchivo = @"C:\Anime\Frieren\Episodio 05.mp4",
        })));
        await Task.WhenAll(tareas);

        Filas(10, 5).Should().Be(1);
    }

    [Fact]
    public async Task GuardarRegistroYFavorito_AlMismoTiempo_DejanUnaSolaFila()
    {
        using var sut = new DatabaseService(_rutaDb);
        await sut.InicializarBaseDatosAsync();

        var tareas = Enumerable.Range(0, 60).Select(i => i % 2 == 0
            ? Task.Run(() => sut.GuardarRegistroEpisodioAsync(new RegistroEpisodio { AniListId = 10, NumeroEpisodio = 6, ProgresoSegundos = i }))
            : Task.Run(() => sut.GuardarFavoritoEpisodioAsync(10, 6, favorito: true, rutaArchivo: @"C:\Anime\Frieren\Episodio 06.mp4")));
        await Task.WhenAll(tareas);

        Filas(10, 6).Should().Be(1);
    }

    [Fact]
    public async Task GuardarEnLote_ConElMismoEpisodioRepetidoEnElLote_LoFusionaEnUnaFila()
    {
        using var sut = new DatabaseService(_rutaDb);
        await sut.InicializarBaseDatosAsync();

        await sut.GuardarRegistrosEpisodioBulkAsync(new List<RegistroEpisodio>
        {
            new() { AniListId = 10, NumeroEpisodio = 7, RutaArchivo = @"C:\Anime\Frieren\Episodio 07.mp4", Resolucion = "1920x1080" },
            new() { AniListId = 10, NumeroEpisodio = 7, VistoLocal = true, ProgresoSegundos = 1200, TotalSegundos = 1400 },
            new() { AniListId = 10, NumeroEpisodio = 8 },
        });

        Filas(10, 7).Should().Be(1);
        var fila = (await sut.ObtenerRegistrosPorAnimeAsync(10)).Single(r => r.NumeroEpisodio == 7);
        fila.VistoLocal.Should().BeTrue();
        fila.ProgresoSegundos.Should().Be(1200);
        fila.RutaArchivo.Should().EndWith("Episodio 07.mp4", "lo que el segundo registro no trae se conserva");
        fila.Resolucion.Should().Be("1920x1080");
        Filas(10, 8).Should().Be(1);
    }

    [Fact]
    public async Task GuardarEnLote_ElOrdenDeLasFilasRepetidasNoHacePerderElVisto()
    {
        using var sut = new DatabaseService(_rutaDb);
        await sut.InicializarBaseDatosAsync();

        // Un respaldo viejo: primero la fila vista y después una solo de miniatura (sin ver, sin progreso).
        await sut.GuardarRegistrosEpisodioBulkAsync(new List<RegistroEpisodio>
        {
            new() { AniListId = 10, NumeroEpisodio = 9, VistoLocal = true, FavoritoLocal = true, ProgresoSegundos = 1200, TotalSegundos = 1400 },
            new() { AniListId = 10, NumeroEpisodio = 9, RutaMiniatura = @"C:\t\ep9.jpg" },
        });

        var fila = (await sut.ObtenerRegistrosPorAnimeAsync(10)).Should().ContainSingle().Subject;
        fila.VistoLocal.Should().BeTrue();
        fila.FavoritoLocal.Should().BeTrue();
        fila.ProgresoSegundos.Should().Be(1200);
        fila.RutaMiniatura.Should().Be(@"C:\t\ep9.jpg");
    }

    [Fact]
    public async Task ImportarUnJsonConElMismoEpisodioRepetido_NoFallaYLoFusiona()
    {
        using var sut = new DatabaseService(_rutaDb);
        await sut.InicializarBaseDatosAsync();
        var backup = new DatabaseService.BibliotecaBackup
        {
            Animes = new List<AnimeItem> { new() { AniListId = 10, Titulo = "Frieren" } },
            Registros = new List<RegistroEpisodio>
            {
                new() { AniListId = 10, NumeroEpisodio = 3, RutaArchivo = @"C:\Anime\Frieren\Episodio 03.mp4" },
                new() { AniListId = 10, NumeroEpisodio = 3, VistoLocal = true },
            },
        };
        await File.WriteAllTextAsync(_rutaJson, System.Text.Json.JsonSerializer.Serialize(backup));

        var importar = async () => await sut.ImportarBibliotecaJsonAsync(_rutaJson);

        await importar.Should().NotThrowAsync();
        Filas(10, 3).Should().Be(1);
        (await sut.ObtenerRegistrosPorAnimeAsync(10)).Single().VistoLocal.Should().BeTrue();
    }
}
