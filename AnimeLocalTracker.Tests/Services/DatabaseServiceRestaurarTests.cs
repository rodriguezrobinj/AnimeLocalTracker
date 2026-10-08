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

/// <summary>Restaurar una copia de seguridad: solo copias de esta app, sin perder nada si algo falla y sin romper lo que está en uso.</summary>
public class DatabaseServiceRestaurarTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), $"AnimeTracker_Restaurar_{Guid.NewGuid():N}");
    private readonly string _rutaDb;
    private readonly DatabaseService _sut;

    public DatabaseServiceRestaurarTests()
    {
        Directory.CreateDirectory(_carpeta);
        _rutaDb = Path.Combine(_carpeta, "biblioteca.db");
        _sut = new DatabaseService(_rutaDb);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _sut.Dispose();
        SQLiteAsyncConnection.ResetPool();
        try { Directory.Delete(_carpeta, recursive: true); } catch { /* ignore */ }
    }

    private string[] CopiasPrevias => Directory.Exists(Path.Combine(_carpeta, "Backups"))
        ? Directory.GetFiles(Path.Combine(_carpeta, "Backups"), "biblioteca.antes-de-restaurar.*.db")
        : [];

    /// <summary>La biblioteca en uso: el anime 1 con <paramref name="episodios"/> episodios vistos.</summary>
    private async Task PoblarBibliotecaActualAsync(int episodios = 5)
    {
        await _sut.InicializarBaseDatosAsync();
        await _sut.GuardarAnimeAsync(new AnimeItem { AniListId = 1, Titulo = "Actual" });
        await _sut.GuardarRegistrosEpisodioBulkAsync(
            Enumerable.Range(1, episodios).Select(i => new RegistroEpisodio { AniListId = 1, NumeroEpisodio = i, VistoLocal = true }).ToList());
    }

    /// <summary>Una copia de seguridad como las que exporta la app (snapshot VACUUM INTO) con el anime 2 y tres episodios vistos.</summary>
    private async Task<string> CrearCopiaValidaAsync()
    {
        string origen = Path.Combine(_carpeta, $"origen_{Guid.NewGuid():N}.db");
        string copia = Path.Combine(_carpeta, $"copia_{Guid.NewGuid():N}.db");
        using (var o = new DatabaseService(origen))
        {
            await o.InicializarBaseDatosAsync();
            await o.GuardarAnimeAsync(new AnimeItem { AniListId = 2, Titulo = "De la copia" });
            await o.GuardarRegistrosEpisodioBulkAsync(
                Enumerable.Range(1, 3).Select(i => new RegistroEpisodio { AniListId = 2, NumeroEpisodio = i, VistoLocal = true }).ToList());
            (await o.ExportarCopiaSeguridadAsync(copia)).Should().BeTrue();
        }
        SQLiteAsyncConnection.ResetPool();
        return copia;
    }

    private async Task LaBibliotecaActualDebeEstarIntactaAsync(int episodios = 5)
    {
        (await _sut.ObtenerTodosLosAnimesAsync()).Should().ContainSingle().Which.Titulo.Should().Be("Actual");
        (await _sut.ObtenerTodosLosRegistrosAsync()).Should().HaveCount(episodios).And.OnlyContain(r => r.AniListId == 1 && r.VistoLocal);
    }

    [Fact]
    public async Task UnaBaseQueNoEsDeEstaApp_SeRechazaYLaBibliotecaQuedaIntacta()
    {
        await PoblarBibliotecaActualAsync();
        string ajena = Path.Combine(_carpeta, "otra-app.db");
        using (var c = new SQLiteConnection(ajena))
        {
            c.Execute("CREATE TABLE Canciones (Id INTEGER PRIMARY KEY, Titulo TEXT);");
            c.Execute("INSERT INTO Canciones(Titulo) VALUES ('x');");
        }

        bool ok = await _sut.RestaurarCopiaSeguridadAsync(ajena);

        ok.Should().BeFalse("un SQLite cualquiera no es una copia de la biblioteca y la dejaría vacía");
        await LaBibliotecaActualDebeEstarIntactaAsync();
        CopiasPrevias.Should().BeEmpty("no se llegó a tocar nada");
    }

    [Fact]
    public async Task UnaCopiaDeUnaVersionMasNuevaDeLaApp_SeRechaza()
    {
        await PoblarBibliotecaActualAsync();
        string copia = await CrearCopiaValidaAsync();
        using (var c = new SQLiteConnection(copia)) c.Execute("PRAGMA user_version = 999;");

        bool ok = await _sut.RestaurarCopiaSeguridadAsync(copia);

        ok.Should().BeFalse("tiene columnas y tablas que esta versión no conoce: restaurarla perdería datos");
        await LaBibliotecaActualDebeEstarIntactaAsync();
    }

    [Fact]
    public async Task UnaCopiaValida_ReemplazaLosDatos_ConservaElEsquemaVivoYSigueAceptandoEscrituras()
    {
        await PoblarBibliotecaActualAsync();
        string copia = await CrearCopiaValidaAsync();

        bool ok = await _sut.RestaurarCopiaSeguridadAsync(copia);

        ok.Should().BeTrue();
        (await _sut.ObtenerTodosLosAnimesAsync()).Should().ContainSingle().Which.AniListId.Should().Be(2);
        (await _sut.ObtenerTodosLosRegistrosAsync()).Should().HaveCount(3).And.OnlyContain(r => r.AniListId == 2);
        await _sut.GuardarRegistroEpisodioAsync(new RegistroEpisodio { AniListId = 2, NumeroEpisodio = 4 });
        (await _sut.ObtenerTodosLosRegistrosAsync()).Should().HaveCount(4);

        SQLiteAsyncConnection.ResetPool();
        using var c = new SQLiteConnection(_rutaDb);
        c.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'IX_RegistroEpisodio_AnimeEp' AND sql LIKE 'CREATE UNIQUE%'")
            .Should().Be(1, "el índice único de v22 sigue en la base viva");
        c.ExecuteScalar<int>("PRAGMA user_version;").Should().BeGreaterThanOrEqualTo(22);
    }

    [Fact]
    public async Task TrasRestaurar_QuedaUnaCopiaPreviaParaDeshacer()
    {
        await PoblarBibliotecaActualAsync();
        string copia = await CrearCopiaValidaAsync();

        (await _sut.RestaurarCopiaSeguridadAsync(copia)).Should().BeTrue();

        var previa = CopiasPrevias.Should().ContainSingle("antes de reemplazar la biblioteca se guarda la que había").Subject;
        using var c = new SQLiteConnection(previa, SQLiteOpenFlags.ReadOnly);
        c.ExecuteScalar<int>("SELECT COUNT(*) FROM RegistroEpisodio WHERE AniListId = 1").Should().Be(5);
        c.ExecuteScalar<string>("SELECT Titulo FROM AnimeItem WHERE AniListId = 1").Should().Be("Actual");
    }

    [Fact]
    public async Task GuardarMientrasSeRestaura_NingunGuardadoFalla()
    {
        await PoblarBibliotecaActualAsync();
        string copia = await CrearCopiaValidaAsync();

        // Como el reproductor guardando el progreso cada pocos segundos mientras el usuario restaura una copia.
        int fallos = 0;
        var guardando = Task.Run(async () =>
        {
            for (int i = 0; i < 300; i++)
            {
                try { await _sut.GuardarRegistroEpisodioAsync(new RegistroEpisodio { AniListId = 3, NumeroEpisodio = (i % 20) + 1, ProgresoSegundos = i }); }
                catch (Exception) { fallos++; }
            }
        });
        await Task.Delay(20);

        bool ok = await _sut.RestaurarCopiaSeguridadAsync(copia);
        await guardando;

        ok.Should().BeTrue();
        fallos.Should().Be(0);
    }

    [Fact]
    public async Task UnFalloAMitadDelVolcado_DejaLaBibliotecaIntacta()
    {
        await PoblarBibliotecaActualAsync();
        string copia = await CrearCopiaValidaAsync();
        // Hace fallar la inserción de episodios en la base viva, cuando ya se habría borrado lo anterior.
        using (var c = new SQLiteConnection(_rutaDb))
            c.Execute("CREATE TRIGGER VolcadoRoto BEFORE INSERT ON RegistroEpisodio BEGIN SELECT RAISE(ABORT, 'volcado roto'); END;");

        bool ok = await _sut.RestaurarCopiaSeguridadAsync(copia);

        ok.Should().BeFalse();
        await LaBibliotecaActualDebeEstarIntactaAsync();
    }

    [Fact]
    public async Task UnaCopiaAntiguaSinUnaColumna_SeMigraAntesDeVolcarla()
    {
        await PoblarBibliotecaActualAsync();
        string copia = await CrearCopiaValidaAsync();
        using (var c = new SQLiteConnection(copia))
        {
            c.Execute("ALTER TABLE AnimeItem DROP COLUMN ConservarVideos;");  // como una copia hecha antes de la v21
            c.Execute("PRAGMA user_version = 20;");
        }

        bool ok = await _sut.RestaurarCopiaSeguridadAsync(copia);

        ok.Should().BeTrue();
        (await _sut.ObtenerAnimePorIdAsync(2))!.ConservarVideos.Should().BeFalse();
        (await _sut.ObtenerTodosLosRegistrosAsync()).Should().HaveCount(3);
    }
}
