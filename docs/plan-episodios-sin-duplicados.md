# Episodios sin filas duplicadas: plan de implementación

> **Para quien ejecute el plan:** ejecutar en la sesión con `superpowers:executing-plans` (el repo prohíbe lanzar subagentes por iniciativa propia). Los pasos usan casillas `- [ ]`. **Sin commit**: la regla del repo dice que no se hace commit ni push sin orden del usuario, así que cada tarea termina en verificación, no en commit.

**Objetivo:** que un episodio nunca pueda tener dos filas en `RegistroEpisodio`: cerrar la carrera que las crea, unir las 39 que ya hay en la base del usuario sin perder nada, y dejar un índice único que lo garantice.

**Arquitectura:** (1) el guardado de un episodio pasa a ser **una sola operación** sobre la conexión (buscar + insertar/actualizar dentro de `RunInTransactionAsync`), que es lo que hace ya el guardado por lotes; (2) la migración v22 hace una copia de seguridad, une las filas repetidas con reglas fijas y convierte `IX_RegistroEpisodio_AnimeEp` en índice único; (3) el guardado por lotes fusiona también las repeticiones dentro del propio lote, para que el índice único no rompa la importación de un JSON con episodios repetidos.

**Tecnología:** C# 12 / .NET 8, sqlite-net, xUnit + FluentAssertions.

**Diseño:** análisis de la base real (solo lectura, 2026-10-07): 3507 registros, **39 episodios con exactamente 2 filas**, creadas con Ids a 1–12 de distancia (una ráfaga de guardados). En 11 de ellos las dos filas **se contradicen** (una vista y otra no, o con distinto estado de sincronización); en 16 solo difiere la resolución y 10 son idénticas. La causa: `GuardarRegistroEpisodioAsync` y `GuardarFavoritoEpisodioAsync` hacen un SELECT y luego un INSERT como dos operaciones separadas, y entre ambas puede colarse otra petición (reproductor, enriquecimiento de miniaturas, ficha).

## Restricciones globales

- 0 advertencias: `TreatWarningsAsErrors` está activo (`Directory.Build.props`).
- El esquema evoluciona con la lista `Migraciones` de `DatabaseService` (versión + acción), nunca con `CreateTableAsync` suelto. Los `[Indexed]` del modelo solo valen para bases nuevas: el índice único va en la migración.
- **Historial = visionado real** (regla de persistencia): la unión de filas no puede borrar que se vio un episodio ni inventar una fecha de reproducción (`UltimaReproduccion` solo se toma de las filas, nunca se fabrica).
- Fechas en UTC tal como están guardadas; no se reinterpretan.
- Las pruebas nunca escriben ni borran bajo `AppDataPaths`: solo bases y carpetas temporales (`Path.GetTempPath()`).
- La base real del usuario **no se toca** en ninguna prueba: la verificación en la app real usa una copia (`VACUUM INTO` desde `mode=ro`) en un perfil aislado (memoria "Pruebas en la app real").
- Verificación según `repo-build-test`: pruebas afectadas por tarea; la suite completa **una sola vez** al final de la Tarea 3.

## Reglas para unir filas repetidas (decididas aquí, cubiertas por pruebas en la Tarea 2)

Sobrevive la fila **más antigua** (menor `Id`); las demás se borran. Sus campos quedan así:

| Campo | Regla |
|---|---|
| `VistoLocal` | visto si **alguna** fila lo estaba |
| `FavoritoLocal` | favorito si **alguna** lo era |
| `SincronizadoEnNube` | si queda visto: `true` solo si **todas las filas vistas** ya estaban sincronizadas (si una no se envió a AniList, se sigue enviando); si no queda visto: `true` si alguna lo estaba |
| `UltimaReproduccion` | la **más reciente** (null si ninguna tiene) |
| `ProgresoSegundos`, `TotalSegundos` | los de la fila con la reproducción más reciente; sin fechas, los de la fila con más progreso; el total, el mayor si el elegido es 0 |
| `RutaArchivo`, `Resolucion`, `CodecVideo`, `Fps`, `RutaMiniatura` | el valor no vacío de la fila **más nueva** que lo tenga |
| `Es10Bit` | `true` si alguna lo era |

## Revisión de casos que el diseño implica

1. **Importar un JSON con el mismo episodio repetido** (archivo propio antiguo o ajeno): con el índice único, dos inserciones iguales harían fallar toda la importación (`IMP-03` es todo o nada). El lote debe fusionarlas. Prueba en Tarea 1.
2. **Muchos guardados a la vez del mismo episodio** (el reproductor guardando progreso mientras se generan miniaturas): una sola fila. Prueba en Tarea 1.
3. **Filas contradictorias** (vista/no vista, enviada/no enviada): se aplican las reglas de la tabla y no se pierde ningún "visto". Pruebas en Tarea 2.
4. **Proteger los datos antes de tocarlos:** la migración hace una copia (`VACUUM INTO`) solo si hay duplicados, y no la hace si no hay nada que unir. Prueba en Tarea 2.
5. **Restaurar una copia de seguridad anterior a v22** que aún tiene duplicados: al reabrir, la migración las une. Prueba en Tarea 2.
6. **Rendimiento:** guardar el progreso de un episodio ocurre cada pocos segundos; no puede cargar todos los registros del anime (One Piece tiene más de mil). El guardado de una fila busca solo esa fila. Se comprueba en la revisión del código de la Tarea 1.

## Estructura de archivos

| Archivo | Cambio |
|---|---|
| `AnimeLocalTracker/Services/DatabaseService.cs` | Modificar: guardado atómico, fusión dentro del lote, migración v22 y reglas de unión |
| `AnimeLocalTracker.Tests/Services/DatabaseServiceRegistrosConcurrentesTests.cs` | Crear: Tarea 1 |
| `AnimeLocalTracker.Tests/Services/DatabaseServiceDeduplicarRegistrosTests.cs` | Crear: Tarea 2 |
| `AnimeLocalTracker.Tests/Services/DatabaseServiceConservarRegistroTests.cs` | Modificar: simular la base antigua quitando el índice único |
| `docs/investigacion-descarga-masiva-y-eliminar-tras-ver.md` | Sin cambios |

---

## Tarea 1: Guardado atómico del episodio y fusión dentro del lote

**Archivos:**
- Modificar: `AnimeLocalTracker/Services/DatabaseService.cs` (`GuardarRegistroEpisodioAsync` ~línea 695, `GuardarFavoritoEpisodioAsync` ~720, `AplicarUpsertRegistros` ~758)
- Crear: `AnimeLocalTracker.Tests/Services/DatabaseServiceRegistrosConcurrentesTests.cs`

**Interfaces:**
- Produce: `GuardarRegistroEpisodioAsync` y `GuardarFavoritoEpisodioAsync` con la misma firma y el mismo significado que ahora, pero atómicos. `AplicarUpsertRegistros` acepta repeticiones dentro de la lista. La Tarea 2 depende de que ningún guardado deje de cumplir el índice único.

- [ ] **Paso 1: escribir las pruebas que fallan**

Crear `AnimeLocalTracker.Tests/Services/DatabaseServiceRegistrosConcurrentesTests.cs`:

```csharp
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
```

- [ ] **Paso 2: comprobar que fallan**

Compilar la app (`dotnet build AnimeLocalTracker/AnimeLocalTracker.csproj -c Debug`) y correr:
`dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false --filter "FullyQualifiedName~DatabaseServiceRegistrosConcurrentesTests"`
Esperado: `GuardarRegistro_MuchasVecesAlMismoTiempo…` y `GuardarRegistroYFavorito_AlMismoTiempo…` fallan con "Expected … to be 1, but found N" (la carrera es probabilística: si pasaran por casualidad, repetir 3 veces y subir 60 a 200; si aun así pasan, anotarlo en el informe y seguir, porque la prueba determinista de la Tarea 2 sigue protegiendo el índice). `GuardarEnLote_ConElMismoEpisodioRepetido…` falla con 2 filas e `ImportarUnJson…` también.

- [ ] **Paso 3: guardado de una fila en una sola operación**

En `DatabaseService.cs`, sustituir `GuardarRegistroEpisodioAsync` por:

```csharp
    public async Task GuardarRegistroEpisodioAsync(RegistroEpisodio registro)
    {
        // Buscar y insertar/actualizar en UNA sola operación sobre la conexión. Antes eran dos (un SELECT y luego un INSERT): otra
        // petición podía colarse en medio y dejar dos filas del mismo episodio (39 en la base real al revisarla el 2026-10-07).
        await _conexion.RunInTransactionAsync(db =>
        {
            var existente = db.Table<RegistroEpisodio>()
                .FirstOrDefault(r => r.AniListId == registro.AniListId && r.NumeroEpisodio == registro.NumeroEpisodio);
            if (existente != null)
            {
                FusionarRegistro(existente, registro);
                db.Update(existente);
            }
            else
            {
                // Si es la primera vez, insertamos el nuevo registro (la fecha solo la pone quien reproduce de verdad;
                // los marcados manuales quedan sin fecha).
                db.Insert(registro);
            }
        });
    }
```

Y `GuardarFavoritoEpisodioAsync` por:

```csharp
    public async Task GuardarFavoritoEpisodioAsync(int aniListId, int numeroEpisodio, bool favorito, string? rutaArchivo)
    {
        await _conexion.RunInTransactionAsync(db =>
        {
            var existente = db.Table<RegistroEpisodio>().FirstOrDefault(r => r.AniListId == aniListId && r.NumeroEpisodio == numeroEpisodio);
            if (existente != null)
            {
                existente.FavoritoLocal = favorito;
                if (string.IsNullOrWhiteSpace(existente.RutaArchivo) && !string.IsNullOrWhiteSpace(rutaArchivo)) existente.RutaArchivo = rutaArchivo;
                db.Update(existente);
            }
            else
            {
                db.Insert(new RegistroEpisodio
                {
                    AniListId = aniListId,
                    NumeroEpisodio = numeroEpisodio,
                    RutaArchivo = rutaArchivo ?? string.Empty,
                    FavoritoLocal = favorito,
                });
            }
        });
    }
```

(Conservar los comentarios existentes del método sobre por qué no se usa `GuardarRegistroEpisodioAsync` para el favorito.)

- [ ] **Paso 4: fusionar las repeticiones dentro del lote**

En `AplicarUpsertRegistros`, sustituir el bucle `foreach (var registro in registros) { ... }` y las dos listas por:

```csharp
        // (la fecha de reproducción la fija el llamador; aquí no se fabrica "ahora")
        var aInsertar = new List<RegistroEpisodio>();
        var aActualizar = new HashSet<RegistroEpisodio>();
        var nuevosDelLote = new Dictionary<(int, int), RegistroEpisodio>();

        foreach (var registro in registros)
        {
            var clave = (registro.AniListId, registro.NumeroEpisodio);
            if (existentes.TryGetValue(clave, out var existente))
            {
                FusionarRegistro(existente, registro);
                aActualizar.Add(existente);
            }
            else if (nuevosDelLote.TryGetValue(clave, out var nuevo))
            {
                // El mismo episodio repetido dentro del lote (JSON con repeticiones, listas armadas a mano): se une en una sola fila.
                FusionarRegistro(nuevo, registro);
            }
            else
            {
                // Registro nuevo: sin fecha (la pone solo la reproducción real).
                nuevosDelLote[clave] = registro;
                aInsertar.Add(registro);
            }
        }

        if (aInsertar.Count > 0) db.InsertAll(aInsertar, runInTransaction: false);
        if (aActualizar.Count > 0) db.UpdateAll(aActualizar, runInTransaction: false);
```

(Quitar las líneas antiguas `var aActualizar = new List<…>`, el `foreach` viejo y los dos `if` finales para que no queden duplicados.)

- [ ] **Paso 5: comprobar que pasan**

Compilar la app (0 advertencias) y correr: `dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false --filter "FullyQualifiedName~DatabaseService"`
Esperado: todas pasan (las anteriores no cambian de significado: el guardado es el mismo, solo atómico).

---

## Tarea 2: Migración v22: copia, unión de filas e índice único

**Archivos:**
- Modificar: `AnimeLocalTracker/Services/DatabaseService.cs` (lista `Migraciones` ~línea 105; métodos nuevos junto a `AgregarColumnasTemporadaFavoritoAsync` ~línea 283)
- Crear: `AnimeLocalTracker.Tests/Services/DatabaseServiceDeduplicarRegistrosTests.cs`

**Interfaces:**
- Consume: la Tarea 1 (ningún guardado nuevo deja duplicados).
- Produce: `DatabaseService.UnirFilasRepetidas(IReadOnlyList<RegistroEpisodio> filas)` (`internal static`, devuelve la fila superviviente ya unida) y la copia `Backups\biblioteca.antes-de-v22.db` junto a la base. El índice `IX_RegistroEpisodio_AnimeEp` pasa a ser único.

- [ ] **Paso 1: escribir las pruebas que fallan**

Crear `AnimeLocalTracker.Tests/Services/DatabaseServiceDeduplicarRegistrosTests.cs`:

```csharp
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

/// <summary>Migración v22: une los episodios con filas repetidas sin perder nada y deja un índice único.</summary>
public class DatabaseServiceDeduplicarRegistrosTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), $"AnimeTracker_Dedup_{Guid.NewGuid():N}");
    private readonly string _rutaDb;

    public DatabaseServiceDeduplicarRegistrosTests()
    {
        Directory.CreateDirectory(_carpeta);
        _rutaDb = Path.Combine(_carpeta, "biblioteca.db");
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        SQLiteAsyncConnection.ResetPool();
        try { Directory.Delete(_carpeta, recursive: true); } catch { /* ignore */ }
    }

    private string Copia => Path.Combine(_carpeta, "Backups", "biblioteca.antes-de-v22.db");

    /// <summary>Deja una base como la de antes de v22: sin índice único, con esas filas y en la versión 21.</summary>
    private async Task CrearBaseAntiguaAsync(params RegistroEpisodio[] filas)
    {
        using (var sut = new DatabaseService(_rutaDb)) await sut.InicializarBaseDatosAsync();
        SQLiteAsyncConnection.ResetPool();
        using var conexion = new SQLiteConnection(_rutaDb);
        conexion.Execute("DROP INDEX IF EXISTS IX_RegistroEpisodio_AnimeEp;");
        conexion.Execute("CREATE INDEX IX_RegistroEpisodio_AnimeEp ON RegistroEpisodio(AniListId, NumeroEpisodio);");
        foreach (var fila in filas) conexion.Insert(fila);
        conexion.Execute("PRAGMA user_version = 21;");
    }

    private async Task<List<RegistroEpisodio>> MigrarYLeerAsync()
    {
        using var sut = new DatabaseService(_rutaDb);
        await sut.InicializarBaseDatosAsync();
        return await sut.ObtenerTodosLosRegistrosAsync();
    }

    private static readonly DateTime Antes = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Despues = new(2026, 9, 20, 18, 0, 0, DateTimeKind.Utc);

    // ── Reglas de unión (puras, sin base de datos) ──

    [Fact]
    public void Union_VistoSiAlgunaFilaLoEstaba_YFavoritoSiAlgunaLoEra()
    {
        var unida = DatabaseService.UnirFilasRepetidas([
            new RegistroEpisodio { Id = 1, VistoLocal = false, FavoritoLocal = true },
            new RegistroEpisodio { Id = 2, VistoLocal = true, FavoritoLocal = false },
        ]);

        unida.Id.Should().Be(1, "sobrevive la fila más antigua");
        unida.VistoLocal.Should().BeTrue("nunca se pierde un 'visto'");
        unida.FavoritoLocal.Should().BeTrue();
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]   // una vista no se envió a AniList: se sigue enviando
    [InlineData(false, false, false)]
    public void Union_SincronizadoSoloSiTodasLasFilasVistasLoEstaban(bool primeraSync, bool segundaSync, bool esperado)
    {
        var unida = DatabaseService.UnirFilasRepetidas([
            new RegistroEpisodio { Id = 1, VistoLocal = true, SincronizadoEnNube = primeraSync },
            new RegistroEpisodio { Id = 2, VistoLocal = true, SincronizadoEnNube = segundaSync },
        ]);

        unida.SincronizadoEnNube.Should().Be(esperado);
    }

    [Fact]
    public void Union_UnaFilaVistaSinEnviarYOtraSinVerYEnviada_SigueSinEnviarse()
    {
        var unida = DatabaseService.UnirFilasRepetidas([
            new RegistroEpisodio { Id = 1, VistoLocal = false, SincronizadoEnNube = true },
            new RegistroEpisodio { Id = 2, VistoLocal = true, SincronizadoEnNube = false },
        ]);

        unida.VistoLocal.Should().BeTrue();
        unida.SincronizadoEnNube.Should().BeFalse("el 'visto' que falta por enviar no se da por enviado por culpa de la otra fila");
    }

    [Fact]
    public void Union_ProgresoYFechaSonLosDeLaReproduccionMasReciente()
    {
        var unida = DatabaseService.UnirFilasRepetidas([
            new RegistroEpisodio { Id = 1, ProgresoSegundos = 900, TotalSegundos = 1400, UltimaReproduccion = Despues },
            new RegistroEpisodio { Id = 2, ProgresoSegundos = 100, TotalSegundos = 1400, UltimaReproduccion = Antes },
        ]);

        unida.ProgresoSegundos.Should().Be(900);
        unida.UltimaReproduccion.Should().Be(Despues);
    }

    [Fact]
    public void Union_SinFechas_NoLasInventaYTomaElMayorProgreso()
    {
        var unida = DatabaseService.UnirFilasRepetidas([
            new RegistroEpisodio { Id = 1, ProgresoSegundos = 50 },
            new RegistroEpisodio { Id = 2, ProgresoSegundos = 700, TotalSegundos = 1400 },
        ]);

        unida.UltimaReproduccion.Should().BeNull("el historial es visionado real: no se fabrica una fecha");
        unida.ProgresoSegundos.Should().Be(700);
        unida.TotalSegundos.Should().Be(1400);
    }

    [Fact]
    public void Union_ArchivoYDatosTecnicos_LosDeLaFilaMasNuevaQueLosTenga()
    {
        var unida = DatabaseService.UnirFilasRepetidas([
            new RegistroEpisodio { Id = 1, RutaArchivo = @"C:\viejo\ep.mp4", Resolucion = "1280x720", CodecVideo = "h264", RutaMiniatura = @"C:\t\a.jpg" },
            new RegistroEpisodio { Id = 2, RutaArchivo = @"C:\nuevo\ep.mp4", Resolucion = "", CodecVideo = null, RutaMiniatura = null, Es10Bit = true },
        ]);

        unida.RutaArchivo.Should().Be(@"C:\nuevo\ep.mp4");
        unida.Resolucion.Should().Be("1280x720", "la fila más nueva no la tenía");
        unida.CodecVideo.Should().Be("h264");
        unida.RutaMiniatura.Should().Be(@"C:\t\a.jpg");
        unida.Es10Bit.Should().BeTrue();
    }

    // ── La migración ──

    [Fact]
    public async Task Migracion_UneLasFilasRepetidas_ConservaElRestoYDejaIndiceUnico()
    {
        await CrearBaseAntiguaAsync(
            new RegistroEpisodio { AniListId = 1, NumeroEpisodio = 1, VistoLocal = true, UltimaReproduccion = Despues, ProgresoSegundos = 30, TotalSegundos = 30 },
            new RegistroEpisodio { AniListId = 1, NumeroEpisodio = 1, RutaArchivo = @"C:\Anime\1\Episodio 01.mp4", Resolucion = "1920x1080" },
            new RegistroEpisodio { AniListId = 1, NumeroEpisodio = 2, VistoLocal = true },
            new RegistroEpisodio { AniListId = 2, NumeroEpisodio = 1, FavoritoLocal = true });

        var filas = await MigrarYLeerAsync();

        filas.Should().HaveCount(3, "solo se unió el episodio repetido");
        var unida = filas.Single(r => r.AniListId == 1 && r.NumeroEpisodio == 1);
        unida.VistoLocal.Should().BeTrue();
        unida.UltimaReproduccion.Should().Be(Despues);
        unida.RutaArchivo.Should().EndWith("Episodio 01.mp4");
        unida.Resolucion.Should().Be("1920x1080");
        filas.Single(r => r.AniListId == 1 && r.NumeroEpisodio == 2).VistoLocal.Should().BeTrue("los demás no se tocan");
        filas.Single(r => r.AniListId == 2).FavoritoLocal.Should().BeTrue();

        using var conexion = new SQLiteConnection(_rutaDb);
        conexion.ExecuteScalar<int>("PRAGMA user_version;").Should().BeGreaterThanOrEqualTo(22);
        var duplicar = () => conexion.Insert(new RegistroEpisodio { AniListId = 1, NumeroEpisodio = 1 });
        duplicar.Should().Throw<SQLiteException>("el índice único impide volver a crear un duplicado");
    }

    [Fact]
    public async Task Migracion_UnGrupoDeTresFilas_QuedaEnUna()
    {
        await CrearBaseAntiguaAsync(
            new RegistroEpisodio { AniListId = 1, NumeroEpisodio = 4 },
            new RegistroEpisodio { AniListId = 1, NumeroEpisodio = 4, VistoLocal = true },
            new RegistroEpisodio { AniListId = 1, NumeroEpisodio = 4, FavoritoLocal = true });

        var filas = await MigrarYLeerAsync();

        var unica = filas.Should().ContainSingle().Subject;
        unica.VistoLocal.Should().BeTrue();
        unica.FavoritoLocal.Should().BeTrue();
    }

    [Fact]
    public async Task Migracion_ConDuplicados_HaceUnaCopiaConLasFilasOriginales()
    {
        await CrearBaseAntiguaAsync(
            new RegistroEpisodio { AniListId = 1, NumeroEpisodio = 1, VistoLocal = true },
            new RegistroEpisodio { AniListId = 1, NumeroEpisodio = 1 });

        await MigrarYLeerAsync();

        File.Exists(Copia).Should().BeTrue("antes de unir filas se guarda una copia de la base");
        using var copia = new SQLiteConnection(Copia);
        copia.ExecuteScalar<int>("SELECT COUNT(*) FROM RegistroEpisodio;").Should().Be(2, "la copia conserva las dos filas tal como estaban");
    }

    [Fact]
    public async Task Migracion_SinDuplicados_NoHaceCopiaNiCambiaNada()
    {
        await CrearBaseAntiguaAsync(
            new RegistroEpisodio { AniListId = 1, NumeroEpisodio = 1, VistoLocal = true },
            new RegistroEpisodio { AniListId = 1, NumeroEpisodio = 2 });

        var filas = await MigrarYLeerAsync();

        filas.Should().HaveCount(2);
        File.Exists(Copia).Should().BeFalse("no había nada que unir");
    }

    [Fact]
    public async Task Migracion_SeEjecutaUnaSolaVez_AlReabrirNoCambiaNada()
    {
        await CrearBaseAntiguaAsync(
            new RegistroEpisodio { AniListId = 1, NumeroEpisodio = 1, VistoLocal = true },
            new RegistroEpisodio { AniListId = 1, NumeroEpisodio = 1 });
        var primera = await MigrarYLeerAsync();
        SQLiteAsyncConnection.ResetPool();

        var segunda = await MigrarYLeerAsync();

        segunda.Select(r => (r.Id, r.VistoLocal)).Should().Equal(primera.Select(r => (r.Id, r.VistoLocal)));
    }

    [Fact]
    public async Task RestaurarUnaCopiaAnteriorAV22ConDuplicados_LasUneAlReabrir()
    {
        // Una copia de seguridad hecha antes de esta migración, con un episodio repetido.
        await CrearBaseAntiguaAsync(
            new RegistroEpisodio { AniListId = 1, NumeroEpisodio = 1, VistoLocal = true },
            new RegistroEpisodio { AniListId = 1, NumeroEpisodio = 1 });
        string copiaAntigua = Path.Combine(_carpeta, "copia-antigua.db");
        File.Copy(_rutaDb, copiaAntigua);
        SQLiteAsyncConnection.ResetPool();
        File.Delete(_rutaDb);
        foreach (var sufijo in new[] { "-wal", "-shm" }) { try { File.Delete(_rutaDb + sufijo); } catch { /* ignore */ } }

        using var sut = new DatabaseService(_rutaDb);
        await sut.InicializarBaseDatosAsync();
        (await sut.RestaurarCopiaSeguridadAsync(copiaAntigua)).Should().BeTrue();

        (await sut.ObtenerTodosLosRegistrosAsync()).Should().ContainSingle().Which.VistoLocal.Should().BeTrue();
    }
}
```

- [ ] **Paso 2: comprobar que fallan**

Run: `dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false --filter "FullyQualifiedName~DatabaseServiceDeduplicarRegistrosTests"`
Esperado: error de compilación `CS0117` ("`DatabaseService` no contiene una definición para `UnirFilasRepetidas`"). Para ver también el fallo de comportamiento, añadir temporalmente un `UnirFilasRepetidas` que devuelva `filas[0]` sin unir y comprobar que las pruebas de reglas y de migración fallan (y quitarlo antes del Paso 3).

- [ ] **Paso 3: reglas de unión, copia previa y migración**

En `DatabaseService.cs`, añadir la entrada al final de la lista `Migraciones` (poner coma tras la v21):

```csharp
        (21, "conservar los videos de un anime (protege del borrado de \"Eliminar tras ver\")", AgregarColumnasTemporadaFavoritoAsync),
        // Un episodio con dos filas era posible por una carrera entre guardados (v22 la cierra): se unen las que ya hay (con copia
        // previa de la base) y el índice (AniListId, NumeroEpisodio) pasa a ser único.
        (22, "episodios sin filas repetidas (se unen) + índice único por (anime, episodio)", DeduplicarRegistrosEpisodioAsync)
```

Y junto a `AgregarColumnasTemporadaFavoritoAsync`, los métodos nuevos:

```csharp
    /// <summary>
    /// v22: une las filas repetidas de un mismo episodio (ver <see cref="UnirFilasRepetidas"/>) y convierte el índice por
    /// (AniListId, NumeroEpisodio) en único. Si hay algo que unir, antes se guarda una copia de la base junto a ella
    /// (<c>Backups\biblioteca.antes-de-v22.db</c>): la unión borra filas y no se puede deshacer sin ella.
    /// </summary>
    private static async Task DeduplicarRegistrosEpisodioAsync(SQLiteAsyncConnection conexion)
    {
        var repetidas = await conexion.QueryAsync<RegistroEpisodio>(
            "SELECT r.* FROM RegistroEpisodio r JOIN (SELECT AniListId, NumeroEpisodio FROM RegistroEpisodio " +
            "GROUP BY AniListId, NumeroEpisodio HAVING COUNT(*) > 1) d " +
            "ON d.AniListId = r.AniListId AND d.NumeroEpisodio = r.NumeroEpisodio " +
            "ORDER BY r.AniListId, r.NumeroEpisodio, r.Id");

        if (repetidas.Count > 0)
        {
            await CopiaAntesDeUnirFilasAsync(conexion);
            var grupos = repetidas.GroupBy(r => (r.AniListId, r.NumeroEpisodio)).ToList();
            await conexion.RunInTransactionAsync(db =>
            {
                foreach (var grupo in grupos)
                {
                    var filas = grupo.OrderBy(r => r.Id).ToList();
                    db.Update(UnirFilasRepetidas(filas));
                    foreach (var sobrante in filas.Skip(1)) db.Delete<RegistroEpisodio>(sobrante.Id);
                }
            });
            AppLogger.Info("DatabaseService", $"Episodios con filas repetidas unidos: {grupos.Count} (se borraron {repetidas.Count - grupos.Count} filas sobrantes).");
        }

        await conexion.ExecuteAsync("DROP INDEX IF EXISTS IX_RegistroEpisodio_AnimeEp;");
        await conexion.ExecuteAsync("CREATE UNIQUE INDEX IX_RegistroEpisodio_AnimeEp ON RegistroEpisodio(AniListId, NumeroEpisodio);");
    }

    private static async Task CopiaAntesDeUnirFilasAsync(SQLiteAsyncConnection conexion)
    {
        string carpeta = Path.Combine(Path.GetDirectoryName(conexion.DatabasePath)!, "Backups");
        Directory.CreateDirectory(carpeta);
        string destino = Path.Combine(carpeta, "biblioteca.antes-de-v22.db");
        if (File.Exists(destino)) File.Delete(destino);
        await conexion.ExecuteAsync($"VACUUM INTO '{destino.Replace("'", "''")}'");
    }

    /// <summary>
    /// Une las filas de UN episodio (ordenadas por Id) en la más antigua, que es la que sobrevive. Reglas: visto y favorito si lo
    /// era alguna; sincronizado solo si lo estaban todas las filas vistas (un 'visto' sin enviar a AniList se sigue enviando);
    /// fecha de reproducción la más reciente (nunca se inventa) y progreso el de esa reproducción; ruta, miniatura y datos técnicos
    /// los de la fila más nueva que los tenga.
    /// </summary>
    internal static RegistroEpisodio UnirFilasRepetidas(IReadOnlyList<RegistroEpisodio> filas)
    {
        var unida = filas[0];
        var vistas = filas.Where(f => f.VistoLocal).ToList();
        unida.VistoLocal = vistas.Count > 0;
        unida.FavoritoLocal = filas.Any(f => f.FavoritoLocal);
        unida.SincronizadoEnNube = vistas.Count > 0 ? vistas.All(f => f.SincronizadoEnNube) : filas.Any(f => f.SincronizadoEnNube);
        unida.Es10Bit = filas.Any(f => f.Es10Bit);

        var reciente = filas.Where(f => f.UltimaReproduccion != null).OrderByDescending(f => f.UltimaReproduccion).ThenByDescending(f => f.Id).FirstOrDefault()
                       ?? filas.OrderByDescending(f => f.ProgresoSegundos).ThenByDescending(f => f.Id).First();
        unida.UltimaReproduccion = filas.Max(f => f.UltimaReproduccion);
        unida.ProgresoSegundos = reciente.ProgresoSegundos;
        unida.TotalSegundos = reciente.TotalSegundos > 0 ? reciente.TotalSegundos : filas.Max(f => f.TotalSegundos);

        static string? MasNuevo(IReadOnlyList<RegistroEpisodio> filas, Func<RegistroEpisodio, string?> campo)
            => filas.OrderByDescending(f => f.Id).Select(campo).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        unida.RutaArchivo = MasNuevo(filas, f => f.RutaArchivo) ?? string.Empty;
        unida.Resolucion = MasNuevo(filas, f => f.Resolucion);
        unida.CodecVideo = MasNuevo(filas, f => f.CodecVideo);
        unida.Fps = MasNuevo(filas, f => f.Fps);
        unida.RutaMiniatura = MasNuevo(filas, f => f.RutaMiniatura);
        return unida;
    }
```

- [ ] **Paso 4: comprobar que pasan**

Compilar la app (0 advertencias) y correr: `dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false --filter "FullyQualifiedName~DatabaseService"`
Esperado: las pruebas nuevas pasan. Es posible que falle `DatabaseServiceConservarRegistroTests.ConEpisodioDuplicado_…` (inserta dos filas del mismo episodio, que ahora el índice único prohíbe): se arregla en la Tarea 3.

- [ ] **Paso 5: comprobar que la rotación de copias no borra la copia previa**

Leer `CrearBackupRotativoAsync` (~línea 300 de `DatabaseService.cs`) y confirmar que solo rota los archivos con el patrón de las copias rotativas (`biblioteca.backup.N.db`) y no toca `biblioteca.antes-de-v22.db`. Si lo tocara, cambiar el nombre de la copia previa a uno que la rotación ignore y repetir el Paso 4.

---

## Tarea 3: Ajustar la prueba antigua, comprobar con los datos reales y cerrar

**Archivos:**
- Modificar: `AnimeLocalTracker.Tests/Services/DatabaseServiceConservarRegistroTests.cs`

- [ ] **Paso 1: la prueba que simulaba duplicados los simula ahora como una base antigua**

En `ConEpisodioDuplicado_LimpiaLasDosFilasYConservaLoVisto`, dentro del bloque `using (var conexion = new SQLiteConnection(_rutaDb))`, antes de los `Insert`, añadir:

```csharp
            // Una base antigua (antes de v22) aún permite dos filas del mismo episodio: se quita el índice único para simularla.
            conexion.Execute("DROP INDEX IF EXISTS IX_RegistroEpisodio_AnimeEp;");
```

y el comentario del bloque pasa a decir "Las bases anteriores a v22 pueden tener dos filas del mismo episodio…". (`ConservarRegistroTrasEliminarArchivoAsync` sigue actualizando todas las filas: no se toca.)

- [ ] **Paso 2: suite completa, una sola vez, y datos reales intactos**

Antes y después, listar la carpeta de datos reales (`find "$LOCALAPPDATA/AnimeLocalTrackerData" -maxdepth 2 -printf '%P|%s|%T@\n'`, ignorando `Logs/` y `biblioteca.db-shm/-wal`) y hacer `diff`; esperado, sin diferencias.
Run: `dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false`
Esperado: 0 fallos (2872 de antes + las pruebas nuevas).

- [ ] **Paso 3: la migración sobre una copia de los datos reales**

Con un perfil aislado (memoria "Pruebas en la app real", receta de `preparar_perfil.py`: copia `VACUUM INTO` desde una conexión `mode=ro`, sin `anilist_token.txt`, `PreferenciaEmision.AutoDescargar = 0`):
1. Antes de lanzar, con Python sobre la copia: anotar `COUNT(*)` de `RegistroEpisodio` (esperado 3507), los 39 grupos repetidos y, para los 11 con VistoLocal distinto, las filas originales.
2. Lanzar la app con `USERPROFILE`/`LOCALAPPDATA`/`APPDATA` apuntando al perfil, esperar a que el log del perfil diga "Migración aplicada: v22" y "Episodios con filas repetidas unidos: 39".
3. Con Python sobre la copia (después de cerrar el PID lanzado): `COUNT(*)` = 3468; ningún grupo repetido; el índice es único; existe `Backups\biblioteca.antes-de-v22.db` con 3507 filas; en los 11 grupos contradictorios la fila resultante cumple las reglas (visto si alguna lo estaba, etc.); el resto de tablas (`AnimeItem`, etc.) tiene los mismos recuentos.
4. Abrir la ficha de un anime afectado por UIA y comprobar que su "N de M vistos" coincide con el cálculo hecho con Python sobre los datos originales.
5. Cerrar **solo el PID lanzado** y borrar el perfil temporal. Si algo no se pudo comprobar, decirlo en el informe.

- [ ] **Paso 4: dejar el trabajo sin commit**

`git status` debe mostrar solo los archivos de la tabla de estructura (y el plan). No hacer commit ni push: el usuario decide cuándo.

---

## Autorrevisión del plan contra el diseño

- **Cerrar la carrera** → Tarea 1 (guardado atómico + fusión en el lote). **Unir las 39 filas sin perder nada** → Tarea 2 (reglas + copia previa). **Garantía permanente** → Tarea 2 (índice único). **No romper importación ni restauración** → Pruebas de Tarea 1 (JSON repetido) y Tarea 2 (restaurar copia antigua).
- **Fuera de este plan:** la miniatura que queda en disco, el aviso a lectores de pantalla, y cualquier cambio en cómo se marca un episodio como visto.
- **No cubierto por pruebas unitarias:** que la migración se comporte igual con los 3507 registros reales (Paso 3 de la Tarea 3, sobre una copia) y que la carrera de la Tarea 1 se reproduzca siempre antes del arreglo (es probabilística; la garantía final es el índice único).
