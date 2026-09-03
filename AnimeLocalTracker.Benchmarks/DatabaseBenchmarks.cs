using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using BenchmarkDotNet.Attributes;

namespace AnimeLocalTracker.Benchmarks;

[MemoryDiagnoser]
public class DatabaseBenchmarks : IDisposable
{
    private string _tempDbPath = null!;
    private DatabaseService _dbService = null!;
    private List<RegistroEpisodio> _registros500 = null!;

    [GlobalSetup]
    public void Setup()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"Bench_DB_{Guid.NewGuid():N}.db");
        _dbService = new DatabaseService(_tempDbPath);
        _dbService.InicializarBaseDatosAsync().GetAwaiter().GetResult();

        _registros500 = Enumerable.Range(1, 500).Select(i => new RegistroEpisodio
        {
            AniListId = (i % 10) + 1,
            NumeroEpisodio = i,
            VistoLocal = true,
            FavoritoLocal = i % 5 == 0,
            RutaArchivo = $"C:\\Anime\\Ep_{i}.mkv"
        }).ToList();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        Dispose();
    }

    // CA1001: el benchmark posee DatabaseService (IDisposable)
    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            _dbService?.Dispose();
            if (_tempDbPath != null && File.Exists(_tempDbPath))
            {
                File.Delete(_tempDbPath);
            }
        }
        catch { }
    }

    [Benchmark(Description = "GuardarRegistrosEpisodioBulkAsync - 500 registros en 1 transacción")]
    public async Task GuardarRegistrosEpisodioBulkBenchmark()
    {
        await _dbService.GuardarRegistrosEpisodioBulkAsync(_registros500);
    }

    [Benchmark(Description = "ObtenerTodosLosRegistrosAsync - Consulta completa")]
    public async Task<List<RegistroEpisodio>> ObtenerTodosLosRegistrosBenchmark()
    {
        return await _dbService.ObtenerTodosLosRegistrosAsync();
    }
}
