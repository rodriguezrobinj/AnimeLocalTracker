using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using FluentAssertions;
using SQLite;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>Análisis guardados de OP/ED por episodio (migración v13, reemplazo por episodio y borrado total). Usa una base temporal.</summary>
public class DatabaseServiceSkipTests : IDisposable
{
    private readonly string _rutaDb;
    private readonly DatabaseService _sut;

    public DatabaseServiceSkipTests()
    {
        _rutaDb = Path.Combine(Path.GetTempPath(), $"AnimeTracker_Skip_{Guid.NewGuid():N}.db");
        _sut = new DatabaseService(_rutaDb);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _sut.Dispose();
        try { if (File.Exists(_rutaDb)) File.Delete(_rutaDb); } catch { /* ignore */ }
    }

    private static AnalisisSkipEpisodio Analisis(int anime, int ep, bool completo = true) =>
        new() { AnimeId = anime, Episodio = ep, Firma = "100-200", FechaUtc = new DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc), Completo = completo };

    private static SegmentoSkipGuardado Seg(string tipo, double ini, double fin, string origen = "audio", double conf = 0.9) =>
        new() { Tipo = tipo, Inicio = ini, Fin = fin, Origen = origen, Confianza = conf };

    [Fact]
    public async Task Migracion_CreaLasTablasYSusIndices()
    {
        await _sut.InicializarBaseDatosAsync();

        using var c = new SQLiteConnection(_rutaDb);
        c.ExecuteScalar<int>("PRAGMA user_version;").Should().BeGreaterThanOrEqualTo(13);
        foreach (var nombre in new[] { "AnalisisSkipEpisodio", "SegmentoSkipGuardado", "IX_AnalisisSkip_Episodio", "IX_SegmentoSkip_Episodio" })
            c.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE name = ?;", nombre).Should().Be(1, nombre);
    }

    [Fact]
    public async Task Guardar_YObtener_ConservaAnalisisYTramos()
    {
        await _sut.InicializarBaseDatosAsync();

        await _sut.GuardarAnalisisSkipAsync(Analisis(101, 5), [Seg("op", 90.5, 180.5), Seg("ed", 1300, 1390, "aniskip", 0)]);

        var a = await _sut.ObtenerAnalisisSkipAsync(101, 5);
        a.Should().NotBeNull();
        (a!.Firma, a.Completo).Should().Be(("100-200", true));
        var s = await _sut.ObtenerSegmentosSkipAsync(101, 5);
        s.Should().HaveCount(2);
        var op = s.Single(x => x.Tipo == "op");
        (op.Inicio, op.Fin, op.Origen, op.Confianza).Should().Be((90.5, 180.5, "audio", 0.9));
    }

    [Fact]
    public async Task Obtener_DeUnEpisodioSinAnalisis_DevuelveNuloYVacio()
    {
        await _sut.InicializarBaseDatosAsync();

        (await _sut.ObtenerAnalisisSkipAsync(1, 1)).Should().BeNull();
        (await _sut.ObtenerSegmentosSkipAsync(1, 1)).Should().BeEmpty();
    }

    [Fact]
    public async Task GuardarDeNuevo_ReemplazaElAnalisisYLosTramosDeEseEpisodio_SinTocarLosDeOtros()
    {
        await _sut.InicializarBaseDatosAsync();
        await _sut.GuardarAnalisisSkipAsync(Analisis(101, 5, completo: false), [Seg("op", 10, 100)]);
        await _sut.GuardarAnalisisSkipAsync(Analisis(101, 6), [Seg("op", 20, 110)]);

        await _sut.GuardarAnalisisSkipAsync(Analisis(101, 5), [Seg("op", 90, 180), Seg("ed", 1300, 1390)]);

        (await _sut.ObtenerAnalisisSkipAsync(101, 5))!.Completo.Should().BeTrue();
        (await _sut.ObtenerSegmentosSkipAsync(101, 5)).Select(x => x.Inicio).Should().BeEquivalentTo([90d, 1300d]);
        (await _sut.ObtenerSegmentosSkipAsync(101, 6)).Should().ContainSingle().Which.Inicio.Should().Be(20);

        using var c = new SQLiteConnection(_rutaDb);
        c.ExecuteScalar<int>("SELECT COUNT(*) FROM AnalisisSkipEpisodio WHERE AnimeId = 101 AND Episodio = 5;").Should().Be(1, "el índice único y el reemplazo evitan duplicados");
    }

    [Fact]
    public async Task GuardarSinTramos_GuardaSoloElAnalisis()
    {
        await _sut.InicializarBaseDatosAsync();

        await _sut.GuardarAnalisisSkipAsync(Analisis(101, 5), []);

        (await _sut.ObtenerAnalisisSkipAsync(101, 5)).Should().NotBeNull();
        (await _sut.ObtenerSegmentosSkipAsync(101, 5)).Should().BeEmpty();
    }

    [Fact]
    public async Task Guardar_ConDatosInvalidos_SeIgnora()
    {
        await _sut.InicializarBaseDatosAsync();

        await _sut.GuardarAnalisisSkipAsync(Analisis(0, 5), [Seg("op", 1, 2)]);
        await _sut.GuardarAnalisisSkipAsync(Analisis(101, 0), [Seg("op", 1, 2)]);
        await _sut.GuardarAnalisisSkipAsync(null!, []);

        (await _sut.ObtenerSegmentosSkipAsync(0, 5)).Should().BeEmpty();
        (await _sut.ObtenerSegmentosSkipAsync(101, 0)).Should().BeEmpty();
    }

    [Fact]
    public async Task VaciarBiblioteca_TambienBorraLosAnalisis()
    {
        await _sut.InicializarBaseDatosAsync();
        await _sut.GuardarAnalisisSkipAsync(Analisis(101, 5), [Seg("op", 90, 180)]);

        await _sut.VaciarBibliotecaAsync();

        (await _sut.ObtenerAnalisisSkipAsync(101, 5)).Should().BeNull();
        (await _sut.ObtenerSegmentosSkipAsync(101, 5)).Should().BeEmpty();
    }
}
