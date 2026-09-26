using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Services;
using FluentAssertions;
using SQLite;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>DB-01: migración v10 y modelos sin [Indexed]: no debe haber índices duplicados y las consultas deben seguir usando índice.</summary>
public class DatabaseServiceIndicesRedundantesTests : IDisposable
{
    private static readonly string[] Redundantes =
    {
        "DescargaHistorial_FechaUtc", "RegistroEpisodio_UltimaReproduccion", "RegistroEpisodio_AniListId"
    };

    private static readonly string[] Explicitos =
    {
        "IX_RegistroEpisodio_AnimeEp", "IX_RegistroEpisodio_SyncCola",
        "IX_RegistroEpisodio_UltimaReproduccion", "IX_DescargaHistorial_FechaUtc"
    };

    private readonly string _rutaDb = Path.Combine(Path.GetTempPath(), $"AnimeTracker_Indices_{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        foreach (var sufijo in new[] { "", "-wal", "-shm" })
        {
            try { if (File.Exists(_rutaDb + sufijo)) File.Delete(_rutaDb + sufijo); } catch { /* ignore */ }
        }
    }

    private static string[] IndicesDe(SQLiteConnection conexion)
        => conexion.Query<NombreFila>("SELECT name AS Nombre FROM sqlite_master WHERE type='index' AND name NOT LIKE 'sqlite_%'")
            .Select(f => f.Nombre).ToArray();

    private sealed class NombreFila { public string Nombre { get; set; } = ""; }

    [Fact]
    public async Task BaseNueva_NoDeberiaTenerIndicesDuplicadosPeroSiLosExplicitos()
    {
        using (var sut = new DatabaseService(_rutaDb))
        {
            await sut.InicializarBaseDatosAsync();
        }

        using var conexion = new SQLiteConnection(_rutaDb);
        var indices = IndicesDe(conexion);

        indices.Should().NotContain(Redundantes);
        indices.Should().Contain(Explicitos);
        conexion.ExecuteScalar<int>("PRAGMA user_version;").Should().BeGreaterThanOrEqualTo(10);
    }

    [Fact]
    public async Task BaseExistenteEnV9ConIndicesDuplicados_LaMigracionLosElimina()
    {
        // Se reproduce una base creada antes de v10: esquema completo + los índices automáticos + versión 9
        using (var sut = new DatabaseService(_rutaDb))
        {
            await sut.InicializarBaseDatosAsync();
        }

        using (var conexion = new SQLiteConnection(_rutaDb))
        {
            conexion.Execute("CREATE INDEX RegistroEpisodio_AniListId ON RegistroEpisodio(AniListId);");
            conexion.Execute("CREATE INDEX RegistroEpisodio_UltimaReproduccion ON RegistroEpisodio(UltimaReproduccion);");
            conexion.Execute("CREATE INDEX DescargaHistorial_FechaUtc ON DescargaHistorial(FechaUtc);");
            conexion.Execute("PRAGMA user_version = 9;");
            IndicesDe(conexion).Should().Contain(Redundantes);
        }

        using (var sut = new DatabaseService(_rutaDb))
        {
            await sut.InicializarBaseDatosAsync();
        }

        using var despues = new SQLiteConnection(_rutaDb);
        var indices = IndicesDe(despues);
        indices.Should().NotContain(Redundantes);
        indices.Should().Contain("IX_RegistroEpisodio_AnimeEp").And.Contain("IX_DescargaHistorial_FechaUtc");
        despues.ExecuteScalar<int>("PRAGMA user_version;").Should().BeGreaterThanOrEqualTo(10);
    }

    [Fact]
    public async Task ConsultasPorAnimeEHistorial_SiguenUsandoIndiceTrasQuitarLosDuplicados()
    {
        using (var sut = new DatabaseService(_rutaDb))
        {
            await sut.InicializarBaseDatosAsync();
        }

        using var conexion = new SQLiteConnection(_rutaDb);
        string Plan(string sql) => string.Join(" | ", conexion.Query<PlanFila>("EXPLAIN QUERY PLAN " + sql).Select(f => f.detail));

        Plan("SELECT * FROM RegistroEpisodio WHERE AniListId = 5").Should().Contain("IX_RegistroEpisodio_AnimeEp");
        Plan("SELECT * FROM RegistroEpisodio WHERE UltimaReproduccion IS NOT NULL OR ProgresoSegundos > 0 ORDER BY UltimaReproduccion DESC LIMIT 200")
            .Should().Contain("IX_RegistroEpisodio_UltimaReproduccion");
        Plan("SELECT * FROM DescargaHistorial ORDER BY FechaUtc DESC LIMIT 50").Should().Contain("IX_DescargaHistorial_FechaUtc");
    }

    // ReSharper disable once InconsistentNaming
    private sealed class PlanFila { public string detail { get; set; } = ""; }
}
