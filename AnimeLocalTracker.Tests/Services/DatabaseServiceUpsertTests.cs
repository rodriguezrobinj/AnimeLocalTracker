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
        GC.SuppressFinalize(this);
        try { if (File.Exists(_tempDbPath)) File.Delete(_tempDbPath); } catch { }
    }

    [Fact]
    public async Task InicializarBaseDatosAsync_DeberiaAplicarMigracionesHastaLaUltimaVersion()
    {
        // Act
        await _sut.InicializarBaseDatosAsync();

        // Assert (ARC-06): user_version avanza y el esquema de cada migración existe
        using var conexion = new SQLiteConnection(_tempDbPath);
        conexion.ExecuteScalar<int>("PRAGMA user_version;").Should().BeGreaterThanOrEqualTo(3);
        conexion.ExecuteScalar<string>(
                "SELECT sql FROM sqlite_master WHERE type='index' AND name='IX_RegistroEpisodio_AnimeEp'")
            .Should().NotBeNullOrEmpty("la migración v1 debe crear el índice compuesto");
        conexion.ExecuteScalar<string>(
                "SELECT sql FROM sqlite_master WHERE type='index' AND name='IX_RegistroEpisodio_SyncCola'")
            .Should().NotBeNullOrEmpty("la migración v2 debe crear el índice de la cola de sincronización (PERF-10)");
        conexion.ExecuteScalar<string>(
                "SELECT sql FROM sqlite_master WHERE type='index' AND name='IX_RegistroEpisodio_UltimaReproduccion'")
            .Should().NotBeNullOrEmpty("la migración v3 debe crear el índice del historial");
    }

    [Fact]
    public async Task InicializarBaseDatosAsync_DosVeces_DeberiaSerIdempotente()
    {
        // Act: inicializar dos veces (arranque + test concurrente)
        await _sut.InicializarBaseDatosAsync();
        await _sut.InicializarBaseDatosAsync();

        // Assert: sin errores y sin duplicar esquema
        using var conexion = new SQLiteConnection(_tempDbPath);
        conexion.ExecuteScalar<int>("PRAGMA user_version;").Should().BeGreaterThanOrEqualTo(1);
        conexion.Table<AnimeItem>().Count().Should().Be(0);
    }

    [Fact]
    public async Task ImportarBibliotecaJsonAsync_ConAniListIdDuplicados_DeberiaConservarElUltimoYAvisar()
    {
        // Arrange (FUN-013)
        await _sut.InicializarBaseDatosAsync();
        var rutaJson = Path.Combine(Path.GetTempPath(), $"import_dup_{Guid.NewGuid():N}.json");
        try
        {
            var backup = new DatabaseService.BibliotecaBackup
            {
                Animes = new List<AnimeItem>
                {
                    new() { AniListId = 42, Titulo = "Titulo Viejo" },
                    new() { AniListId = 42, Titulo = "Titulo Nuevo" }
                }
            };
            await File.WriteAllTextAsync(rutaJson, System.Text.Json.JsonSerializer.Serialize(backup));

            // Act
            int importados = await _sut.ImportarBibliotecaJsonAsync(rutaJson);

            // Assert: un solo anime y gana la última entrada del JSON
            importados.Should().Be(1);
            using var conexion = new SQLiteConnection(_tempDbPath);
            var animes = conexion.Table<AnimeItem>().ToList();
            animes.Should().ContainSingle();
            animes[0].Titulo.Should().Be("Titulo Nuevo");
        }
        finally
        {
            try { if (File.Exists(rutaJson)) File.Delete(rutaJson); } catch { }
        }
    }

    [Fact]
    public async Task ImportarBibliotecaJsonAsync_ConJsonInvalido_DeberiaLanzarConMensajeClaro()
    {
        // Arrange (FUN-013)
        await _sut.InicializarBaseDatosAsync();
        var rutaJson = Path.Combine(Path.GetTempPath(), $"import_inv_{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(rutaJson, "{ esto no es json valido");
        try
        {
            // Act & Assert: un JSON malformado no llega como error genérico al ViewModel
            var act = async () => await _sut.ImportarBibliotecaJsonAsync(rutaJson);
            await act.Should().ThrowAsync<InvalidDataException>()
                .WithMessage("*JSON de biblioteca válido*");
        }
        finally
        {
            try { if (File.Exists(rutaJson)) File.Delete(rutaJson); } catch { }
        }
    }

    [Fact]
    public async Task CrearBackupRotativo_DeberiaCrearCopiaValidaYRotar()
    {
        // Arrange
        var backupDir = Path.Combine(Path.GetTempPath(), $"AnimeTracker_Backup_{Guid.NewGuid():N}");
        try
        {
            await _sut.InicializarBaseDatosAsync();
            await _sut.GuardarAnimeAsync(new AnimeItem { AniListId = 1, Titulo = "Test" });

            // Act: dos backups seguidos
            await _sut.CrearBackupRotativoAsync(maxCopias: 3, backupDir);
            await _sut.CrearBackupRotativoAsync(maxCopias: 3, backupDir);

            // Assert: la copia más reciente existe y la rotación empujó la primera
            var copia1 = Path.Combine(backupDir, "biblioteca.backup.1.db");
            var copia2 = Path.Combine(backupDir, "biblioteca.backup.2.db");
            File.Exists(copia1).Should().BeTrue("el backup más reciente debe existir");
            File.Exists(copia2).Should().BeTrue("el backup anterior debe rotar a .2");

            // TST-02/DI-06: la copia debe ser un snapshot íntegro, no un archivo cualquiera
            using (var copia = new SQLiteConnection(copia1))
            {
                copia.ExecuteScalar<string>("PRAGMA integrity_check;").Should().Be("ok");
                copia.Table<AnimeItem>().Count().Should().Be(1, "la copia debe contener las filas de la fuente");
            }
        }
        finally
        {
            try { if (Directory.Exists(backupDir)) Directory.Delete(backupDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ExportarCopiaSeguridadAsync_DeberiaCrearSnapshotValido()
    {
        // Arrange
        await _sut.InicializarBaseDatosAsync();
        await _sut.GuardarAnimeAsync(new AnimeItem { AniListId = 500, Titulo = "Snapshot" });
        var destino = Path.Combine(Path.GetTempPath(), $"AnimeTracker_Export_{Guid.NewGuid():N}.db");
        try
        {
            // Act
            bool ok = await _sut.ExportarCopiaSeguridadAsync(destino);

            // Assert
            ok.Should().BeTrue();
            using var copia = new SQLiteConnection(destino);
            copia.ExecuteScalar<string>("PRAGMA integrity_check;").Should().Be("ok");
            copia.Table<AnimeItem>().Count().Should().Be(1);
        }
        finally
        {
            try { if (File.Exists(destino)) File.Delete(destino); } catch { }
        }
    }

    [Fact]
    public async Task ImportarBibliotecaJsonAsync_ArchivoGigante_DeberiaRechazarse()
    {
        // Arrange (IMP-01): archivo disperso de 51 MB (sin escribir contenido real)
        await _sut.InicializarBaseDatosAsync();
        var gigante = Path.Combine(Path.GetTempPath(), $"AnimeTracker_Huge_{Guid.NewGuid():N}.json");
        using (var fs = new FileStream(gigante, FileMode.CreateNew))
        {
            fs.SetLength(51L * 1024 * 1024);
        }
        try
        {
            // Act & Assert
            var act = async () => await _sut.ImportarBibliotecaJsonAsync(gigante);
            await act.Should().ThrowAsync<InvalidDataException>("un JSON de más de 50 MB debe rechazarse sin leerse");
            (await _sut.ObtenerTodosLosAnimesAsync()).Should().BeEmpty();
        }
        finally
        {
            try { if (File.Exists(gigante)) File.Delete(gigante); } catch { }
        }
    }

    [Fact]
    public async Task ImportarBibliotecaJsonAsync_FilasInvalidas_DeberiaDescartarYConservarValidas()
    {
        // Arrange (IMP-02): mezcla de filas válidas e inválidas
        await _sut.InicializarBaseDatosAsync();
        var backup = new DatabaseService.BibliotecaBackup
        {
            Animes = new List<AnimeItem>
            {
                new() { AniListId = 600, Titulo = "Válido" },
                new() { AniListId = -1, Titulo = "ID inválido" },
                new() { AniListId = 0, Titulo = "ID cero" },
                new() { AniListId = 601, Titulo = new string('X', 501) }
            },
            Registros = new List<RegistroEpisodio>
            {
                new() { AniListId = 600, NumeroEpisodio = 1, VistoLocal = true, SincronizadoEnNube = false },
                new() { AniListId = 600, NumeroEpisodio = 0, VistoLocal = true },
                new() { AniListId = 600, NumeroEpisodio = 2, TotalSegundos = -5 },
                new() { AniListId = 600, NumeroEpisodio = 99999, VistoLocal = true }
            }
        };
        var jsonPath = Path.Combine(Path.GetTempPath(), $"AnimeTracker_Import_{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(jsonPath, System.Text.Json.JsonSerializer.Serialize(backup));
        try
        {
            // Act
            int importados = await _sut.ImportarBibliotecaJsonAsync(jsonPath);

            // Assert: solo la fila válida entra
            importados.Should().Be(1);
            var animes = await _sut.ObtenerTodosLosAnimesAsync();
            animes.Should().ContainSingle(a => a.AniListId == 600);
            animes.Should().NotContain(a => a.AniListId == -1 || a.AniListId == 0 || a.AniListId == 601);

            var registros = await _sut.ObtenerRegistrosPorAnimeAsync(600);
            registros.Should().ContainSingle(r => r.NumeroEpisodio == 1, "solo el registro válido debe importarse");
            registros.Should().OnlyContain(r => r.SincronizadoEnNube, "los importados nunca quedan pendientes de sync");
        }
        finally
        {
            try { if (File.Exists(jsonPath)) File.Delete(jsonPath); } catch { }
        }
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
        registros.Where(r => r.NumeroEpisodio <= 5).Should().OnlyContain(r => r.UltimaReproduccion == null,
            "sin reproducción real el registro conserva fecha NULL (el marcado manual no es historial)");
        registros.Where(r => r.NumeroEpisodio > 5).Should().OnlyContain(r => r.UltimaReproduccion == null,
            "un registro nuevo sin fecha no debe fabricar 'ahora'");
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

    [Fact]
    public async Task ExportarBibliotecaJsonAsync_NoDeberiaIncluirPropiedadesCalculadas()
    {
        // Arrange (IMP-05): propiedades con getters caros (File.Exists, Regex, splits) no deben serializarse
        await _sut.InicializarBaseDatosAsync();
        await _sut.GuardarAnimeAsync(new AnimeItem { AniListId = 700, Titulo = "Export", TotalEpisodios = 12, Sinopsis = "Sinopsis <br> larga" });
        var destino = Path.Combine(Path.GetTempPath(), $"AnimeTracker_Export_{Guid.NewGuid():N}.json");
        try
        {
            // Act
            await _sut.ExportarBibliotecaJsonAsync(destino);
            var json = await File.ReadAllTextAsync(destino);

            // Assert: solo datos persistibles
            json.Should().NotContain("PortadaVisible").And
                .NotContain("ProgresoPorcentaje").And
                .NotContain("GenerosLista").And
                .NotContain("SinopsisLimpia").And
                .NotContain("EstadoVisual").And
                .NotContain("NuevosEpisodios");
            json.Should().Contain("\"Titulo\"").And.Contain("\"AniListId\"");
        }
        finally
        {
            try { if (File.Exists(destino)) File.Delete(destino); } catch { }
        }
    }

    [Fact]
    public async Task ExportarEImportarBiblioteca_RoundTrip_DeberiaConservarDatos()
    {
        // Arrange: biblioteca con 1 anime y 3 registros
        await _sut.InicializarBaseDatosAsync();
        await _sut.GuardarAnimeAsync(new AnimeItem { AniListId = 800, Titulo = "RoundTrip", TotalEpisodios = 12, Generos = "Acción" });
        await _sut.GuardarRegistrosEpisodioBulkAsync(
            Enumerable.Range(1, 3).Select(i => new RegistroEpisodio { AniListId = 800, NumeroEpisodio = i, VistoLocal = true }).ToList());

        var jsonPath = Path.Combine(Path.GetTempPath(), $"AnimeTracker_RoundTrip_{Guid.NewGuid():N}.json");
        var db2Path = Path.Combine(Path.GetTempPath(), $"AnimeTracker_RoundTrip_{Guid.NewGuid():N}.db");
        try
        {
            // Act: exportar e importar en una base nueva
            await _sut.ExportarBibliotecaJsonAsync(jsonPath);
            var sut2 = new DatabaseService(db2Path);
            await sut2.InicializarBaseDatosAsync();
            int importados = await sut2.ImportarBibliotecaJsonAsync(jsonPath);

            // Assert: los datos sobreviven al round-trip
            importados.Should().Be(1);
            (await sut2.ObtenerTodosLosAnimesAsync())
                .Should().ContainSingle(a => a.AniListId == 800 && a.TotalEpisodios == 12 && a.Generos == "Acción");
            (await sut2.ObtenerRegistrosPorAnimeAsync(800)).Should().HaveCount(3);
            (await sut2.ObtenerRegistrosPorAnimeAsync(800)).Should().OnlyContain(r => r.SincronizadoEnNube);
        }
        finally
        {
            try { if (File.Exists(jsonPath)) File.Delete(jsonPath); } catch { }
            try { if (File.Exists(db2Path)) File.Delete(db2Path); } catch { }
        }
    }

    [Fact]
    public async Task RestaurarCopiaSeguridadAsync_CopiaValida_DeberiaReemplazarLaBiblioteca()
    {
        // Arrange: base destino con anime 901; copia de origen con anime 900
        await _sut.InicializarBaseDatosAsync();
        await _sut.GuardarAnimeAsync(new AnimeItem { AniListId = 901, Titulo = "Actual" });

        var copia = Path.Combine(Path.GetTempPath(), $"AnimeTracker_Restore_{Guid.NewGuid():N}.db");
        var sutOrigen = new DatabaseService(copia);
        await sutOrigen.InicializarBaseDatosAsync();
        await sutOrigen.GuardarAnimeAsync(new AnimeItem { AniListId = 900, Titulo = "Copia" });
        // La copia se crea como snapshot VACUUM INTO (caso real: backups exportados),
        // no copiando el archivo WAL crudo de una conexión abierta
        var snapshot = Path.Combine(Path.GetTempPath(), $"AnimeTracker_RestoreSnap_{Guid.NewGuid():N}.db");
        (await sutOrigen.ExportarCopiaSeguridadAsync(snapshot)).Should().BeTrue();
        try
        {
            // Act
            bool ok = await _sut.RestaurarCopiaSeguridadAsync(snapshot);

            // Assert: la biblioteca quedó reemplazada y la conexión sigue operativa
            ok.Should().BeTrue();
            var animes = await _sut.ObtenerTodosLosAnimesAsync();
            animes.Should().ContainSingle(a => a.AniListId == 900 && a.Titulo == "Copia");
            animes.Should().NotContain(a => a.AniListId == 901);

            // La conexión restaurada sigue aceptando escrituras
            await _sut.GuardarAnimeAsync(new AnimeItem { AniListId = 902, Titulo = "PostRestore" });
            (await _sut.ObtenerTodosLosAnimesAsync()).Should().HaveCount(2);
        }
        finally
        {
            try { if (File.Exists(copia)) File.Delete(copia); } catch { }
            try { if (File.Exists(snapshot)) File.Delete(snapshot); } catch { }
        }
    }

    [Fact]
    public async Task RestaurarCopiaSeguridadAsync_ArchivoCorrupto_DeberiaRechazarSinTocarLaBase()
    {
        // Arrange: base con anime 903; archivo de 4 bytes que no es una DB SQLite
        await _sut.InicializarBaseDatosAsync();
        await _sut.GuardarAnimeAsync(new AnimeItem { AniListId = 903, Titulo = "Intacto" });
        var corrupto = Path.Combine(Path.GetTempPath(), $"AnimeTracker_Corrupt_{Guid.NewGuid():N}.db");
        await File.WriteAllBytesAsync(corrupto, new byte[] { 0x1, 0x2, 0x3, 0x4 });
        try
        {
            // Act
            bool ok = await _sut.RestaurarCopiaSeguridadAsync(corrupto);

            // Assert: rechazado y la base actual intacta
            ok.Should().BeFalse();
            var animes = await _sut.ObtenerTodosLosAnimesAsync();
            animes.Should().ContainSingle(a => a.AniListId == 903 && a.Titulo == "Intacto");
        }
        finally
        {
            try { if (File.Exists(corrupto)) File.Delete(corrupto); } catch { }
        }
    }

    [Fact]
    public async Task ImportarBibliotecaJsonAsync_NoDeberiaGenerarPendientesDeSyncNube()
    {
        // Arrange (IMP-04): JSON con un registro visto y SIN sincronizar
        await _sut.InicializarBaseDatosAsync();
        var backup = new DatabaseService.BibliotecaBackup
        {
            Animes = new List<AnimeItem> { new() { AniListId = 400, Titulo = "Importado" } },
            Registros = new List<RegistroEpisodio>
            {
                new() { AniListId = 400, NumeroEpisodio = 1, VistoLocal = true, SincronizadoEnNube = false }
            }
        };
        var jsonPath = Path.Combine(Path.GetTempPath(), $"AnimeTracker_Import_{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(jsonPath, System.Text.Json.JsonSerializer.Serialize(backup));
        try
        {
            // Act
            await _sut.ImportarBibliotecaJsonAsync(jsonPath);

            // Assert: el import NUNCA debe dejar trabajo pendiente de sync a AniList
            var pendientes = await _sut.ObtenerEpisodiosNoSincronizadosAsync();
            pendientes.Should().BeEmpty("el import no debe disparar sync automático a la nube (IMP-04)");
        }
        finally
        {
            try { if (File.Exists(jsonPath)) File.Delete(jsonPath); } catch { }
        }
    }
}
