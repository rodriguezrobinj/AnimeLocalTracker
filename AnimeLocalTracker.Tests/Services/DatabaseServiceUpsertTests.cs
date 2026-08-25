using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// Semánticas del upsert masivo: merge de campos, no duplicados y defaults de fechas.
/// Complementa los tests de estrés (volumen) de DatabaseServiceStressTests.
/// </summary>
public class DatabaseServiceUpsertTests : IDisposable
{
    private readonly string _tempDbPath;
    private readonly DatabaseService _sut;

    public DatabaseServiceUpsertTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"AnimeTracker_Upsert_{Guid.NewGuid():N}.db");
        _sut = new DatabaseService(_tempDbPath);
    }

    public void Dispose()
    {
        try { if (File.Exists(_tempDbPath)) File.Delete(_tempDbPath); } catch { }
    }

    [Fact]
    public async Task BulkUpsert_ConExistentes_DeberiaActualizarSinDuplicar()
    {
        // Arrange: 10 registros iniciales
        await _sut.InicializarBaseDatosAsync();
        var originales = Enumerable.Range(1, 10).Select(i => new RegistroEpisodio
        {
            AniListId = 100,
            NumeroEpisodio = i,
            VistoLocal = false,
            RutaArchivo = $"C:\\Anime\\Ep{i:00}.mkv"
        }).ToList();
        await _sut.GuardarRegistrosEpisodioBulkAsync(originales);

        // Act: re-guardar los mismos episodios con VistoLocal=true y RutaArchivo vacío
        var actualizados = Enumerable.Range(1, 10).Select(i => new RegistroEpisodio
        {
            AniListId = 100,
            NumeroEpisodio = i,
            VistoLocal = true,
            FavoritoLocal = i == 5,
            RutaArchivo = string.Empty // vacío: debe conservar la ruta original
        }).ToList();
        await _sut.GuardarRegistrosEpisodioBulkAsync(actualizados);

        // Assert: sin duplicados y con merge correcto
        var registros = await _sut.ObtenerRegistrosPorAnimeAsync(100);
        registros.Should().HaveCount(10, "el upsert no debe duplicar filas");
        registros.Should().OnlyContain(r => r.VistoLocal, "todos deben quedar vistos");
        registros.Should().OnlyContain(r => r.RutaArchivo == $"C:\\Anime\\Ep{r.NumeroEpisodio:00}.mkv",
            "un RutaArchivo vacío en el nuevo registro debe conservar el existente");
        registros.Count(r => r.FavoritoLocal).Should().Be(1);
    }

    [Fact]
    public async Task BulkUpsert_MixtoInsertYUpdate_DeberiaAplicarAmbos()
    {
        // Arrange: episodios 1-5 existen
        await _sut.InicializarBaseDatosAsync();
        await _sut.GuardarRegistrosEpisodioBulkAsync(Enumerable.Range(1, 5).Select(i => new RegistroEpisodio
        {
            AniListId = 200,
            NumeroEpisodio = i,
            VistoLocal = false
        }).ToList());

        // Act: 1-5 actualizados (visto) + 6-10 nuevos
        var lote = Enumerable.Range(1, 10).Select(i => new RegistroEpisodio
        {
            AniListId = 200,
            NumeroEpisodio = i,
            VistoLocal = i <= 5
        }).ToList();
        await _sut.GuardarRegistrosEpisodioBulkAsync(lote);

        // Assert
        var registros = await _sut.ObtenerRegistrosPorAnimeAsync(200);
        registros.Should().HaveCount(10);
        registros.Count(r => r.VistoLocal).Should().Be(5);
        registros.Where(r => r.NumeroEpisodio <= 5).Should().OnlyContain(r => r.UltimaReproduccion != null,
            "los actualizados deben recibir UltimaReproduccion");
    }

    [Fact]
    public async Task BulkUpsert_ListaVaciaONula_NoDeberiaLanzar()
    {
        // Arrange
        await _sut.InicializarBaseDatosAsync();

        // Act & Assert
        var act = async () =>
        {
            await _sut.GuardarRegistrosEpisodioBulkAsync(new List<RegistroEpisodio>());
            await _sut.GuardarRegistrosEpisodioBulkAsync(null!);
        };
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task MarcarEpisodiosSincronizadosAsync_DeberiaMarcarSoloLosSolicitados()
    {
        // Arrange: 5 episodios vistos no sincronizados
        await _sut.InicializarBaseDatosAsync();
        await _sut.GuardarRegistrosEpisodioBulkAsync(Enumerable.Range(1, 5).Select(i => new RegistroEpisodio
        {
            AniListId = 300,
            NumeroEpisodio = i,
            VistoLocal = true
        }).ToList());

        var todos = await _sut.ObtenerRegistrosPorAnimeAsync(300);
        var idsPrimerosDos = todos.Where(r => r.NumeroEpisodio <= 2).Select(r => r.Id).ToList();

        // Act
        await _sut.MarcarEpisodiosSincronizadosAsync(idsPrimerosDos);

        // Assert
        var despues = await _sut.ObtenerRegistrosPorAnimeAsync(300);
        despues.Count(r => r.SincronizadoEnNube).Should().Be(2);
        despues.Where(r => r.NumeroEpisodio <= 2).Should().OnlyContain(r => r.SincronizadoEnNube);
        despues.Where(r => r.NumeroEpisodio > 2).Should().OnlyContain(r => !r.SincronizadoEnNube);
    }

    [Fact]
    public async Task InicializarBaseDatosAsync_Concurrente_DeberiaInicializarUnaSolaVez()
    {
        // Arrange & Act: 8 inicializaciones en paralelo sobre la misma instancia
        var tareas = Enumerable.Range(0, 8)
            .Select(_ => _sut.InicializarBaseDatosAsync())
            .ToArray();
        await Task.WhenAll(tareas);

        // Assert: sin excepciones y operativa
        var act = async () => await _sut.ObtenerTodosLosAnimesAsync();
        await act.Should().NotThrowAsync();
    }
}
