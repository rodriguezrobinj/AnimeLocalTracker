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

/// <summary>Consultas pensadas para la Galería: contar en la base de datos en vez de traer todas las filas. Usa una base temporal.</summary>
public class DatabaseServiceLecturasGaleriaTests : IDisposable
{
    private readonly string _rutaDb = Path.Combine(Path.GetTempPath(), $"AnimeTracker_Galeria_{Guid.NewGuid():N}.db");
    private readonly DatabaseService _sut;

    public DatabaseServiceLecturasGaleriaTests() => _sut = new DatabaseService(_rutaDb);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _sut.Dispose();
        try { if (File.Exists(_rutaDb)) File.Delete(_rutaDb); } catch { /* ignore */ }
    }

    [Fact]
    public async Task EpisodiosVistosPorAnime_CuentaSoloLosVistos()
    {
        await _sut.InicializarBaseDatosAsync();
        await _sut.GuardarRegistrosEpisodioBulkAsync(new List<RegistroEpisodio>
        {
            new() { AniListId = 1, NumeroEpisodio = 1, VistoLocal = true },
            new() { AniListId = 1, NumeroEpisodio = 2, VistoLocal = true },
            new() { AniListId = 1, NumeroEpisodio = 3, VistoLocal = false },
            new() { AniListId = 2, NumeroEpisodio = 1, VistoLocal = false },
            new() { AniListId = 3, NumeroEpisodio = 7, VistoLocal = true }
        });

        var vistos = await _sut.ObtenerEpisodiosVistosPorAnimeAsync();

        vistos.Should().BeEquivalentTo(new Dictionary<int, int> { [1] = 2, [3] = 1 },
            "un anime sin ningún episodio visto no aparece (cuenta como 0)");
    }

    [Fact]
    public async Task FechaDeAlta_SeGuardaConElAnime_YLosDeAntesQuedanSinFecha()
    {
        await _sut.InicializarBaseDatosAsync();
        var fecha = new DateTime(2026, 10, 2, 18, 30, 0, DateTimeKind.Utc);
        await _sut.GuardarAnimeAsync(new AnimeItem { AniListId = 1, Titulo = "Nuevo", FechaAgregadoUtc = fecha });
        await _sut.GuardarAnimeAsync(new AnimeItem { AniListId = 2, Titulo = "De antes" });

        var animes = await _sut.ObtenerTodosLosAnimesAsync();

        animes.Single(a => a.AniListId == 1).FechaAgregadoUtc!.Value.Ticks.Should().Be(fecha.Ticks);
        animes.Single(a => a.AniListId == 2).FechaAgregadoUtc.Should().BeNull();
    }

    [Fact]
    public async Task GuardarProximasEmisiones_GuardaTodasYReemplazaLasQueYaEstaban()
    {
        await _sut.InicializarBaseDatosAsync();
        await _sut.GuardarProximaEmisionAsync(new ProximaEmisionLocal { AniListId = 1, Episodio = 4 });

        await _sut.GuardarProximasEmisionesAsync(new[]
        {
            new ProximaEmisionLocal { AniListId = 1, Episodio = 5 },
            new ProximaEmisionLocal { AniListId = 2, Episodio = 12 }
        });

        var guardadas = await _sut.ObtenerProximasEmisionesAsync();
        guardadas.Select(p => (p.AniListId, p.Episodio)).Should().BeEquivalentTo(new[] { (1, 5), (2, 12) });
    }
}
