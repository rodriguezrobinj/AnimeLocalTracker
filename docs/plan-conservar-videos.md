# Conservar los videos de un anime: plan de implementación

> **Para quien ejecute el plan:** ejecutar en la sesión con `superpowers:executing-plans` (el repo prohíbe lanzar subagentes por iniciativa propia). Los pasos usan casillas `- [ ]`. **Sin commit**: la regla del repo dice que no se hace commit ni push sin orden del usuario, así que cada tarea termina en verificación, no en commit.

**Objetivo:** un interruptor "Conservar los videos" en el menú Herramientas de la ficha: con él activo, ningún modo de "Eliminar el video tras verlo" borra nada de ese anime ni pregunta nada.

**Arquitectura:** una columna booleana nueva en `AnimeItem` (migración v21, misma vía que v4 y v19: `CreateTableAsync<AnimeItem>` añade la columna). `LimpiadorDeEpisodios` lee el anime que ya carga y sale si está protegido. La ficha lo cambia con un comando que guarda con `ActualizarAnimeAsync`, igual que el favorito. La importación de un JSON no puede quitar una protección que ya existe.

**Tecnología:** C# 12 / .NET 8, WPF, CommunityToolkit.Mvvm, sqlite-net, xUnit + FluentAssertions + Moq.

**Diseño:** decisión del usuario del 2026-10-06 ("Interruptor 'Conservar los videos' en la ficha"). Sustituye al punto 5 ("Fuera de alcance: excepciones por anime") de `docs/investigacion-descarga-masiva-y-eliminar-tras-ver.md`.

## Restricciones globales

- 0 advertencias: `TreatWarningsAsErrors` está activo (`Directory.Build.props`).
- Textos con `{loc:T Clave}` en XAML y `LocalizationService.T("Clave")` en código; toda clave nueva, en ES **y** EN, y en la lista de `LocalizationServiceTests`.
- Colores solo de `Brush.*`/`AppText.*`; el botón del menú lleva `AutomationProperties.Name`.
- El esquema evoluciona con la lista `Migraciones` de `DatabaseService` (versión + acción), nunca con `CreateTableAsync` suelto.
- La protección solo afecta a "Eliminar tras ver". El borrado manual de un episodio y "Liberar espacio" son acciones explícitas del usuario y **no** se tocan.
- Las pruebas nunca escriben ni borran bajo `AppDataPaths`: solo carpetas o bases temporales (`Path.GetTempPath()`).
- Verificación según `repo-build-test`: pruebas afectadas por tarea; la suite completa **una sola vez** al final de la Tarea 3.
- La prueba en la app real, **solo** con un perfil aislado (memoria "Pruebas en la app real").

## Revisión de casos que el diseño implica

1. **Importar un JSON anterior a esta función** (o de otra persona) sobre un anime ya protegido: no debe quitar la protección. `ImportarBibliotecaJsonAsync` usa `InsertOrReplace`, que pisaría la fila entera con `ConservarVideos = false` y el siguiente "Eliminar tras ver" borraría los videos de un anime que el usuario protegió. Prueba en Tarea 1.
2. **Base existente en v20 sin la columna**: la migración la añade y los animes quedan sin proteger. Prueba en Tarea 1.
3. **"Al completar la serie" con el anime protegido**: ni diálogo ni borrado. Prueba en Tarea 2.
4. **"Consumo ligero" con el anime protegido** y más episodios vistos que los que se conservan: no borra ninguno. Prueba en Tarea 2.
5. **El guardado del interruptor falla**: el interruptor vuelve a como estaba y avisa (si no, mostraría "protegido" sin estarlo). Prueba en Tarea 3.

## Estructura de archivos

| Archivo | Cambio |
|---|---|
| `AnimeLocalTracker/Models/AnimeItem.cs` | Modificar: propiedad `ConservarVideos` |
| `AnimeLocalTracker/Services/DatabaseService.cs` | Modificar: migración v21 y fusión en la importación |
| `AnimeLocalTracker/Services/LimpiadorDeEpisodios.cs` | Modificar: salir si el anime está protegido |
| `AnimeLocalTracker/ViewModels/DetalleViewModel.cs` | Modificar: propiedad, carga y comando |
| `AnimeLocalTracker/Views/DetalleView.xaml` | Modificar: botón en el menú Herramientas |
| `AnimeLocalTracker/Services/LocalizationService.cs` | Modificar: 3 claves `Det_ConservarVideos*` en ES y EN |
| `AnimeLocalTracker.Tests/Services/DatabaseServiceConservarVideosTests.cs` | Crear |
| `AnimeLocalTracker.Tests/Services/LimpiadorDeEpisodiosTests.cs` | Modificar: parámetro y 3 pruebas |
| `AnimeLocalTracker.Tests/ViewModels/DetalleConservarVideosTests.cs` | Crear |
| `AnimeLocalTracker.Tests/Services/LocalizationServiceTests.cs` | Modificar: 3 claves en la lista |
| `docs/investigacion-descarga-masiva-y-eliminar-tras-ver.md` | Modificar: punto 5 |

---

## Tarea 1: Columna, migración v21 y protección en la importación

**Archivos:**
- Modificar: `AnimeLocalTracker/Models/AnimeItem.cs` (junto a `FechaAgregadoUtc`, línea ~120)
- Modificar: `AnimeLocalTracker/Services/DatabaseService.cs` (lista `Migraciones` ~línea 105 y `ImportarBibliotecaJsonAsync` ~líneas 583 y 622)
- Crear: `AnimeLocalTracker.Tests/Services/DatabaseServiceConservarVideosTests.cs`

**Interfaces:**
- Produce: `AnimeItem.ConservarVideos` (`bool`, por defecto `false`), guardada por `ActualizarAnimeAsync`, `GuardarAnimeAsync` y leída por `ObtenerAnimePorIdAsync` y `ObtenerTodosLosAnimesAsync`. La tarea 2 y la 3 la usan por ese nombre.

- [ ] **Paso 1: escribir las pruebas que fallan**

Crear `AnimeLocalTracker.Tests/Services/DatabaseServiceConservarVideosTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using FluentAssertions;
using SQLite;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>"Conservar los videos": se guarda con el anime, llega a bases ya existentes y la importación no la quita.</summary>
public class DatabaseServiceConservarVideosTests : IDisposable
{
    private readonly string _rutaDb = Path.Combine(Path.GetTempPath(), $"AnimeTracker_ConservarVideos_{Guid.NewGuid():N}.db");
    private readonly string _rutaJson = Path.Combine(Path.GetTempPath(), $"import_conservar_{Guid.NewGuid():N}.json");

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

    [Fact]
    public async Task SeGuardaConElAnime_YSobreviveAReabrirLaBase()
    {
        using (var sut = new DatabaseService(_rutaDb))
        {
            await sut.InicializarBaseDatosAsync();
            await sut.GuardarAnimeAsync(new AnimeItem { AniListId = 1, Titulo = "Frieren" });
            var anime = (await sut.ObtenerAnimePorIdAsync(1))!;
            anime.ConservarVideos.Should().BeFalse("por defecto ningún anime está protegido");

            anime.ConservarVideos = true;
            await sut.ActualizarAnimeAsync(anime);
        }

        using var reabierta = new DatabaseService(_rutaDb);
        await reabierta.InicializarBaseDatosAsync();
        (await reabierta.ObtenerAnimePorIdAsync(1))!.ConservarVideos.Should().BeTrue();
    }

    [Fact]
    public async Task BaseEnV20SinLaColumna_LaMigracionLaAgregaYLosAnimesQuedanSinProteger()
    {
        using (var sut = new DatabaseService(_rutaDb))
        {
            await sut.InicializarBaseDatosAsync();
            await sut.GuardarAnimeAsync(new AnimeItem { AniListId = 1, Titulo = "De antes" });
        }
        SQLiteAsyncConnection.ResetPool();

        // Se reproduce una base creada antes de esta función: sin la columna y en la versión 20.
        using (var vieja = new SQLiteConnection(_rutaDb))
        {
            vieja.Execute("ALTER TABLE AnimeItem DROP COLUMN ConservarVideos;");
            vieja.Execute("PRAGMA user_version = 20;");
        }

        using var reabierta = new DatabaseService(_rutaDb);
        await reabierta.InicializarBaseDatosAsync();

        (await reabierta.ObtenerAnimePorIdAsync(1))!.ConservarVideos.Should().BeFalse();
        using var conexion = new SQLiteConnection(_rutaDb);
        conexion.ExecuteScalar<int>("PRAGMA user_version;").Should().BeGreaterThanOrEqualTo(21);
    }

    [Fact]
    public async Task ImportarUnJsonSinLaMarca_NoQuitaLaProteccionDeUnAnimeQueYaTenia()
    {
        using var sut = new DatabaseService(_rutaDb);
        await sut.InicializarBaseDatosAsync();
        await sut.GuardarAnimeAsync(new AnimeItem { AniListId = 7, Titulo = "Protegido", ConservarVideos = true });
        await sut.GuardarAnimeAsync(new AnimeItem { AniListId = 8, Titulo = "Normal" });

        // Una exportación hecha antes de existir la función (o por otra persona) trae ConservarVideos = false.
        var backup = new DatabaseService.BibliotecaBackup
        {
            Animes = new List<AnimeItem>
            {
                new() { AniListId = 7, Titulo = "Protegido (del archivo)" },
                new() { AniListId = 8, Titulo = "Normal (del archivo)" },
                new() { AniListId = 9, Titulo = "Nuevo", ConservarVideos = true },
            }
        };
        await File.WriteAllTextAsync(_rutaJson, System.Text.Json.JsonSerializer.Serialize(backup));

        await sut.ImportarBibliotecaJsonAsync(_rutaJson);

        (await sut.ObtenerAnimePorIdAsync(7))!.ConservarVideos.Should().BeTrue("importar no debe quitar una protección que ya existía");
        (await sut.ObtenerAnimePorIdAsync(7))!.Titulo.Should().Be("Protegido (del archivo)", "el resto de campos sí se fusiona como siempre");
        (await sut.ObtenerAnimePorIdAsync(8))!.ConservarVideos.Should().BeFalse();
        (await sut.ObtenerAnimePorIdAsync(9))!.ConservarVideos.Should().BeTrue("la marca de un anime nuevo del archivo se respeta");
    }
}
```

- [ ] **Paso 2: comprobar que fallan**

Run: `dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false --filter "FullyQualifiedName~DatabaseServiceConservarVideosTests"`
Esperado: error de compilación `CS1061` ("`AnimeItem` no contiene una definición para `ConservarVideos`"). Antes hay que compilar la app: `dotnet build AnimeLocalTracker/AnimeLocalTracker.csproj -c Debug`.

- [ ] **Paso 3: añadir la propiedad al modelo**

En `AnimeLocalTracker/Models/AnimeItem.cs`, justo debajo de `FechaAgregadoUtc`:

```csharp
    /// <summary>
    /// "Conservar los videos": con esto activo, ningún modo de "Eliminar el video tras verlo" borra nada de este anime
    /// (ni pregunta). Solo protege del borrado automático: borrar a mano un episodio o "Liberar espacio" siguen funcionando.
    /// Migración v21; los animes que ya estaban quedan sin proteger.
    /// </summary>
    public bool ConservarVideos { get; set; }
```

- [ ] **Paso 4: añadir la migración v21**

En `AnimeLocalTracker/Services/DatabaseService.cs`, en la lista `Migraciones`, cambiar el final:

```csharp
        (20, "sonido del reproductor por anime o por capítulo (volumen, ecualizador y Modo Noche)", CrearTablaAjusteAudioAsync),
        // Columna nueva del modelo (misma vía que la v4 y la v19). Los animes que ya estaban quedan sin proteger.
        (21, "conservar los videos de un anime (protege del borrado de \"Eliminar tras ver\")", AgregarColumnasTemporadaFavoritoAsync)
    };
```

(La línea 20 pasa a terminar en coma.)

- [ ] **Paso 5: que importar no quite la protección**

En `ImportarBibliotecaJsonAsync`, la consulta de la línea ~583 ya carga todos los animes actuales; guardar también la marca. Sustituir:

```csharp
        var carpetasActuales = (await _conexion.Table<AnimeItem>().ToListAsync()).ToDictionary(a => a.AniListId, a => a.RutaCarpeta);
```

por:

```csharp
        var animesActuales = await _conexion.Table<AnimeItem>().ToListAsync();
        var carpetasActuales = animesActuales.ToDictionary(a => a.AniListId, a => a.RutaCarpeta);
        var protegidosActuales = animesActuales.Where(a => a.ConservarVideos).Select(a => a.AniListId).ToHashSet();
```

y, justo antes de la transacción (después de `var animesUnicos = ...`):

```csharp
        // Una protección que ya existe aquí nunca se quita al importar (el archivo puede ser anterior a la función o de otra persona).
        foreach (var anime in animesUnicos.Where(a => protegidosActuales.Contains(a.AniListId))) anime.ConservarVideos = true;
```

- [ ] **Paso 6: comprobar que pasan**

Compilar la app (0 advertencias) y correr: `dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false --filter "FullyQualifiedName~DatabaseService"`
Esperado: todas pasan. Si `DROP COLUMN` no lo admite la versión de SQLite incluida, la segunda prueba falla con "near DROP: syntax error": entonces recrear la tabla vieja con `CREATE TABLE AnimeItem_v20 AS SELECT <todas las columnas menos ConservarVideos> ...` y renombrarla (no cambiar la comprobación final).

---

## Tarea 2: El limpiador respeta la protección

**Archivos:**
- Modificar: `AnimeLocalTracker/Services/LimpiadorDeEpisodios.cs` (método `AplicarTrasVerAsync`, después de cargar el anime, línea ~51)
- Modificar: `AnimeLocalTracker.Tests/Services/LimpiadorDeEpisodiosTests.cs` (método `Preparar` y tres pruebas nuevas)

**Interfaces:**
- Consume: `AnimeItem.ConservarVideos` (Tarea 1).

- [ ] **Paso 1: escribir las pruebas que fallan**

En `LimpiadorDeEpisodiosTests.cs`, añadir el parámetro al final de la firma de `Preparar` y usarlo al crear el anime:

```csharp
    private LimpiadorDeEpisodios Preparar(string modo, int[] conArchivo, int[] vistos, int total = 12, int conservar = 3,
        IEnumerable<EpisodioItem>? extraEscaneados = null, bool confirma = true, bool conservarVideos = false)
```

```csharp
        _db.Setup(d => d.ObtenerAnimePorIdAsync(Id)).ReturnsAsync(new AnimeItem { AniListId = Id, Titulo = "Frieren", TotalEpisodios = total, RutaCarpeta = _carpeta, ConservarVideos = conservarVideos });
```

Añadir las pruebas (junto a las demás, antes de `Dispose` o al final de la clase):

```csharp
    [Theory]
    [InlineData(ModoEliminarTrasVerValores.Automatico)]
    [InlineData(ModoEliminarTrasVerValores.ConsumoLigero)]
    [InlineData(ModoEliminarTrasVerValores.AlCompletarSerie)]
    public async Task AnimeProtegido_NoSeBorraNadaEnNingunModo(string modo)
    {
        // 6 episodios vistos con archivo y el 6 es el último: cualquiera de los tres modos habría borrado algo.
        var sut = Preparar(modo, conArchivo: [1, 2, 3, 4, 5, 6], vistos: [1, 2, 3, 4, 5, 6], total: 6, conservar: 1, conservarVideos: true);

        await sut.AplicarTrasVerAsync(Id, 6);

        Enumerable.Range(1, 6).Should().OnlyContain(n => Existe(n), "el anime está protegido");
        VerificarRegistroConservado(); // ningún registro tocado
        _dialogos.Verify(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _dialogos.Verify(d => d.MostrarToast(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task AnimeSinProteger_ConElMismoEscenario_SiBorra()
    {
        // Control: el mismo escenario sin la protección sí borra, así que la prueba anterior no pasa por casualidad.
        var sut = Preparar(ModoEliminarTrasVerValores.Automatico, conArchivo: [1, 2, 3], vistos: [1, 2, 3], conservarVideos: false);

        await sut.AplicarTrasVerAsync(Id, 3);

        Existe(3).Should().BeFalse();
    }
```

(Si `MostrarToast` no tiene esos cuatro parámetros, copiar la firma de `IDialogService`.)

- [ ] **Paso 2: comprobar que fallan**

Run: `dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false --filter "FullyQualifiedName~LimpiadorDeEpisodiosTests"`
Esperado: `AnimeProtegido_NoSeBorraNadaEnNingunModo` falla en los tres modos (el servicio borra); la de control pasa.

- [ ] **Paso 3: salir si el anime está protegido**

En `LimpiadorDeEpisodios.AplicarTrasVerAsync`, después de la línea `if (anime == null || string.IsNullOrWhiteSpace(anime.RutaCarpeta)) return;`:

```csharp
            if (anime.ConservarVideos)
            {
                AppLogger.Debug("LimpiadorDeEpisodios", $"{anime.Titulo} tiene activado 'Conservar los videos': no se borra nada.");
                return;
            }
```

- [ ] **Paso 4: comprobar que pasan**

Compilar la app y correr de nuevo el filtro `LimpiadorDeEpisodiosTests`. Esperado: todas pasan (las anteriores no cambian: `conservarVideos` vale `false` por defecto).

---

## Tarea 3: Interruptor en la ficha, textos y documentación

**Archivos:**
- Modificar: `AnimeLocalTracker/ViewModels/DetalleViewModel.cs` (propiedad junto a `_esFavoritoAnime` línea ~110, carga junto a la línea ~174 y comando junto a `AlternarFavoritoAnimeAsync` línea ~290)
- Modificar: `AnimeLocalTracker/Views/DetalleView.xaml` (menú `HerramientasPopup`, entre "Liberar espacio" y el separador anterior a "Eliminar de la biblioteca")
- Modificar: `AnimeLocalTracker/Services/LocalizationService.cs` (dos diccionarios, tras `Det_LiberarEspacioDescFormato`)
- Modificar: `AnimeLocalTracker.Tests/Services/LocalizationServiceTests.cs` (lista `ClavesTemporadaYEliminarTrasVer`)
- Crear: `AnimeLocalTracker.Tests/ViewModels/DetalleConservarVideosTests.cs`
- Modificar: `docs/investigacion-descarga-masiva-y-eliminar-tras-ver.md` (punto 5)

**Interfaces:**
- Consume: `AnimeItem.ConservarVideos` (Tarea 1).
- Produce: `DetalleViewModel.ConservarVideosAnime` (`bool`, observable) y `AlternarConservarVideosCommand` (generado a partir de `AlternarConservarVideosAsync`).

- [ ] **Paso 1: escribir las pruebas que fallan**

Crear `AnimeLocalTracker.Tests/ViewModels/DetalleConservarVideosTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>Ficha: el interruptor "Conservar los videos" se carga, se guarda y no miente si el guardado falla.</summary>
public class DetalleConservarVideosTests : IDisposable
{
    private readonly Mock<IAnimeTrackingService> _tracking = new();
    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<IFileScannerService> _escaner = new();
    private readonly Mock<IDialogService> _dialogos = new();
    private readonly Mock<IDownloadService> _descargas = new();
    private readonly Mock<IEmisionMonitorService> _monitor = new();
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), $"ficha_conservar_{Guid.NewGuid():N}");

    public DetalleConservarVideosTests() => Directory.CreateDirectory(_carpeta);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(_carpeta, recursive: true); } catch { /* ignore */ }
    }

    private async Task<DetalleViewModel> FichaAsync(bool conservarVideos)
    {
        var anime = new AnimeItem { AniListId = 7, Titulo = "Frieren", RutaCarpeta = _carpeta, Estado = "FINISHED", TotalEpisodios = 1, ConservarVideos = conservarVideos };
        _escaner.Setup(e => e.EscanearEpisodiosAsync(_carpeta)).ReturnsAsync(new List<EpisodioItem>());
        _db.Setup(d => d.ObtenerRegistrosPorAnimeAsync(7)).ReturnsAsync(new List<RegistroEpisodio>());
        double p = 0;
        _descargas.Setup(d => d.EstaDescargando(It.IsAny<int>(), It.IsAny<int>(), out p)).Returns(false);

        var sut = new DetalleViewModel(_tracking.Object, _db.Object, Mock.Of<IAuthService>(), _escaner.Object, _dialogos.Object, _descargas.Object,
            monitorEmision: _monitor.Object);
        await sut.InicializarAsync(anime);
        return sut;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AlAbrirLaFicha_ElInterruptorRefleja_LoGuardado(bool guardado)
    {
        var sut = await FichaAsync(guardado);

        sut.ConservarVideosAnime.Should().Be(guardado);
    }

    [Fact]
    public async Task AlPulsar_CambiaElInterruptor_YGuardaElAnime()
    {
        var sut = await FichaAsync(conservarVideos: false);

        await sut.AlternarConservarVideosCommand.ExecuteAsync(null);

        sut.ConservarVideosAnime.Should().BeTrue();
        _db.Verify(d => d.ActualizarAnimeAsync(It.Is<AnimeItem>(a => a.AniListId == 7 && a.ConservarVideos)), Times.Once);

        await sut.AlternarConservarVideosCommand.ExecuteAsync(null);

        sut.ConservarVideosAnime.Should().BeFalse();
        _db.Verify(d => d.ActualizarAnimeAsync(It.Is<AnimeItem>(a => a.AniListId == 7 && !a.ConservarVideos)), Times.Once);
    }

    [Fact]
    public async Task SiElGuardadoFalla_VuelveAComoEstabaYAvisa()
    {
        var sut = await FichaAsync(conservarVideos: false);
        _db.Setup(d => d.ActualizarAnimeAsync(It.IsAny<AnimeItem>())).ThrowsAsync(new IOException("disco lleno"));

        await sut.AlternarConservarVideosCommand.ExecuteAsync(null);

        sut.ConservarVideosAnime.Should().BeFalse("no se pudo guardar: mostrar 'protegido' sería mentir");
        sut.AnimeSeleccionado!.ConservarVideos.Should().BeFalse();
        _dialogos.Verify(d => d.MostrarDialogoAsync(LocalizationService.T("Det_ConservarVideos"), LocalizationService.T("Det_ConservarVideosErrorMsj"),
            false, It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }
}
```

Añadir a la lista `ClavesTemporadaYEliminarTrasVer` de `LocalizationServiceTests.cs` (después de `"Lim_CompletarTitulo", ... "Lim_LiberadoMsjFormato",`):

```csharp
        "Det_ConservarVideos", "Det_ConservarVideosDesc", "Det_ConservarVideosErrorMsj",
```

- [ ] **Paso 2: comprobar que fallan**

Run: `dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false --filter "FullyQualifiedName~DetalleConservarVideosTests|FullyQualifiedName~LocalizationServiceTests"`
Esperado: error de compilación (`ConservarVideosAnime` y `AlternarConservarVideosCommand` no existen).

- [ ] **Paso 3: añadir las claves de localización**

En `LocalizationService.cs`, justo debajo de la línea `["Det_LiberarEspacioDescFormato"] = "Borra {0} episodios ya vistos y libera {1}",` (diccionario en español):

```csharp
        ["Det_ConservarVideos"] = "Conservar los videos",
        ["Det_ConservarVideosDesc"] = "«Eliminar el video tras verlo» no borrará nada de este anime",
        ["Det_ConservarVideosErrorMsj"] = "No se pudo guardar el cambio; se queda como estaba.",
```

y debajo de `["Det_LiberarEspacioDescFormato"] = "Deletes {0} watched episodes and frees {1}",` (diccionario en inglés):

```csharp
        ["Det_ConservarVideos"] = "Keep the videos",
        ["Det_ConservarVideosDesc"] = "\"Delete the video after watching\" will not delete anything from this anime",
        ["Det_ConservarVideosErrorMsj"] = "The change could not be saved; it stays as it was.",
```

- [ ] **Paso 4: propiedad, carga y comando en la ficha**

En `DetalleViewModel.cs`:

Junto a `[ObservableProperty] private bool _esFavoritoAnime = false;`:

```csharp
    /// <summary>Interruptor "Conservar los videos" del anime abierto (persiste en AnimeItem.ConservarVideos).</summary>
    [ObservableProperty] private bool _conservarVideosAnime;
```

Justo después de `EsFavoritoAnime = anime.EsFavorito;` (línea ~174):

```csharp
        ConservarVideosAnime = anime.ConservarVideos;
```

Justo después del método `AlternarFavoritoAnimeAsync`:

```csharp
    /// <summary>
    /// "Conservar los videos": con la protección activa, "Eliminar el video tras verlo" no borra nada de este anime. Si el guardado
    /// falla el interruptor vuelve a como estaba y se avisa: mostrar "protegido" sin estarlo llevaría a perder videos.
    /// </summary>
    [RelayCommand]
    private async Task AlternarConservarVideosAsync()
    {
        if (AnimeSeleccionado == null) return;

        bool nuevo = !AnimeSeleccionado.ConservarVideos;
        AnimeSeleccionado.ConservarVideos = nuevo;
        ConservarVideosAnime = nuevo;
        try
        {
            await _databaseService.ActualizarAnimeAsync(AnimeSeleccionado);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("DetalleViewModel", $"No se pudo guardar 'Conservar los videos' de {AnimeSeleccionado.Titulo}: {ex.Message}");
            AnimeSeleccionado.ConservarVideos = !nuevo;
            ConservarVideosAnime = !nuevo;
            await _dialogService.MostrarDialogoAsync(LocalizationService.T("Det_ConservarVideos"), LocalizationService.T("Det_ConservarVideosErrorMsj"), false, "AlertCircleOutline", "#F59E0B");
        }
    }
```

- [ ] **Paso 5: el botón en el menú Herramientas**

En `DetalleView.xaml`, insertar este botón justo **antes** de `<Border Height="1" Background="{StaticResource Brush.BorderControl}" Margin="8,4"/>` que precede al botón de `EliminarAnimeActualCommand` (es decir, después del botón de `LiberarEspacioCommand`):

```xml
                                    <Button Command="{Binding AlternarConservarVideosCommand}" Style="{StaticResource DetMenuItem}"
                                            AutomationProperties.Name="{loc:T Det_ConservarVideos}">
                                        <Grid>
                                            <Grid.ColumnDefinitions>
                                                <ColumnDefinition Width="Auto"/>
                                                <ColumnDefinition Width="*"/>
                                                <ColumnDefinition Width="Auto"/>
                                            </Grid.ColumnDefinitions>
                                            <materialDesign:PackIcon Grid.Column="0" Kind="ShieldLockOutline" Width="20" Height="20" Foreground="{StaticResource Brush.Success}" VerticalAlignment="Center" Margin="0,0,12,0"/>
                                            <StackPanel Grid.Column="1" VerticalAlignment="Center">
                                                <TextBlock Text="{loc:T Det_ConservarVideos}" FontSize="13" FontWeight="SemiBold" Foreground="{StaticResource Brush.TextPrimary}"/>
                                                <TextBlock Text="{loc:T Det_ConservarVideosDesc}" FontSize="11" Foreground="{StaticResource Brush.TextTertiary}" TextWrapping="Wrap" Margin="0,2,0,0"/>
                                            </StackPanel>
                                            <materialDesign:PackIcon Grid.Column="2" Kind="Check" Width="18" Height="18" Foreground="{StaticResource Brush.Success}" VerticalAlignment="Center" Margin="12,0,0,0"
                                                                     Visibility="{Binding ConservarVideosAnime, Converter={StaticResource BoolToVis}}"/>
                                        </Grid>
                                    </Button>
```

Si `ShieldLockOutline` no existe en esta versión de `PackIconKind`, la compilación falla con un error de XAML: usar `LockOutline`.

- [ ] **Paso 6: comprobar que pasan**

Compilar la app (0 advertencias) y correr: `dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false --filter "FullyQualifiedName~DetalleConservarVideosTests|FullyQualifiedName~LocalizationServiceTests|FullyQualifiedName~EnlacesDeLaFicha"`
Esperado: todas pasan (`EnlacesDeLaFichaTests` valida por reflexión los enlaces del XAML de la ficha).

- [ ] **Paso 7: actualizar el documento de diseño**

En `docs/investigacion-descarga-masiva-y-eliminar-tras-ver.md`, punto 5, sustituir "Excepciones por anime, " por nada y añadir al final del párrafo: ` Las excepciones por anime se resolvieron con el interruptor "Conservar los videos" de la ficha (docs/plan-conservar-videos.md): con él activo ningún modo borra ni pregunta por ese anime.`

- [ ] **Paso 8: suite completa, una sola vez, y datos reales intactos**

Antes y después, listar la carpeta de datos reales (`find "$LOCALAPPDATA/AnimeLocalTrackerData" -maxdepth 2 -printf '%P|%s|%T@\n'`, ignorando `Logs/` y `biblioteca.db-shm/-wal`) y hacer `diff`; esperado, sin diferencias.
Run: `dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false`
Esperado: 0 fallos (2854 de antes + las pruebas nuevas).

- [ ] **Paso 9: verificación en la app real, con perfil aislado**

Receta de la memoria (`preparar_perfil2.py` con modo `Automatico`, 2 videos, y `ModoEliminarTrasVer` en el `settings.json` del perfil). Con el interruptor ya activo en la base del perfil (`UPDATE AnimeItem SET ConservarVideos = 1 WHERE AniListId = <id>` en la copia): abrir la ficha, comprobar por UIA que el botón "Conservar los videos" existe en Herramientas y que su marca está visible, reproducir un episodio hasta el final, salir y comprobar que el archivo **sigue** en disco y que el log dice "tiene activado 'Conservar los videos'". Después apagar el interruptor desde el propio menú, repetir con el otro episodio y comprobar que ahora **sí** se borra. Cerrar solo el PID lanzado y borrar el perfil temporal. Si algo no se pudo comprobar, decirlo en el informe.

- [ ] **Paso 10: dejar el trabajo sin commit**

`git status` debe mostrar solo los archivos de la tabla de estructura. No hacer commit ni push: el usuario decide cuándo.

---

## Autorrevisión del plan contra el diseño

- **Interruptor en la ficha, que protege de los tres modos y no pregunta** → Tareas 2 y 3. **Persistencia por anime** → Tarea 1. **Migración para bases existentes** → Tarea 1. **Importar no quita la protección** → Tarea 1. **Fallo de guardado** → Tarea 3.
- **Fuera de este plan:** proteger de "Liberar espacio" o del borrado manual (son acciones explícitas del usuario), un ajuste global de "preguntar siempre", "recordar Conservar" en el diálogo (lo cubre el interruptor), y arreglar la miniatura huérfana o las filas duplicadas de episodio (otro tema).
- **No cubierto por pruebas unitarias:** el aspecto del botón en el menú y que el reproductor real respete la protección (Paso 9, perfil aislado).
