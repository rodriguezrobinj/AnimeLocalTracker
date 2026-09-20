#pragma warning disable CA1861 // Datos constantes de prueba: un arreglo por llamada es lo más legible aquí
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

/// <summary>Persistencia de las relaciones entre animes (migración v6): guardado, reemplazo y limpieza total.</summary>
public class DatabaseServiceRelacionesTests : IDisposable
{
    private readonly string _rutaDb;
    private readonly DatabaseService _sut;

    public DatabaseServiceRelacionesTests()
    {
        _rutaDb = Path.Combine(Path.GetTempPath(), $"AnimeTracker_Relaciones_{Guid.NewGuid():N}.db");
        _sut = new DatabaseService(_rutaDb);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _sut.Dispose();
        try { if (File.Exists(_rutaDb)) File.Delete(_rutaDb); } catch { /* ignore */ }
    }

    private static RelacionAnime Rel(int desde, int hacia, string tipo) => new() { AnimeId = desde, RelacionadoId = hacia, Tipo = tipo };

    [Fact]
    public async Task Migracion_CreaLasTablasYElIndiceUnico()
    {
        await _sut.InicializarBaseDatosAsync();

        using var conexion = new SQLiteConnection(_rutaDb);
        conexion.ExecuteScalar<int>("PRAGMA user_version;").Should().BeGreaterThanOrEqualTo(6);
        conexion.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'RelacionAnime';").Should().Be(1);
        conexion.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'RelacionAnimeSync';").Should().Be(1);
        conexion.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'IX_RelacionAnime_Unica';").Should().Be(1);
    }

    [Fact]
    public async Task Guardar_ConservaLasAristasYMarcaComoSincronizado()
    {
        await _sut.InicializarBaseDatosAsync();

        await _sut.GuardarRelacionesAnimeAsync(new Dictionary<int, List<RelacionAnime>>
        {
            [1] = new() { Rel(1, 2, "SEQUEL"), Rel(1, 3, "SIDE_STORY") }
        });

        var aristas = await _sut.ObtenerRelacionesAnimeAsync();
        aristas.Select(a => (a.AnimeId, a.RelacionadoId, a.Tipo)).Should().BeEquivalentTo(new[] { (1, 2, "SEQUEL"), (1, 3, "SIDE_STORY") });
        (await _sut.ObtenerRelacionesSincronizadasAsync()).Should().ContainSingle(s => s.AnimeId == 1);
    }

    [Fact]
    public async Task Guardar_UnAnimeSinRelaciones_QuedaSincronizadoDeTodosModos()
    {
        await _sut.InicializarBaseDatosAsync();

        await _sut.GuardarRelacionesAnimeAsync(new Dictionary<int, List<RelacionAnime>> { [7] = new() });

        (await _sut.ObtenerRelacionesAnimeAsync()).Should().BeEmpty();
        (await _sut.ObtenerRelacionesSincronizadasAsync()).Should().ContainSingle(s => s.AnimeId == 7 && s.FechaUtc > DateTime.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public async Task Guardar_DeNuevo_ReemplazaLasAristasDelAnime_SinDuplicar()
    {
        await _sut.InicializarBaseDatosAsync();
        await _sut.GuardarRelacionesAnimeAsync(new Dictionary<int, List<RelacionAnime>> { [1] = new() { Rel(1, 2, "SEQUEL"), Rel(1, 3, "SIDE_STORY") } });

        // AniList ahora dice otra cosa (se quitó una relación y se repite otra)
        await _sut.GuardarRelacionesAnimeAsync(new Dictionary<int, List<RelacionAnime>> { [1] = new() { Rel(1, 2, "SEQUEL"), Rel(1, 2, "SEQUEL") } });

        var aristas = await _sut.ObtenerRelacionesAnimeAsync();
        aristas.Should().ContainSingle(a => a.AnimeId == 1 && a.RelacionadoId == 2);
        aristas.Should().NotContain(a => a.RelacionadoId == 3);
    }

    [Fact]
    public async Task Guardar_NoTocaLasAristasDeOtrosAnimes()
    {
        await _sut.InicializarBaseDatosAsync();
        await _sut.GuardarRelacionesAnimeAsync(new Dictionary<int, List<RelacionAnime>> { [1] = new() { Rel(1, 2, "SEQUEL") } });

        await _sut.GuardarRelacionesAnimeAsync(new Dictionary<int, List<RelacionAnime>> { [5] = new() { Rel(5, 6, "SEQUEL") } });

        (await _sut.ObtenerRelacionesAnimeAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task VaciarBiblioteca_TambienBorraLasRelaciones()
    {
        await _sut.InicializarBaseDatosAsync();
        await _sut.GuardarRelacionesAnimeAsync(new Dictionary<int, List<RelacionAnime>> { [1] = new() { Rel(1, 2, "SEQUEL") } });

        await _sut.VaciarBibliotecaAsync();

        (await _sut.ObtenerRelacionesAnimeAsync()).Should().BeEmpty();
        (await _sut.ObtenerRelacionesSincronizadasAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task UnaBaseAntiguaEnV5_MigraALaV6SinPerderDatos()
    {
        using (var vieja = new SQLiteConnection(_rutaDb))
        {
            vieja.CreateTable<AnimeItem>();
            vieja.CreateTable<RegistroEpisodio>();
            vieja.CreateTable<LogroDesbloqueado>();
            vieja.Insert(new AnimeItem { AniListId = 7, Titulo = "Conservado" });
            vieja.Execute("PRAGMA user_version = 5;");
        }

        await _sut.InicializarBaseDatosAsync();

        (await _sut.ObtenerTodosLosAnimesAsync()).Should().ContainSingle(a => a.AniListId == 7);
        (await _sut.ObtenerRelacionesAnimeAsync()).Should().BeEmpty();
    }
}
