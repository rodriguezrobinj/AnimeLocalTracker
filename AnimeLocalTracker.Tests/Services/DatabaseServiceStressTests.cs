using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

public class DatabaseServiceStressTests : IDisposable
{
    private readonly string _tempDbPath;
    private readonly DatabaseService _sut;

    public DatabaseServiceStressTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"AnimeTracker_Test_{Guid.NewGuid():N}.db");
        _sut = new DatabaseService(_tempDbPath);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            if (File.Exists(_tempDbPath))
            {
                File.Delete(_tempDbPath);
            }
        }
        catch { }
    }

    [Fact]
    public async Task GuardarRegistrosEpisodioBulkAsync_DeberiaInsertar5000RegistrosRapidamente()
    {
        // Arrange
        await _sut.InicializarBaseDatosAsync();

        var registros = new List<RegistroEpisodio>(5000);
        for (int animeId = 1; animeId <= 50; animeId++)
        {
            for (int ep = 1; ep <= 100; ep++)
            {
                registros.Add(new RegistroEpisodio
                {
                    AniListId = animeId,
                    NumeroEpisodio = ep,
                    VistoLocal = ep <= 50,
                    FavoritoLocal = ep % 10 == 0,
                    RutaArchivo = $"C:\\Anime\\Anime_{animeId}\\Episode_{ep}.mkv"
                });
            }
        }

        // Act
        var sw = Stopwatch.StartNew();
        await _sut.GuardarRegistrosEpisodioBulkAsync(registros);
        sw.Stop();

        // Assert
        sw.ElapsedMilliseconds.Should().BeLessThan(5000); // 5000 registros en menos de 5s bajo concurrencia de tests
        
        var guardados = await _sut.ObtenerTodosLosRegistrosAsync();
        guardados.Should().HaveCount(5000);

        var epAnime10 = await _sut.ObtenerRegistrosPorAnimeAsync(10);
        epAnime10.Should().HaveCount(100);
        epAnime10.Count(r => r.VistoLocal).Should().Be(50);
    }

    [Fact]
    public async Task GuardarAnimeAsync_DeberiaInsertarYRecuperar1000Animes()
    {
        // Arrange
        await _sut.InicializarBaseDatosAsync();

        var animes = Enumerable.Range(1, 1000).Select(i => new AnimeItem
        {
            AniListId = i,
            Titulo = $"Anime Test Series #{i}",
            TotalEpisodios = 24,
            Estado = "FINISHED",
            EstadoUsuario = i % 2 == 0 ? "COMPLETED" : "CURRENT",
            RutaCarpeta = $"C:\\Media\\Anime_{i}"
        }).ToList();

        // Act
        foreach (var anime in animes)
        {
            await _sut.GuardarAnimeAsync(anime);
        }

        var recuperados = await _sut.ObtenerTodosLosAnimesAsync();

        // Assert
        recuperados.Should().HaveCount(1000);
        recuperados.FirstOrDefault(a => a.AniListId == 500)!.Titulo.Should().Be("Anime Test Series #500");
    }

    [Fact]
    public async Task Concurrencia_50ConsultasParalelas_NoDeberianBloquearse()
    {
        // Arrange
        await _sut.InicializarBaseDatosAsync();
        await _sut.GuardarAnimeAsync(new AnimeItem { AniListId = 1, Titulo = "Concurrencia Base", TotalEpisodios = 12 });

        // Act: Lanzar 50 tareas concurrentes de lectura y escritura
        var tasks = Enumerable.Range(1, 50).Select(async i =>
        {
            if (i % 2 == 0)
            {
                await _sut.GuardarRegistroEpisodioAsync(new RegistroEpisodio
                {
                    AniListId = 1,
                    NumeroEpisodio = i,
                    VistoLocal = true,
                    RutaArchivo = $"C:\\Ep_{i}.mkv"
                });
            }
            else
            {
                var animes = await _sut.ObtenerTodosLosAnimesAsync();
                animes.Should().NotBeNull();
            }
        });

        // Assert: Todas las tareas deben completarse sin excepciones de base de datos bloqueada
        var act = async () => await Task.WhenAll(tasks);
        await act.Should().NotThrowAsync();

        var registros = await _sut.ObtenerRegistrosPorAnimeAsync(1);
        registros.Should().HaveCount(25);
    }
}
