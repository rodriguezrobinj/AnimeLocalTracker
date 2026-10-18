# Orden de visionado de la franquicia (ficha): diseño y plan

Fecha: 2026-10-18. Segunda de las cuatro funciones pedidas ese día (la primera, el desfase de audio/subtítulos, ya está en `6ac2c4b`).

## Objetivo

En sagas largas (Monogatari, Fate, Shingeki no Kyojin, Sword Art Online) saber en qué orden ver temporadas, películas y OVAs es un laberinto. Desde la ficha de cualquier parte, una ventana "Orden de visionado" dibuja la saga a partir de las relaciones oficiales de AniList: qué partes ya viste, cuáles tienes descargadas, cuáles no tienes, y cuál es exactamente la siguiente que toca ver.

Ejemplo: en la ficha de *Shingeki no Kyojin Season 2* se abre la ventana y se ve la temporada 1 con ✔ y su OVA colgada debajo, la temporada 2 resaltada ("Estás aquí"), la 3 con ⬇ y el distintivo "Siguiente", y *Shingeki! Kyojin Chuugakkou* en el grupo "Aparte".

## Decisiones tomadas (con el usuario)

- **Ubicación:** ventana encima de la ficha, como la de Openings & Endings (se construye la primera vez que se abre). No cambia la distribución de la ficha (el usuario ya rechazó cambiarla). Descartadas: sección fija bajo "Parecidos" (255 px, no cabe una saga larga) y pestañas en la ficha.
- **Orden:** línea principal + extras. La columna vertebral es la cadena de precuelas/secuelas; películas, OVAs e historias paralelas cuelgan bajo la parte de la que salen; spin-offs y versiones alternativas, en un grupo "Aparte". Descartados: todo por fecha de estreno (mezcla recopilatorios y spin-offs con la historia) y solo la línea principal (deja fuera películas canónicas que no son secuela).
- **Acción por fila:** si está en la biblioteca, abre su ficha; si no, botón "Añadir a la biblioteca" (mismo alta que la pestaña Agregar).
- **Datos:** el mapa de relaciones que ya guarda `FranquiciaService` + una copia local de los datos de las partes que no están en la biblioteca, refrescada cada 30 días (offline-first). Descartados: consultar siempre en vivo (sin red no se ve nada) y recorrer la saga en vivo en cada apertura (lento, gasta cupo de AniList).

## Lo que ya existe y se reutiliza

- `RelacionAnime` / `RelacionAnimeSync` (migración v6) con el grafo: en la base real del usuario, 182 PREQUEL, 174 SEQUEL, 149 PARENT, 121 SIDE_STORY, 75 CHARACTER, 56 OTHER, 36 ALTERNATIVE, 16 SUMMARY, 14 SPIN_OFF; 247 animes relacionados fuera de la biblioteca (sin título ni portada guardados).
- `FranquiciaService.SincronizarAsync(ids)`: consulta las relaciones que falten o hayan caducado (30 días) y explora hasta 4 saltos y 400 nodos externos, siguiendo PREQUEL, SEQUEL, PARENT, SIDE_STORY, SPIN_OFF, SUMMARY y ALTERNATIVE. Tiene candado y espera de 5 min tras un fallo. Hoy solo la llama Estadísticas.
- `IAnimeTrackingService.ObtenerAnimesPorIdsLoteAsync(ids)`: lotes de 50 por `id_in` con título, portada, episodios, estado y `startDate`. **Le falta `format`**: se añade a su consulta (un campo; `AniListMedia.Format` ya existe).
- `IAnimeTrackingService.ObtenerAnimePorIdAsync(id)` + `AnimeLibraryService.CrearYGuardarAnimeAsync(media, titulo)`: el alta de la pestaña Agregar.
- `AnimeItem.OrdenEstreno` (año × 10 + temporada), `IDatabaseService.ObtenerEpisodiosVistosPorAnimeAsync` y `ObtenerTodosLosRegistrosAsync` (archivos en disco).
- Patrón de ventana encima: `AnfitrionMusica` / `PanelMusicaView` en `DetalleView` (se crea la primera vez).

## Qué se ve

- **Chip "Orden de visionado"** en la fila de chips de la ficha (junto a Tráiler y Openings & Endings), con icono, `ToolTip` y `AutomationProperties.Name`. Visible si el anime tiene alguna relación de franquicia conocida **o** si sus relaciones aún no se han consultado nunca (anime recién añadido). Si al abrir resulta que no tiene ninguna, la ventana muestra un estado vacío: "Este anime no tiene precuelas, secuelas ni extras en AniList".
- **Ventana** (encima de la ficha, botón ✕, `Esc` la cierra, se cierra sola al salir de la ficha vía `IsVisibleChanged` y se reinicia al cambiar de ficha vía `DataContextChanged`, según `ui-wpf-vistas.md`):
  - Lista numerada de la **línea principal**; bajo cada parte, sus extras con sangría (↳).
  - Grupo **"Aparte"** al final (spin-offs y versiones alternativas).
  - Indicador de carga mientras se consulta AniList; lo ya guardado se ve desde el primer momento.
  - Aviso discreto si no hay conexión: "Sin conexión: puede faltar alguna parte".
- **Cada fila:** portada pequeña (marcador de posición debajo de la `Image`), título, formato y año ("TV · 2013", "Película · 2020") y estado:
  - ✔ **Vista**: en la biblioteca y completada (`EstadoUsuario == COMPLETED`, o todos los episodios vistos con total conocido).
  - ◐ **A medias**: algún episodio visto.
  - ⬇ **Descargada**: algún archivo en disco y ningún episodio visto.
  - **En tu biblioteca**: sin archivos ni episodios vistos.
  - **No la tienes**: con botón "Añadir a la biblioteca".
  - Distintivos: **"Estás aquí"** (la de la ficha) y **"Siguiente"**.
- Al abrir, la lista se desplaza hasta "Siguiente" (o hasta "Estás aquí" si no hay siguiente).
- Pulsar una fila que está en la biblioteca abre su ficha (`NavegarMensaje_Detalle`). "Añadir" la da de alta; la fila pasa a "En tu biblioteca"; sin red, aviso de error y la fila no cambia.
- Lista con `ListBox` virtualizado (`ui-wpf-vistas.md` #2), colores de la paleta `Brush.*`, textos ES/EN.

## Cálculo del orden (`Services/Franquicias/OrdenVisionado.cs`, puro)

Entrada: el id del anime de la ficha, las aristas (`RelacionAnime`), y una función que da, para cada id, su fecha de estreno comparable (o ninguna). Salida: la línea principal (cada parte con sus extras) y el grupo "Aparte". "Siguiente" y "Estás aquí" los decide el ViewModel con el estado de cada parte.

1. **Tipos.** Secuencia: PREQUEL, SEQUEL. Extras colgados: SIDE_STORY, SUMMARY, PARENT. Aparte: SPIN_OFF, ALTERNATIVE. Fuera: CHARACTER (cruces: *Isekai Quartet* no mete *Re:Zero* en *Overlord*), OTHER (anuncios, videos musicales) y cualquier tipo desconocido.
2. **Punto de partida.** Si el anime tiene alguna arista de secuencia, la línea es la suya. Si no, pero tiene PARENT o SIDE_STORY hacia una parte con secuencia, se usa la línea de esa parte (abrir desde una OVA muestra su saga con la OVA colgada en su sitio). Si no hay ninguna, la línea es solo el propio anime.
3. **Línea principal.** Componente conexo por aristas de secuencia, en las dos direcciones, con un tope de 60 partes. Orden topológico (lo que es precuela de algo va antes; las aristas se leen desde los dos lados, porque A→SEQUEL→B equivale a B→PREQUEL→A); los empates y las ramas (dos secuelas, como en Monogatari) se ordenan por fecha de estreno y, sin fecha, por id. Si hay un ciclo o datos contradictorios, las partes del ciclo se ordenan por fecha y nada se cuelga ni se pierde.
4. **Extras.** Cada nodo unido a una parte de la línea por SIDE_STORY, SUMMARY o PARENT (y que no esté en la línea) se cuelga bajo esa parte, por fecha. Si se une a varias, va bajo la última de la línea. Un nivel: los extras de un extra no se recorren.
5. **Aparte.** Nodos unidos a cualquier parte de la línea por SPIN_OFF o ALTERNATIVE, sin repetir ni incluir lo que ya está en la línea o en los extras, por fecha. Un nivel.
6. **Fecha comparable.** La de `AnimeRelacionadoInfo` si la hay (`startDate` de AniList); si no, `OrdenEstreno` de la biblioteca. Las dos se reducen a la escala de `OrdenEstreno`: año × 10 + temporada (1 invierno … 4 otoño); para AniList la temporada sale del mes, `(mes − 1) / 3 + 1`, como cuenta AniList sus temporadas (invierno = enero–marzo); año sin mes = año × 10. Sin fecha (0), al final de su grupo.
7. **Siguiente** (en el ViewModel): la primera parte de la línea principal que no está "Vista". Las películas que AniList encadena como SEQUEL forman parte de la línea y pueden ser "Siguiente"; los extras y "Aparte" nunca. Si todo está visto, no se marca nada.

## Datos

- **Tabla `AnimeRelacionadoInfo`** (migración **v27**, tabla nueva): `AniListId` (clave primaria), `Titulo`, `Formato`, `Anio`, `Mes`, `Dia` (nullables), `Episodios` (nullable), `UrlPortada`, `FechaUtc` (UTC; se trata como UTC al leer). Guarda **todas** las partes de la saga, también las de la biblioteca: `AnimeItem` no tiene formato ni fecha exacta (solo `DatosExtraAnime`, y solo de fichas ya abiertas). Para una parte de la biblioteca, título, portada y estado salen de la biblioteca; de esta tabla solo el formato y la fecha. `VaciarBibliotecaAsync` la vacía. Métodos: `ObtenerRelacionadosInfoAsync()` (la tabla entera: cientos de filas como mucho) y `GuardarRelacionadosInfoAsync(IEnumerable<AnimeRelacionadoInfo>)`, por `EscribirAsync`/`_lectura` (`persistence.md` #7). La copia de seguridad JSON no la incluye (se regenera sola).
- **Al abrir la ventana** (`OrdenVisionadoService.CargarAsync(aniListId, ct)`, nunca lanza):
  1. Lee relaciones y sincronizaciones. Si este anime no tiene `RelacionAnimeSync` o caducó, llama a `IFranquiciaService.SincronizarAsync([id])` (la exploración y sus topes ya están ahí).
  2. Calcula el orden con `OrdenVisionado`.
  3. Para las partes sin datos en la tabla o con datos de más de 30 días, una llamada a `ObtenerAnimesPorIdsLoteAsync` (lotes de 50) y guarda lo recibido.
  4. Devuelve las filas con su estado: biblioteca (`ObtenerTodosLosAnimesAsync`, vistos por anime, archivos en disco) o datos guardados.
  - La primera pasada (lo que ya hay guardado) se puede pintar antes de los pasos 1 y 3; si estos traen algo nuevo, se vuelve a pintar.
- **Añadir:** `ObtenerAnimePorIdAsync(id)` → `CrearYGuardarAnimeAsync(media, titulo)`; tras el alta la fila de `AnimeRelacionadoInfo` se conserva (sigue dando formato y fecha); título, portada y estado pasan a salir de la biblioteca.

## Fallos

- Sin conexión o AniList caído: se muestra lo guardado con el aviso; una parte sin datos aparece como "Parte sin datos todavía". No se reintenta en bucle (la espera de 5 min de `FranquiciaService`; la consulta de datos solo se repite al volver a abrir la ventana).
- Se cambia de ficha o se cierra la ventana mientras carga: cancelación por `CancellationToken` y versión; lo viejo no se pinta.
- Portada que no carga: marcador de posición.
- Errores registrados con `AppLogger` (nada de `Debug.WriteLine`).

## Pruebas

- `OrdenVisionadoTests` (puro): cadena tipo Shingeki con OVA colgada y spin-off en "Aparte"; dos secuelas tipo Monogatari ordenadas por fecha; abrir desde una OVA (PARENT) da la línea del padre; ciclo PREQUEL/SEQUEL sin perder partes; CHARACTER y OTHER excluidos; tope de 60; extra unido a dos partes va bajo la última; sin relaciones devuelve solo el anime.
- `DatabaseServiceRelacionadoInfoTests`: guardar y leer, sustituir, `VaciarBiblioteca`, migración sobre una base en v26 (patrón "base vieja" de `migracion-db`).
- `AniListTrackingService`: la consulta por lotes pide `format` y se deserializa (respuesta de ejemplo).
- `OrdenVisionadoServiceTests` (Moq): sincroniza solo si falta o caducó; pide datos solo de lo que falta o caducó; sin red devuelve lo guardado; estados Vista / A medias / Descargada / En tu biblioteca / No la tienes.
- ViewModel: "Siguiente" y "Estás aquí"; añadir con éxito y con error; cambiar de ficha mientras carga no pinta lo viejo.
- Localización: `LocalizationServiceTests` y `buscar_literales.py` sobre lo tocado.
- En vivo (perfil aislado **con red**, `perfil-aislado`): la migración v27 sobre una copia de la base real; abrir la ventana en *Kage no Jitsuryokusha ni Naritakute!* y en una saga larga de la biblioteca; sin red (relanzar sin `--con-red`) se ve lo guardado; "Añadir" una parte y comprobar que aparece en la Galería.

## Fuera de alcance

- Cronología interna de la historia distinta del orden de estreno (p. ej. *Fate/Zero* antes que *Fate/stay night*): AniList no la da.
- Recorrer las temporadas propias de un spin-off dentro de la ventana (se ven abriendo su ficha).
- Marcar como visto o descargar desde la ventana.
- Mostrar la franquicia en la Galería o en Estadísticas.

---

# Plan de implementación

> **Ejecución:** en esta sesión, tarea a tarea, con `superpowers:executing-plans` (las reglas del repo no permiten subagentes salvo que el usuario los pida). Los pasos usan casillas (`- [ ]`). **Sin commits por tarea**: el repo solo hace commit cuando el usuario lo pide (skill `commit-es`); al final se propone un único commit.

**Objetivo:** ventana "Orden de visionado" en la ficha que dibuja la saga (línea principal + extras + aparte) con el estado de cada parte y la siguiente que toca ver.

**Arquitectura:** cálculo puro `OrdenVisionado` (grafo → orden) + `OrdenVisionadoService` (base de datos, `FranquiciaService` y AniList → partes con estado) + `OrdenVisionadoFichaViewModel` (pieza de la ficha, como `Musica`) + `OrdenVisionadoView` (ventana encima, creada la primera vez). Datos de AniList en la tabla nueva `AnimeRelacionadoInfo` (v27).

**Tecnología:** .NET 8, WPF, CommunityToolkit.Mvvm, sqlite-net, xUnit + FluentAssertions + Moq, MaterialDesignThemes.

**Especificación:** la primera mitad de este documento.

## Restricciones globales

- Comandos asíncronos devuelven `Task`; nada de `async void` salvo manejadores de la vista con `try-catch` (`wpf-mvvm.md` #3).
- Escrituras de la base por `EscribirAsync`; lecturas por `_lectura` (`persistence.md` #7). Fechas en UTC con `DateTime.UtcNow`.
- Esquema solo con la lista `Migraciones` (v27), skill `migracion-db`.
- Todo texto visible con `{loc:T Clave}` / `LocalizationService.T("Clave")`, claves en las dos mitades de `LocalizationService` (skill `localizacion`).
- Colores solo de la paleta `Brush.*`; botones solo-icono con `ToolTip` + `AutomationProperties.Name` + `Cursor="Hand"`.
- Lista con `ListBox` virtualizado y plantillas implícitas por tipo (`ui-wpf-vistas.md` #2); portada con marcador de posición debajo de la `Image` y sin `IsAsync`.
- Registro con `AppLogger`; nada de `Debug.WriteLine`.
- Las pruebas nunca tocan `AppDataPaths` (carpetas temporales).
- Compilación con 0 advertencias; `AnimeLocalTracker.Benchmarks` debe compilar si cambia `IDatabaseService` (skill `repo-build-test`).

## Focos de revisión

1. **Saga enorme (Fate, Gundam):** con cientos de relaciones la ventana no se cuelga y la línea se corta a 60 partes, quedándose con las más cercanas → `TopeDeSesenta_SeQuedaConLasMasCercanas` (tarea 2).
2. **Datos contradictorios de AniList** (A secuela de B y B secuela de A, o una sola dirección guardada): ninguna parte se pierde ni se repite → `CicloDeSecuelas_NoPierdeNiRepite` y `SoloPrecuela_SeLeeDesdeElOtroLado` (tarea 2).
3. **Sin conexión la primera vez** (anime recién añadido, relaciones nunca consultadas): la ventana no se queda cargando y avisa de que falta información → `SinRedYSinRelacionesConsultadas_AvisaSinConexion` (tarea 3).
4. **"Añadir" dos veces, o sobre una parte que ya se añadió desde otra pantalla:** no se duplica ni rompe; la fila acaba "En tu biblioteca" → `AnadirLoQueYaEstaba_DevuelveElDeLaBiblioteca` (tarea 3) y `Anadir_MientrasAnade_NoRepite` (tarea 4).
5. **Cerrar la ventana o salir de la ficha mientras carga:** la respuesta tardía no repinta una ventana cerrada → `CerrarMientrasCarga_NoPintaLoQueLlegaDespues` (tarea 4).

Comandos que se repiten (skill `repo-build-test`):
- Compilar la app: `dotnet build AnimeLocalTracker/AnimeLocalTracker.csproj -c Debug -nodeReuse:false`
- Pruebas filtradas: `dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false -nodeReuse:false --filter "<filtro>"`
- Benchmarks: `dotnet build AnimeLocalTracker.Benchmarks/AnimeLocalTracker.Benchmarks.csproj -c Debug -t:Compile -p:BuildProjectReferences=false -nodeReuse:false`

---

## Tarea 1: datos (tabla v27, `format` en la consulta por lotes)

**Archivos:**
- Crear: `AnimeLocalTracker/Models/AnimeRelacionadoInfo.cs`
- Modificar: `AnimeLocalTracker/Services/DatabaseService.cs` (lista `Migraciones`, `VaciarBibliotecaAsync`, métodos nuevos tras `ObtenerRelacionesSincronizadasAsync`)
- Modificar: `AnimeLocalTracker/Services/IDatabaseService.cs` (tras `GuardarRelacionesAnimeAsync`)
- Modificar: `AnimeLocalTracker.Benchmarks/ReproductorBenchmarks.cs` (`DummyDatabaseService`)
- Modificar: `AnimeLocalTracker/Services/AniListTrackingService.cs` (consulta de `ObtenerAnimesPorIdsLoteAsync`)
- Pruebas: crear `AnimeLocalTracker.Tests/Services/DatabaseServiceRelacionadoInfoTests.cs`; ampliar `AnimeLocalTracker.Tests/Services/AniListRelacionesTests.cs`

**Interfaces:**
- Produce: `AnimeRelacionadoInfo { int AniListId; string Titulo; string Formato; int? Anio; int? Mes; int? Dia; int? Episodios; string UrlPortada; DateTime FechaUtc }`; `IDatabaseService.ObtenerRelacionadosInfoAsync(): Task<List<AnimeRelacionadoInfo>>`; `IDatabaseService.GuardarRelacionadosInfoAsync(IEnumerable<AnimeRelacionadoInfo>): Task`; `AniListMedia.Format` relleno por `ObtenerAnimesPorIdsLoteAsync`.

- [ ] **Paso 1: pruebas de la tabla (no compilan todavía)**

```csharp
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

/// <summary>Migración v27: datos de AniList de las partes de una saga (orden de visionado de la ficha).</summary>
public class DatabaseServiceRelacionadoInfoTests : IDisposable
{
    private readonly string _rutaDb = Path.Combine(Path.GetTempPath(), $"AnimeTracker_RelInfo_{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        SQLiteAsyncConnection.ResetPool();
        foreach (var sufijo in new[] { "", "-wal", "-shm" })
        {
            try { if (File.Exists(_rutaDb + sufijo)) File.Delete(_rutaDb + sufijo); } catch { /* ignore */ }
        }
    }

    private static AnimeRelacionadoInfo Info(int id, string titulo) =>
        new() { AniListId = id, Titulo = titulo, Formato = "MOVIE", Anio = 2020, Mes = 10, Episodios = 1, UrlPortada = "https://x/y.jpg", FechaUtc = DateTime.UtcNow };

    [Fact]
    public async Task GuardarYLeer_SustituyeLaDelMismoAnime()
    {
        using var sut = new DatabaseService(_rutaDb);
        await sut.InicializarBaseDatosAsync();

        await sut.GuardarRelacionadosInfoAsync(new[] { Info(1, "Viejo"), Info(2, "Otro") });
        await sut.GuardarRelacionadosInfoAsync(new[] { Info(1, "Nuevo") });

        var todas = await sut.ObtenerRelacionadosInfoAsync();
        todas.Should().HaveCount(2);
        todas.Single(i => i.AniListId == 1).Titulo.Should().Be("Nuevo");
        todas.Single(i => i.AniListId == 1).Mes.Should().Be(10);
    }

    [Fact]
    public async Task VaciarBiblioteca_TambienLaVacia()
    {
        using var sut = new DatabaseService(_rutaDb);
        await sut.InicializarBaseDatosAsync();
        await sut.GuardarRelacionadosInfoAsync(new[] { Info(1, "X") });

        await sut.VaciarBibliotecaAsync();

        (await sut.ObtenerRelacionadosInfoAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task BaseEnV26SinLaTabla_LaMigracionLaCreaYConservaLosAnimes()
    {
        using (var sut = new DatabaseService(_rutaDb))
        {
            await sut.InicializarBaseDatosAsync();
            await sut.GuardarAnimeAsync(new AnimeItem { AniListId = 1, Titulo = "De antes" });
        }
        SQLiteAsyncConnection.ResetPool();

        using (var vieja = new SQLiteConnection(_rutaDb))
        {
            vieja.Execute("DROP TABLE AnimeRelacionadoInfo;");
            vieja.Execute("PRAGMA user_version = 26;");
        }

        using (var reabierta = new DatabaseService(_rutaDb))
        {
            await reabierta.InicializarBaseDatosAsync();
            (await reabierta.ObtenerAnimePorIdAsync(1))!.Titulo.Should().Be("De antes");
            await reabierta.GuardarRelacionadosInfoAsync(new[] { Info(5, "Película") });
        }
        SQLiteAsyncConnection.ResetPool();

        // Reabrir otra vez no cambia nada.
        using var otraVez = new DatabaseService(_rutaDb);
        await otraVez.InicializarBaseDatosAsync();
        (await otraVez.ObtenerRelacionadosInfoAsync()).Should().ContainSingle(i => i.AniListId == 5);
        using var conexion = new SQLiteConnection(_rutaDb);
        conexion.ExecuteScalar<int>("PRAGMA user_version;").Should().BeGreaterThanOrEqualTo(27);
    }
}
```

- [ ] **Paso 2: prueba de `format` en la consulta por lotes** (al final de `AniListRelacionesTests`, que ya tiene `Responder` y `_peticiones`)

```csharp
    [Fact]
    public async Task ConsultaPorLotes_PideElFormato_YLoDevuelve()
    {
        Responder(HttpStatusCode.OK, @"{ ""data"": { ""Page"": { ""media"": [
            { ""id"": 21, ""title"": { ""romaji"": ""Mugen Ressha-hen"" }, ""format"": ""MOVIE"",
              ""startDate"": { ""year"": 2020, ""month"": 10, ""day"": 16 }, ""coverImage"": { ""extraLarge"": ""https://img/21.jpg"" } }
        ] } } }");

        var resultado = await _sut.ObtenerAnimesPorIdsLoteAsync(new[] { 21 });

        _peticiones.Should().ContainSingle().Which.Should().Contain("format");
        resultado[21].Format.Should().Be("MOVIE");
        resultado[21].StartDate!.Month.Should().Be(10);
    }
```

- [ ] **Paso 3: comprobar que fallan** — compilar la app y las pruebas filtradas con `"FullyQualifiedName~RelacionadoInfo|FullyQualifiedName~AniListRelacionesTests"`. Esperado: error de compilación (`AnimeRelacionadoInfo` no existe).

- [ ] **Paso 4: modelo `AnimeLocalTracker/Models/AnimeRelacionadoInfo.cs`**

```csharp
using System;
using SQLite;

namespace AnimeLocalTracker.Models;

/// <summary>
/// Datos de AniList de una parte de una saga (orden de visionado de la ficha): título, formato, fecha de estreno y portada. Guarda
/// todas las partes, también las de la biblioteca (de esas solo se usan formato y fecha: <see cref="AnimeItem"/> no los tiene).
/// Se refresca cada 30 días. Tabla creada por la migración v27.
/// </summary>
public class AnimeRelacionadoInfo
{
    [PrimaryKey]
    public int AniListId { get; set; }

    public string Titulo { get; set; } = string.Empty;

    /// <summary>Formato de AniList: TV, TV_SHORT, MOVIE, SPECIAL, OVA, ONA, MUSIC (vacío si no se sabe).</summary>
    public string Formato { get; set; } = string.Empty;

    public int? Anio { get; set; }
    public int? Mes { get; set; }
    public int? Dia { get; set; }
    public int? Episodios { get; set; }
    public string UrlPortada { get; set; } = string.Empty;

    /// <summary>Cuándo se consultó (UTC; sqlite-net la devuelve con Kind Unspecified: tratarla como UTC).</summary>
    public DateTime FechaUtc { get; set; }
}
```

- [ ] **Paso 5: migración, vaciado y métodos en `DatabaseService.cs`**

En `Migraciones`, la entrada v26 ya existe: ponerle coma final y añadir tras ella

```csharp
        // v27: tabla nueva (clave = AniListId; se lee entera, sin índice). Empieza vacía: se llena al abrir el orden de visionado.
        (27, "datos de AniList de las partes de una saga (orden de visionado de la ficha)", CrearTablaRelacionadoInfoAsync)
```

y, junto a `CrearTablaDesfaseEpisodioAsync`:

```csharp
    private static Task CrearTablaRelacionadoInfoAsync(SQLiteAsyncConnection conexion) => conexion.CreateTableAsync<AnimeRelacionadoInfo>();
```

En `VaciarBibliotecaAsync`, tras `db.Execute("DELETE FROM DesfaseEpisodio;");`:

```csharp
            db.Execute("DELETE FROM AnimeRelacionadoInfo;");
```

Tras `ObtenerRelacionesSincronizadasAsync`:

```csharp
    public async Task<List<AnimeRelacionadoInfo>> ObtenerRelacionadosInfoAsync()
    {
        return await _lectura.Table<AnimeRelacionadoInfo>().ToListAsync();
    }

    public async Task GuardarRelacionadosInfoAsync(IEnumerable<AnimeRelacionadoInfo> infos)
    {
        var lista = infos?.Where(i => i.AniListId > 0).ToList() ?? new List<AnimeRelacionadoInfo>();
        if (lista.Count == 0) return;
        await EscribirAsync(db =>
        {
            foreach (var info in lista) db.InsertOrReplace(info);
        });
    }
```

- [ ] **Paso 6: interfaz y doble de benchmarks**

`IDatabaseService.cs`, tras `Task GuardarRelacionesAnimeAsync(...)`:

```csharp
    /// <summary>Datos de AniList guardados de las partes de sagas (orden de visionado de la ficha). La tabla entera.</summary>
    Task<List<AnimeRelacionadoInfo>> ObtenerRelacionadosInfoAsync();
    /// <summary>Guarda (o sustituye) los datos de esas partes.</summary>
    Task GuardarRelacionadosInfoAsync(IEnumerable<AnimeRelacionadoInfo> infos);
```

`DummyDatabaseService` en `ReproductorBenchmarks.cs`:

```csharp
        public Task<List<AnimeRelacionadoInfo>> ObtenerRelacionadosInfoAsync() => Task.FromResult(new List<AnimeRelacionadoInfo>());
        public Task GuardarRelacionadosInfoAsync(IEnumerable<AnimeRelacionadoInfo> infos) => Task.CompletedTask;
```

- [ ] **Paso 7: `format` en la consulta de `ObtenerAnimesPorIdsLoteAsync`**, tras la línea `episodes`:

```
                            format
```

- [ ] **Paso 8: compilar app y benchmarks y correr las pruebas del paso 3.** Esperado: 0 advertencias; 4 pruebas en verde.

---

## Tarea 2: cálculo puro del orden

**Archivos:**
- Crear: `AnimeLocalTracker/Services/Franquicias/OrdenVisionado.cs`
- Pruebas: crear `AnimeLocalTracker.Tests/Services/OrdenVisionadoTests.cs`

**Interfaces:**
- Consume: `RelacionAnime { int AnimeId; int RelacionadoId; string Tipo }`.
- Produce:
  - `record ParteLineaPrincipal(int AniListId, IReadOnlyList<int> Extras)`
  - `record ResultadoOrdenVisionado(IReadOnlyList<ParteLineaPrincipal> LineaPrincipal, IReadOnlyList<int> Aparte)`
  - `static class OrdenVisionado { const int MaximoPartes = 60; bool CuentaParaElOrden(string? tipo); int FechaComparable(int? anio, int? mes); ResultadoOrdenVisionado Construir(int aniListId, IEnumerable<RelacionAnime> aristas, Func<int, int> fecha) }`

- [ ] **Paso 1: pruebas (no compilan todavía)**

```csharp
#pragma warning disable CA1861 // Datos constantes de prueba
using System;
using System.Collections.Generic;
using System.Linq;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services.Franquicias;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>Orden de visionado: línea principal por precuelas/secuelas, extras colgados y spin-offs aparte.</summary>
public class OrdenVisionadoTests
{
    private static RelacionAnime R(int a, int b, string tipo) => new() { AnimeId = a, RelacionadoId = b, Tipo = tipo };

    private static Func<int, int> Fechas(Dictionary<int, int> fechas) => id => fechas.GetValueOrDefault(id);

    private static int[] Linea(ResultadoOrdenVisionado r) => r.LineaPrincipal.Select(p => p.AniListId).ToArray();

    // Shingeki: 1 (T1, 2013) → 2 (T2, 2017) → 3 (T3, 2018); 10 = OVA de la T1; 20 = spin-off (Chuugakkou).
    private static readonly RelacionAnime[] Shingeki =
    {
        R(1, 2, "SEQUEL"), R(2, 1, "PREQUEL"), R(2, 3, "SEQUEL"), R(3, 2, "PREQUEL"),
        R(1, 10, "SIDE_STORY"), R(10, 1, "PARENT"), R(1, 20, "SPIN_OFF"),
    };
    private static readonly Dictionary<int, int> FechasShingeki = new() { [1] = 20132, [2] = 20172, [3] = 20183, [10] = 20134, [20] = 20154 };

    [Fact]
    public void Saga_LineaPrincipal_ExtraColgado_YSpinOffAparte()
    {
        var r = OrdenVisionado.Construir(2, Shingeki, Fechas(FechasShingeki));

        Linea(r).Should().Equal(1, 2, 3);
        r.LineaPrincipal[0].Extras.Should().Equal(10);
        r.LineaPrincipal[1].Extras.Should().BeEmpty();
        r.Aparte.Should().Equal(20);
    }

    [Fact]
    public void AbrirDesdeLaOva_DaLaLineaDeSuPadre()
    {
        var r = OrdenVisionado.Construir(10, Shingeki, Fechas(FechasShingeki));

        Linea(r).Should().Equal(1, 2, 3);
        r.LineaPrincipal[0].Extras.Should().Equal(10);
    }

    [Fact]
    public void DosSecuelas_SeOrdenanPorFecha()
    {
        var aristas = new[] { R(1, 3, "SEQUEL"), R(1, 2, "SEQUEL") };
        var r = OrdenVisionado.Construir(1, aristas, Fechas(new() { [1] = 20093, [2] = 20101, [3] = 20121 }));

        Linea(r).Should().Equal(1, 2, 3);
    }

    [Fact]
    public void SoloPrecuela_SeLeeDesdeElOtroLado()
    {
        var r = OrdenVisionado.Construir(1, new[] { R(2, 1, "PREQUEL") }, Fechas(new()));

        Linea(r).Should().Equal(1, 2);
    }

    [Fact]
    public void CicloDeSecuelas_NoPierdeNiRepite()
    {
        var aristas = new[] { R(1, 2, "SEQUEL"), R(2, 1, "SEQUEL") };
        var r = OrdenVisionado.Construir(2, aristas, Fechas(new() { [1] = 20101, [2] = 20121 }));

        Linea(r).Should().Equal(1, 2);
    }

    [Fact]
    public void SinFecha_VaAlFinalDeSuGrupo()
    {
        var aristas = new[] { R(1, 2, "SEQUEL"), R(1, 3, "SEQUEL") };
        var r = OrdenVisionado.Construir(1, aristas, Fechas(new() { [1] = 20101, [3] = 20111 }));

        Linea(r).Should().Equal(1, 3, 2);
    }

    [Fact]
    public void CruceDePersonajesYOtros_QuedanFuera()
    {
        var aristas = new[] { R(1, 2, "SEQUEL"), R(1, 50, "CHARACTER"), R(1, 51, "OTHER"), R(1, 52, "ADAPTATION") };
        var r = OrdenVisionado.Construir(1, aristas, Fechas(new()));

        Linea(r).Should().Equal(1, 2);
        r.LineaPrincipal.SelectMany(p => p.Extras).Should().BeEmpty();
        r.Aparte.Should().BeEmpty();
    }

    [Fact]
    public void ExtraUnidoADosPartes_VaBajoLaUltima()
    {
        var aristas = new[] { R(1, 2, "SEQUEL"), R(30, 1, "PARENT"), R(30, 2, "PARENT") };
        var r = OrdenVisionado.Construir(1, aristas, Fechas(new() { [1] = 20101, [2] = 20111, [30] = 20112 }));

        r.LineaPrincipal[0].Extras.Should().BeEmpty();
        r.LineaPrincipal[1].Extras.Should().Equal(30);
    }

    [Fact]
    public void TopeDeSesenta_SeQuedaConLasMasCercanas()
    {
        var aristas = Enumerable.Range(1, 99).Select(i => R(i, i + 1, "SEQUEL")).ToArray();
        var r = OrdenVisionado.Construir(1, aristas, Fechas(new()));

        Linea(r).Should().HaveCount(OrdenVisionado.MaximoPartes).And.OnlyHaveUniqueItems();
        Linea(r).Should().StartWith(new[] { 1, 2, 3 });
        Linea(r).Max().Should().Be(OrdenVisionado.MaximoPartes);
    }

    [Fact]
    public void SinRelaciones_SoloElPropioAnime()
    {
        var r = OrdenVisionado.Construir(7, Array.Empty<RelacionAnime>(), Fechas(new()));

        Linea(r).Should().Equal(7);
        r.Aparte.Should().BeEmpty();
    }

    [Fact]
    public void UnExtraQueTambienEsSpinOff_NoSeRepite()
    {
        var aristas = new[] { R(1, 2, "SEQUEL"), R(1, 40, "SIDE_STORY"), R(2, 40, "SPIN_OFF") };
        var r = OrdenVisionado.Construir(1, aristas, Fechas(new()));

        r.LineaPrincipal.SelectMany(p => p.Extras).Should().Equal(40);
        r.Aparte.Should().BeEmpty();
    }

    [Theory]
    [InlineData(2013, 4, 20132)]
    [InlineData(2013, 1, 20131)]
    [InlineData(2013, 12, 20134)]
    [InlineData(2013, null, 20130)]
    [InlineData(null, 5, 0)]
    [InlineData(0, 5, 0)]
    public void FechaComparable_UsaLaEscalaDeOrdenEstreno(int? anio, int? mes, int esperado)
    {
        OrdenVisionado.FechaComparable(anio, mes).Should().Be(esperado);
    }
}
```

- [ ] **Paso 2: comprobar que fallan** — filtro `"FullyQualifiedName~OrdenVisionadoTests"`. Esperado: error de compilación (`OrdenVisionado` no existe).

- [ ] **Paso 3: implementación `AnimeLocalTracker/Services/Franquicias/OrdenVisionado.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services.Franquicias;

/// <summary>Una parte de la línea principal de una saga con los extras (películas, OVAs, resúmenes) que cuelgan de ella.</summary>
public sealed record ParteLineaPrincipal(int AniListId, IReadOnlyList<int> Extras);

/// <summary>Orden de visionado de una saga: la línea principal y, aparte, spin-offs y versiones alternativas.</summary>
public sealed record ResultadoOrdenVisionado(IReadOnlyList<ParteLineaPrincipal> LineaPrincipal, IReadOnlyList<int> Aparte);

/// <summary>
/// Orden de visionado a partir de las relaciones de AniList (ver docs/plan-orden-visionado-franquicia.md). Cálculo puro, sin red ni
/// base de datos. La línea principal sigue la cadena de precuelas y secuelas; los extras cuelgan de la parte con la que se relacionan;
/// spin-offs y versiones alternativas van aparte. Los cruces de personajes (CHARACTER) y lo marcado como OTHER no cuentan.
/// </summary>
public static class OrdenVisionado
{
    /// <summary>Tope de partes de la línea principal: con más, se quedan las más cercanas al anime de la ficha.</summary>
    public const int MaximoPartes = 60;

    private static readonly HashSet<string> Secuencia = new(StringComparer.OrdinalIgnoreCase) { "PREQUEL", "SEQUEL" };
    private static readonly HashSet<string> Colgados = new(StringComparer.OrdinalIgnoreCase) { "SIDE_STORY", "SUMMARY", "PARENT" };
    private static readonly HashSet<string> DeAparte = new(StringComparer.OrdinalIgnoreCase) { "SPIN_OFF", "ALTERNATIVE" };

    public static bool CuentaParaElOrden(string? tipo) =>
        tipo != null && (Secuencia.Contains(tipo) || Colgados.Contains(tipo) || DeAparte.Contains(tipo));

    /// <summary>
    /// Fecha en la escala de <see cref="AnimeItem.OrdenEstreno"/> (año × 10 + temporada 1–4). La temporada sale del mes como la
    /// cuenta AniList (invierno = enero–marzo). 0 = sin fecha.
    /// </summary>
    public static int FechaComparable(int? anio, int? mes) =>
        anio is not > 0 ? 0 : anio.Value * 10 + (mes is >= 1 and <= 12 ? (mes.Value - 1) / 3 + 1 : 0);

    /// <param name="fecha">Fecha comparable de cada id (<see cref="FechaComparable"/>; 0 = sin fecha, va al final de su grupo).</param>
    public static ResultadoOrdenVisionado Construir(int aniListId, IEnumerable<RelacionAnime> aristas, Func<int, int> fecha)
    {
        var lista = aristas.Where(a => a.AnimeId != a.RelacionadoId && CuentaParaElOrden(a.Tipo)).ToList();
        (int, int) Clave(int id)
        {
            int f = fecha(id);
            return (f > 0 ? f : int.MaxValue, id);
        }

        var vecinosSecuencia = Vecinos(lista, Secuencia);
        var vecinosColgados = Vecinos(lista, Colgados);
        var vecinosAparte = Vecinos(lista, DeAparte);

        // Una OVA o película sin precuela/secuela propia se ve dentro de la saga de su padre.
        int inicio = aniListId;
        if (!vecinosSecuencia.ContainsKey(aniListId))
        {
            int padre = De(vecinosColgados, aniListId).Where(vecinosSecuencia.ContainsKey).OrderBy(Clave).FirstOrDefault();
            if (padre > 0) inicio = padre;
        }

        // Recorrido en anchura desde el inicio: con el tope se quedan las partes más cercanas.
        var linea = new HashSet<int> { inicio };
        var cola = new Queue<int>();
        cola.Enqueue(inicio);
        while (cola.Count > 0 && linea.Count < MaximoPartes)
        {
            foreach (int vecino in De(vecinosSecuencia, cola.Dequeue()).OrderBy(Clave))
            {
                if (linea.Count >= MaximoPartes) break;
                if (linea.Add(vecino)) cola.Enqueue(vecino);
            }
        }

        var ordenLinea = OrdenTopologico(linea, lista, Clave);
        var posicion = ordenLinea.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);

        var extras = ordenLinea.ToDictionary(id => id, _ => new List<int>());
        var colocados = new HashSet<int>(linea);
        foreach (int extra in linea.SelectMany(id => De(vecinosColgados, id)).Where(id => !linea.Contains(id)).Distinct().OrderBy(Clave))
        {
            int duenio = De(vecinosColgados, extra).Where(linea.Contains).OrderByDescending(id => posicion[id]).First();
            extras[duenio].Add(extra);
            colocados.Add(extra);
        }

        var aparte = linea.SelectMany(id => De(vecinosAparte, id)).Distinct().Where(id => !colocados.Contains(id)).OrderBy(Clave).ToList();

        return new ResultadoOrdenVisionado(ordenLinea.Select(id => new ParteLineaPrincipal(id, extras[id])).ToList(), aparte);
    }

    /// <summary>Vecinos por esos tipos, en las dos direcciones (AniList no siempre guarda las dos aristas).</summary>
    private static Dictionary<int, List<int>> Vecinos(List<RelacionAnime> aristas, HashSet<string> tipos)
    {
        var vecinos = new Dictionary<int, List<int>>();
        void Unir(int a, int b)
        {
            if (!vecinos.TryGetValue(a, out var l)) vecinos[a] = l = new List<int>();
            if (!l.Contains(b)) l.Add(b);
        }
        foreach (var a in aristas.Where(a => tipos.Contains(a.Tipo)))
        {
            Unir(a.AnimeId, a.RelacionadoId);
            Unir(a.RelacionadoId, a.AnimeId);
        }
        return vecinos;
    }

    private static IEnumerable<int> De(Dictionary<int, List<int>> vecinos, int id) =>
        vecinos.TryGetValue(id, out var l) ? l : Enumerable.Empty<int>();

    /// <summary>
    /// Lo que es precuela de algo va antes (A SEQUEL B = B PREQUEL A); entre las partes libres, la más temprana. Si los datos forman
    /// un ciclo, se rompe por la parte más temprana: nada se pierde ni se repite. ponytail: O(n²) con n ≤ 60.
    /// </summary>
    private static List<int> OrdenTopologico(HashSet<int> nodos, List<RelacionAnime> aristas, Func<int, (int, int)> clave)
    {
        var despues = nodos.ToDictionary(id => id, _ => new HashSet<int>());
        foreach (var a in aristas)
        {
            if (!nodos.Contains(a.AnimeId) || !nodos.Contains(a.RelacionadoId)) continue;
            if (a.Tipo.Equals("SEQUEL", StringComparison.OrdinalIgnoreCase)) despues[a.AnimeId].Add(a.RelacionadoId);
            else if (a.Tipo.Equals("PREQUEL", StringComparison.OrdinalIgnoreCase)) despues[a.RelacionadoId].Add(a.AnimeId);
        }

        var antesPendientes = nodos.ToDictionary(id => id, _ => 0);
        foreach (var siguientes in despues.Values)
            foreach (int s in siguientes) antesPendientes[s]++;

        var orden = new List<int>();
        var restantes = new HashSet<int>(nodos);
        while (restantes.Count > 0)
        {
            var libres = restantes.Where(id => antesPendientes[id] <= 0).ToList();
            int elegido = (libres.Count > 0 ? libres : restantes.ToList()).OrderBy(clave).First();
            restantes.Remove(elegido);
            orden.Add(elegido);
            foreach (int s in despues[elegido]) antesPendientes[s]--;
        }
        return orden;
    }
}
```

- [ ] **Paso 4: pruebas en verde** con el mismo filtro. Esperado: ninguna roja.

---

## Tarea 3: servicio (datos + estado de cada parte + añadir)

**Archivos:**
- Crear: `AnimeLocalTracker/Services/Franquicias/OrdenVisionadoService.cs`
- Modificar: `AnimeLocalTracker/App.xaml.cs` (registro tras el de `IFranquiciaService`, línea ~308)
- Pruebas: crear `AnimeLocalTracker.Tests/Services/OrdenVisionadoServiceTests.cs`

**Interfaces:**
- Consume: tarea 1 (`ObtenerRelacionadosInfoAsync`, `GuardarRelacionadosInfoAsync`), tarea 2 (`OrdenVisionado.Construir`, `CuentaParaElOrden`, `FechaComparable`), `IFranquiciaService.SincronizarAsync(IEnumerable<int>)`, `IAnimeTrackingService.ObtenerAnimesPorIdsLoteAsync(IEnumerable<int>, string?)`, `IAnimeTrackingService.ObtenerAnimePorIdAsync(int)`, `AnimeLibraryService.CrearYGuardarAnimeAsync(AniListMedia, string)`, `IDatabaseService.ExisteAnimeAsync(int)` (lo usa el alta).
- Produce:
  - `enum EstadoParteSaga { Vista, AMedias, Descargada, EnBiblioteca, NoLaTienes }`
  - `record ParteOrdenVisionado(int AniListId, int Numero, bool EsExtra, bool EsAparte, AnimeItem? Anime, string? Titulo, string Formato, int Anio, string? UrlPortada, EstadoParteSaga Estado)` — `Numero` 1, 2… en la línea principal y 0 en extras/aparte; `Titulo` null = sin datos.
  - `record OrdenVisionadoLeido(IReadOnlyList<ParteOrdenVisionado> Partes, bool SinConexion)`
  - `interface IOrdenVisionadoService { Task<bool> TieneFranquiciaOSinConsultarAsync(int aniListId); Task<OrdenVisionadoLeido> LeerGuardadoAsync(int aniListId); Task<OrdenVisionadoLeido> ActualizarAsync(int aniListId, CancellationToken ct); Task<AnimeItem?> AnadirABibliotecaAsync(int aniListId); }`
  - `static EstadoParteSaga OrdenVisionadoService.EstadoDe(AnimeItem? anime, int vistos, bool enDisco)`

- [ ] **Paso 1: pruebas (no compilan todavía)**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Franquicias;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>Orden de visionado: lo guardado, lo que se pide a AniList, el estado de cada parte y el alta desde la ventana.</summary>
public sealed class OrdenVisionadoServiceTests : IDisposable
{
    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<IFranquiciaService> _franquicias = new();
    private readonly Mock<IAnimeTrackingService> _anilist = new();
    private readonly Mock<ISettingsService> _ajustes = new();
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), "alt_orden_" + Guid.NewGuid().ToString("N"));
    private readonly List<RelacionAnime> _aristas = new();
    private readonly List<RelacionAnimeSync> _sync = new();
    private readonly List<AnimeItem> _animes = new();
    private readonly List<AnimeRelacionadoInfo> _info = new();
    private readonly Dictionary<int, int> _vistos = new();
    private readonly List<RegistroEpisodio> _registros = new();

    public OrdenVisionadoServiceTests()
    {
        Directory.CreateDirectory(_carpeta);
        _ajustes.Setup(s => s.ObtenerRutaBaseAnimes()).Returns(_carpeta);
        _db.Setup(d => d.ObtenerRelacionesAnimeAsync()).ReturnsAsync(() => _aristas.ToList());
        _db.Setup(d => d.ObtenerRelacionesSincronizadasAsync()).ReturnsAsync(() => _sync.ToList());
        _db.Setup(d => d.ObtenerTodosLosAnimesAsync()).ReturnsAsync(() => _animes.ToList());
        _db.Setup(d => d.ObtenerRelacionadosInfoAsync()).ReturnsAsync(() => _info.ToList());
        _db.Setup(d => d.ObtenerEpisodiosVistosPorAnimeAsync()).ReturnsAsync(() => new Dictionary<int, int>(_vistos));
        _db.Setup(d => d.ObtenerTodosLosRegistrosAsync()).ReturnsAsync(() => _registros.ToList());
        _db.Setup(d => d.GuardarRelacionadosInfoAsync(It.IsAny<IEnumerable<AnimeRelacionadoInfo>>()))
            .Callback<IEnumerable<AnimeRelacionadoInfo>>(nuevas =>
            {
                var lista = nuevas.ToList();
                _info.RemoveAll(i => lista.Any(n => n.AniListId == i.AniListId));
                _info.AddRange(lista);
            })
            .Returns(Task.CompletedTask);
        _anilist.Setup(a => a.ObtenerAnimesPorIdsLoteAsync(It.IsAny<IEnumerable<int>>(), It.IsAny<string?>()))
            .ReturnsAsync(new Dictionary<int, AniListMedia>());
    }

    public void Dispose()
    {
        try { Directory.Delete(_carpeta, true); } catch { }
    }

    private OrdenVisionadoService CrearSut() =>
        new(_db.Object, _franquicias.Object, _anilist.Object, new AnimeLibraryService(_db.Object, _ajustes.Object));

    private void Secuela(int a, int b) => _aristas.Add(new RelacionAnime { AnimeId = a, RelacionadoId = b, Tipo = "SEQUEL" });

    private void Sincronizado(int id) => _sync.Add(new RelacionAnimeSync { AnimeId = id, FechaUtc = DateTime.UtcNow });

    private static AniListMedia Media(int id, string titulo, string formato) => new()
    {
        Id = id, Title = new AniListTitle { Romaji = titulo }, Format = formato,
        StartDate = new AniListFuzzyDate { Year = 2020, Month = 10 }, CoverImage = new AniListCoverImage { ExtraLarge = $"https://img/{id}.jpg" },
    };

    [Theory]
    [InlineData("COMPLETED", 0, 0, false, EstadoParteSaga.Vista)]
    [InlineData("CURRENT", 12, 12, false, EstadoParteSaga.Vista)]
    [InlineData("CURRENT", 12, 3, true, EstadoParteSaga.AMedias)]
    [InlineData("PLANNING", 12, 0, true, EstadoParteSaga.Descargada)]
    [InlineData("PLANNING", 12, 0, false, EstadoParteSaga.EnBiblioteca)]
    public void EstadoDe_CadaCaso(string estadoUsuario, int total, int vistos, bool enDisco, EstadoParteSaga esperado)
    {
        var anime = new AnimeItem { AniListId = 1, EstadoUsuario = estadoUsuario, TotalEpisodios = total };
        OrdenVisionadoService.EstadoDe(anime, vistos, enDisco).Should().Be(esperado);
    }

    [Fact]
    public void EstadoDe_FueraDeLaBiblioteca_NoLaTienes() =>
        OrdenVisionadoService.EstadoDe(null, 0, false).Should().Be(EstadoParteSaga.NoLaTienes);

    [Fact]
    public async Task LeerGuardado_MezclaBibliotecaYDatosGuardados()
    {
        Secuela(1, 2);
        _animes.Add(new AnimeItem { AniListId = 1, Titulo = "Temporada 1", EstadoUsuario = "COMPLETED", AnioLanzamiento = 2013, Temporada = "SPRING" });
        _info.Add(new AnimeRelacionadoInfo { AniListId = 1, Formato = "TV", Anio = 2013, Mes = 4, FechaUtc = DateTime.UtcNow });
        _info.Add(new AnimeRelacionadoInfo { AniListId = 2, Titulo = "Temporada 2", Formato = "TV", Anio = 2017, Mes = 4, UrlPortada = "https://img/2.jpg", FechaUtc = DateTime.UtcNow });

        var leido = await CrearSut().LeerGuardadoAsync(1);

        leido.Partes.Select(p => (p.AniListId, p.Numero, p.Estado)).Should().Equal((1, 1, EstadoParteSaga.Vista), (2, 2, EstadoParteSaga.NoLaTienes));
        leido.Partes[0].Titulo.Should().Be("Temporada 1");
        leido.Partes[0].Formato.Should().Be("TV");
        leido.Partes[1].Titulo.Should().Be("Temporada 2");
        leido.Partes[1].Anio.Should().Be(2017);
    }

    [Fact]
    public async Task LeerGuardado_ParteSinDatos_TituloNulo()
    {
        Secuela(1, 2);
        _animes.Add(new AnimeItem { AniListId = 1, Titulo = "Temporada 1" });

        var leido = await CrearSut().LeerGuardadoAsync(1);

        leido.Partes.Single(p => p.AniListId == 2).Titulo.Should().BeNull();
    }

    [Fact]
    public async Task LeerGuardado_ArchivoEnDisco_EsDescargada()
    {
        Secuela(1, 2);
        _animes.Add(new AnimeItem { AniListId = 2, Titulo = "Temporada 2", TotalEpisodios = 12 });
        _registros.Add(new RegistroEpisodio { AniListId = 2, NumeroEpisodio = 1, RutaArchivo = @"C:\x\Episodio 01.mkv" });

        var leido = await CrearSut().LeerGuardadoAsync(1);

        leido.Partes.Single(p => p.AniListId == 2).Estado.Should().Be(EstadoParteSaga.Descargada);
    }

    [Fact]
    public async Task Actualizar_Sincroniza_YPideSoloLoQueFaltaOCaduco()
    {
        Secuela(1, 2); Secuela(2, 3);
        Sincronizado(1);
        _info.Add(new AnimeRelacionadoInfo { AniListId = 1, Titulo = "Fresca", FechaUtc = DateTime.UtcNow });
        _info.Add(new AnimeRelacionadoInfo { AniListId = 2, Titulo = "Vieja", FechaUtc = DateTime.UtcNow.AddDays(-40) });
        List<int>? pedidos = null;
        _anilist.Setup(a => a.ObtenerAnimesPorIdsLoteAsync(It.IsAny<IEnumerable<int>>(), It.IsAny<string?>()))
            .Callback<IEnumerable<int>, string?>((ids, _) => pedidos = ids.ToList())
            .ReturnsAsync(new Dictionary<int, AniListMedia> { [2] = Media(2, "T2", "TV"), [3] = Media(3, "Película", "MOVIE") });

        var leido = await CrearSut().ActualizarAsync(1, CancellationToken.None);

        _franquicias.Verify(f => f.SincronizarAsync(It.Is<IEnumerable<int>>(ids => ids.SequenceEqual(new[] { 1 }))), Times.Once);
        pedidos.Should().BeEquivalentTo(new[] { 2, 3 });
        leido.SinConexion.Should().BeFalse();
        leido.Partes.Single(p => p.AniListId == 3).Formato.Should().Be("MOVIE");
    }

    [Fact]
    public async Task Actualizar_SinRed_DevuelveLoGuardadoYAvisa()
    {
        Secuela(1, 2);
        Sincronizado(1);
        _animes.Add(new AnimeItem { AniListId = 1, Titulo = "Temporada 1" });

        var leido = await CrearSut().ActualizarAsync(1, CancellationToken.None);

        leido.SinConexion.Should().BeTrue();
        leido.Partes.Select(p => p.AniListId).Should().Equal(1, 2);
    }

    [Fact]
    public async Task SinRedYSinRelacionesConsultadas_AvisaSinConexion()
    {
        _animes.Add(new AnimeItem { AniListId = 1, Titulo = "Recién añadido" });
        _info.Add(new AnimeRelacionadoInfo { AniListId = 1, Titulo = "Recién añadido", FechaUtc = DateTime.UtcNow });

        var leido = await CrearSut().ActualizarAsync(1, CancellationToken.None);

        leido.SinConexion.Should().BeTrue();
        leido.Partes.Should().ContainSingle(p => p.AniListId == 1);
    }

    [Fact]
    public async Task TieneFranquicia_SinConsultar_SiConRelacion_NoSinNinguna()
    {
        var sut = CrearSut();
        (await sut.TieneFranquiciaOSinConsultarAsync(1)).Should().BeTrue("nunca se consultó");

        Sincronizado(1);
        (await sut.TieneFranquiciaOSinConsultarAsync(1)).Should().BeFalse();

        _aristas.Add(new RelacionAnime { AnimeId = 1, RelacionadoId = 9, Tipo = "CHARACTER" });
        (await sut.TieneFranquiciaOSinConsultarAsync(1)).Should().BeFalse("un cruce de personajes no es saga");

        _aristas.Add(new RelacionAnime { AnimeId = 9, RelacionadoId = 1, Tipo = "PREQUEL" });
        (await sut.TieneFranquiciaOSinConsultarAsync(1)).Should().BeTrue("la arista guardada desde el otro lado también cuenta");
    }

    [Fact]
    public async Task Anadir_DaDeAltaConLosDatosDeAniList()
    {
        _anilist.Setup(a => a.ObtenerAnimePorIdAsync(3)).ReturnsAsync(Media(3, "Película", "MOVIE"));

        var anime = await CrearSut().AnadirABibliotecaAsync(3);

        anime.Should().NotBeNull();
        anime!.AniListId.Should().Be(3);
        _db.Verify(d => d.GuardarAnimeAsync(It.Is<AnimeItem>(a => a.AniListId == 3)), Times.Once);
    }

    [Fact]
    public async Task Anadir_SinRed_DevuelveNull()
    {
        (await CrearSut().AnadirABibliotecaAsync(3)).Should().BeNull();
    }

    [Fact]
    public async Task AnadirLoQueYaEstaba_DevuelveElDeLaBiblioteca()
    {
        var existente = new AnimeItem { AniListId = 3, Titulo = "Ya estaba" };
        _anilist.Setup(a => a.ObtenerAnimePorIdAsync(3)).ReturnsAsync(Media(3, "Película", "MOVIE"));
        _db.Setup(d => d.ExisteAnimeAsync(3)).ReturnsAsync(true);
        _db.Setup(d => d.ObtenerAnimePorIdAsync(3)).ReturnsAsync(existente);

        (await CrearSut().AnadirABibliotecaAsync(3)).Should().BeSameAs(existente);
    }
}
```

- [ ] **Paso 2: comprobar que fallan** — filtro `"FullyQualifiedName~OrdenVisionadoServiceTests"`. Esperado: error de compilación.

- [ ] **Paso 3: implementación `AnimeLocalTracker/Services/Franquicias/OrdenVisionadoService.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services.Franquicias;

public enum EstadoParteSaga { Vista, AMedias, Descargada, EnBiblioteca, NoLaTienes }

/// <summary>
/// Una fila del orden de visionado. <paramref name="Numero"/>: 1, 2… en la línea principal; 0 en extras y "aparte".
/// <paramref name="Titulo"/> null = de esa parte aún no hay datos (sin conexión la primera vez).
/// </summary>
public sealed record ParteOrdenVisionado(int AniListId, int Numero, bool EsExtra, bool EsAparte, AnimeItem? Anime, string? Titulo,
    string Formato, int Anio, string? UrlPortada, EstadoParteSaga Estado);

public sealed record OrdenVisionadoLeido(IReadOnlyList<ParteOrdenVisionado> Partes, bool SinConexion);

public interface IOrdenVisionadoService
{
    /// <summary>Si el anime tiene alguna relación de saga guardada, o si sus relaciones nunca se consultaron. Sin red.</summary>
    Task<bool> TieneFranquiciaOSinConsultarAsync(int aniListId);

    /// <summary>El orden con lo ya guardado. Sin red. Nunca lanza.</summary>
    Task<OrdenVisionadoLeido> LeerGuardadoAsync(int aniListId);

    /// <summary>Consulta a AniList lo que falte o haya caducado (relaciones y datos de las partes) y devuelve el orden. Nunca lanza.</summary>
    Task<OrdenVisionadoLeido> ActualizarAsync(int aniListId, CancellationToken ct);

    /// <summary>Da de alta la parte como en la pestaña Agregar. Null si no se pudo (sin red). Si ya estaba, devuelve la de la biblioteca.</summary>
    Task<AnimeItem?> AnadirABibliotecaAsync(int aniListId);
}

/// <summary>Orden de visionado de la ficha (ver docs/plan-orden-visionado-franquicia.md).</summary>
public sealed class OrdenVisionadoService : IOrdenVisionadoService
{
    /// <summary>Los datos de una parte se refrescan cada cierto tiempo (cambian portadas, fechas y nº de episodios).</summary>
    private static readonly TimeSpan Caducidad = TimeSpan.FromDays(30);

    private readonly IDatabaseService _db;
    private readonly IFranquiciaService _franquicias;
    private readonly IAnimeTrackingService _anilist;
    private readonly AnimeLibraryService _biblioteca;

    public OrdenVisionadoService(IDatabaseService db, IFranquiciaService franquicias, IAnimeTrackingService anilist, AnimeLibraryService biblioteca)
    {
        _db = db;
        _franquicias = franquicias;
        _anilist = anilist;
        _biblioteca = biblioteca;
    }

    public static EstadoParteSaga EstadoDe(AnimeItem? anime, int vistos, bool enDisco)
    {
        if (anime == null) return EstadoParteSaga.NoLaTienes;
        if (string.Equals(anime.EstadoUsuario, "COMPLETED", StringComparison.OrdinalIgnoreCase)
            || (anime.TotalEpisodios > 0 && vistos >= anime.TotalEpisodios)) return EstadoParteSaga.Vista;
        if (vistos > 0) return EstadoParteSaga.AMedias;
        return enDisco ? EstadoParteSaga.Descargada : EstadoParteSaga.EnBiblioteca;
    }

    public async Task<bool> TieneFranquiciaOSinConsultarAsync(int aniListId)
    {
        try
        {
            var sync = await _db.ObtenerRelacionesSincronizadasAsync();
            if (!sync.Any(s => s.AnimeId == aniListId)) return true;
            var aristas = await _db.ObtenerRelacionesAnimeAsync();
            return aristas.Any(a => (a.AnimeId == aniListId || a.RelacionadoId == aniListId) && OrdenVisionado.CuentaParaElOrden(a.Tipo));
        }
        catch (Exception ex)
        {
            AppLogger.Warn("OrdenVisionadoService", $"No se pudo saber si el anime {aniListId} tiene saga: {ex.Message}");
            return false;
        }
    }

    public Task<OrdenVisionadoLeido> LeerGuardadoAsync(int aniListId) => Task.Run(async () =>
    {
        try
        {
            return new OrdenVisionadoLeido(await LeerPartesAsync(aniListId), false);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("OrdenVisionadoService", $"No se pudo leer el orden de visionado de {aniListId}: {ex.Message}");
            return new OrdenVisionadoLeido(Array.Empty<ParteOrdenVisionado>(), false);
        }
    });

    public Task<OrdenVisionadoLeido> ActualizarAsync(int aniListId, CancellationToken ct) => Task.Run(async () =>
    {
        try
        {
            // FranquiciaService decide si hace falta (caducidad de 30 días, espera tras un fallo) y explora la saga con sus topes.
            await _franquicias.SincronizarAsync(new[] { aniListId });
            bool sinRelaciones = !(await _db.ObtenerRelacionesSincronizadasAsync()).Any(s => s.AnimeId == aniListId);
            ct.ThrowIfCancellationRequested();

            var partes = await LeerPartesAsync(aniListId);
            var info = (await _db.ObtenerRelacionadosInfoAsync()).ToDictionary(i => i.AniListId);
            var ahora = DateTime.UtcNow;
            var pendientes = partes.Select(p => p.AniListId)
                .Where(id => !info.TryGetValue(id, out var d) || ahora - d.FechaUtc > Caducidad)
                .ToList();

            bool sinDatos = false;
            if (pendientes.Count > 0)
            {
                var recibidos = await _anilist.ObtenerAnimesPorIdsLoteAsync(pendientes);
                ct.ThrowIfCancellationRequested();
                if (recibidos.Count == 0) sinDatos = true;
                else
                {
                    await _db.GuardarRelacionadosInfoAsync(recibidos.Values.Select(m => Convertir(m, ahora)).ToList());
                    partes = await LeerPartesAsync(aniListId);
                }
            }
            return new OrdenVisionadoLeido(partes, sinRelaciones || sinDatos);
        }
        catch (OperationCanceledException)
        {
            return new OrdenVisionadoLeido(Array.Empty<ParteOrdenVisionado>(), false);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("OrdenVisionadoService", $"No se pudo actualizar el orden de visionado de {aniListId}: {ex.Message}");
            return new OrdenVisionadoLeido(Array.Empty<ParteOrdenVisionado>(), true);
        }
    });

    public async Task<AnimeItem?> AnadirABibliotecaAsync(int aniListId)
    {
        try
        {
            var media = await _anilist.ObtenerAnimePorIdAsync(aniListId);
            if (media == null) return null;
            return await _biblioteca.CrearYGuardarAnimeAsync(media, TituloDe(media))
                ?? await _db.ObtenerAnimePorIdAsync(aniListId); // ya estaba (se añadió desde otra pantalla)
        }
        catch (Exception ex)
        {
            AppLogger.Warn("OrdenVisionadoService", $"No se pudo añadir el anime {aniListId}: {ex.Message}");
            return null;
        }
    }

    private async Task<IReadOnlyList<ParteOrdenVisionado>> LeerPartesAsync(int aniListId)
    {
        var aristas = await _db.ObtenerRelacionesAnimeAsync();
        var animes = (await _db.ObtenerTodosLosAnimesAsync()).GroupBy(a => a.AniListId).ToDictionary(g => g.Key, g => g.First());
        var info = (await _db.ObtenerRelacionadosInfoAsync()).ToDictionary(i => i.AniListId);
        var vistos = await _db.ObtenerEpisodiosVistosPorAnimeAsync();
        var enDisco = (await _db.ObtenerTodosLosRegistrosAsync())
            .Where(r => !string.IsNullOrEmpty(r.RutaArchivo)).Select(r => r.AniListId).ToHashSet();

        int Fecha(int id) => info.TryGetValue(id, out var d) && d.Anio is > 0
            ? OrdenVisionado.FechaComparable(d.Anio, d.Mes)
            : animes.TryGetValue(id, out var a) ? a.OrdenEstreno : 0;

        ParteOrdenVisionado Parte(int id, int numero, bool esExtra, bool esAparte)
        {
            animes.TryGetValue(id, out var anime);
            info.TryGetValue(id, out var datos);
            int anio = datos?.Anio ?? anime?.AnioLanzamiento ?? 0;
            string? titulo = anime?.Titulo ?? (string.IsNullOrWhiteSpace(datos?.Titulo) ? null : datos!.Titulo);
            string? portada = anime != null ? anime.PortadaVisible : datos?.UrlPortada;
            var estado = EstadoDe(anime, vistos.GetValueOrDefault(id), enDisco.Contains(id));
            return new ParteOrdenVisionado(id, numero, esExtra, esAparte, anime, titulo, datos?.Formato ?? string.Empty, anio,
                string.IsNullOrWhiteSpace(portada) ? null : portada, estado);
        }

        var orden = OrdenVisionado.Construir(aniListId, aristas, Fecha);
        var partes = new List<ParteOrdenVisionado>();
        int numero = 0;
        foreach (var parte in orden.LineaPrincipal)
        {
            partes.Add(Parte(parte.AniListId, ++numero, esExtra: false, esAparte: false));
            partes.AddRange(parte.Extras.Select(id => Parte(id, 0, esExtra: true, esAparte: false)));
        }
        partes.AddRange(orden.Aparte.Select(id => Parte(id, 0, esExtra: false, esAparte: true)));
        return partes;
    }

    private static AnimeRelacionadoInfo Convertir(AniListMedia media, DateTime ahora) => new()
    {
        AniListId = media.Id,
        Titulo = TituloDe(media),
        Formato = media.Format ?? string.Empty,
        Anio = media.StartDate?.Year,
        Mes = media.StartDate?.Month,
        Dia = media.StartDate?.Day,
        Episodios = media.Episodes,
        UrlPortada = media.CoverImage?.ExtraLarge ?? media.CoverImage?.Large ?? string.Empty,
        FechaUtc = ahora,
    };

    /// <summary>Mismo orden que la pestaña Agregar (<c>AnimeBusquedaItem.TituloPrincipal</c>): romaji, el preferido y el inglés.</summary>
    private static string TituloDe(AniListMedia media) =>
        !string.IsNullOrWhiteSpace(media.Title?.Romaji) ? media.Title!.Romaji! :
        !string.IsNullOrWhiteSpace(media.Title?.UserPreferred) ? media.Title!.UserPreferred! :
        !string.IsNullOrWhiteSpace(media.Title?.English) ? media.Title!.English! :
        LocalizationService.T("Media_TituloDesconocido");
}
```

Nota: si `ObtenerAnimesPorIdsLoteAsync` lanza sin red en vez de devolver un diccionario vacío, el `catch` general devuelve `SinConexion = true` sin partes; en ese caso cambiar ese `catch` para releer `LeerPartesAsync` dentro de un `try` propio (la ventana ya tiene pintado lo guardado de la primera pasada, así que no se pierde nada visible).

- [ ] **Paso 4: registro en DI** (`App.xaml.cs`, tras la línea del `IFranquiciaService`):

```csharp
        services.AddSingleton<AnimeLocalTracker.Services.Franquicias.IOrdenVisionadoService, AnimeLocalTracker.Services.Franquicias.OrdenVisionadoService>();
```

- [ ] **Paso 5: pruebas en verde** — filtro `"FullyQualifiedName~OrdenVisionado"` (cálculo + servicio).

---

## Tarea 4: pieza de la ficha (ViewModel) y textos

**Archivos:**
- Crear: `AnimeLocalTracker/ViewModels/OrdenVisionadoFichaViewModel.cs` (la pieza y su fila `FilaOrdenVisionado`)
- Modificar: `AnimeLocalTracker/ViewModels/DetalleViewModel.cs` (parámetro opcional del constructor, propiedad, `InicializarAsync`, `Dispose`)
- Modificar: `AnimeLocalTracker/Services/LocalizationService.cs` (claves `Det_Orden*`)
- Pruebas: crear `AnimeLocalTracker.Tests/ViewModels/OrdenVisionadoFichaViewModelTests.cs`

**Interfaces:**
- Consume: tarea 3 (`IOrdenVisionadoService`, `ParteOrdenVisionado`, `EstadoParteSaga`, `OrdenVisionadoLeido`); `DetalleViewModel.TraducirValor(string prefijo, string valor)` (claves `Fmt_*` ya existentes); `NavegarMensaje_Detalle(AnimeItem)`; `IDialogService.MostrarToast(string titulo, string mensaje, string icono, string color)`; `Core.Cancelacion.Reemplazar(ref CancellationTokenSource?)`.
- Produce (lo usa la vista de la tarea 5):
  - `OrdenVisionadoFichaViewModel`: `bool HayOrdenVisionado`, `bool MostrandoOrdenVisionado`, `bool EstaCargando`, `bool SinConexion`, `bool SinPartes`, `bool MostrarCargando`, `bool MostrarVacio`, `ObservableCollection<object> Filas` (filas `FilaOrdenVisionado` y, antes del grupo aparte, su cabecera `string`), `event Action<FilaOrdenVisionado>? FilaDestacadaSolicitada`, comandos `AbrirCommand`, `CerrarCommand`, `AbrirFilaCommand(FilaOrdenVisionado)`, `AnadirCommand(FilaOrdenVisionado)`; métodos `Preparar(int aniListId)`, `Task ComprobarDisponibleAsync(CancellationToken)`, `Dispose()`; `internal Task Carga`.
  - `FilaOrdenVisionado : ObservableObject`: `int AniListId`, `string NumeroTexto`, `string Titulo`, `string Subtitulo`, `string? UrlPortada`, `bool EsExtra`, `bool EsAparte`, `bool EsDeLaLineaPrincipal`, `bool EsActual`, `bool EsSiguiente`, `EstadoParteSaga Estado`, `AnimeItem? Anime`, `bool EstaAnadiendo`, `string TextoEstado`, `string IconoEstado`, `bool PuedeAnadir`, `bool PuedeAbrir`.
  - `DetalleViewModel.OrdenVisionado` (`OrdenVisionadoFichaViewModel`).

- [ ] **Paso 1: textos ES/EN** (`LocalizationService.cs`, skill `localizacion`)

Mitad `Es`, tras `["Det_Trailer"] = "Tráiler",`:

```csharp
        ["Det_OrdenVisionado"] = "Orden de visionado",
        ["Det_OrdenVisionadoTip"] = "Ver la saga en orden: temporadas, películas y OVAs",
        ["Det_OrdenSub"] = "Según las relaciones oficiales de AniList",
        ["Det_OrdenCerrar"] = "Cerrar",
        ["Det_OrdenAparte"] = "Aparte: spin-offs y versiones alternativas",
        ["Det_OrdenVacio"] = "Este anime no tiene precuelas, secuelas ni extras en AniList",
        ["Det_OrdenSinConexion"] = "Sin conexión: puede faltar alguna parte",
        ["Det_OrdenSinDatos"] = "Parte sin datos todavía",
        ["Det_OrdenEstasAqui"] = "Estás aquí",
        ["Det_OrdenSiguiente"] = "Siguiente",
        ["Det_OrdenVista"] = "Vista",
        ["Det_OrdenAMedias"] = "A medias",
        ["Det_OrdenDescargada"] = "Descargada",
        ["Det_OrdenEnBiblioteca"] = "En tu biblioteca",
        ["Det_OrdenNoLaTienes"] = "No la tienes",
        ["Det_OrdenAnadir"] = "Añadir a la biblioteca",
        ["Det_OrdenAnadirErrorTitulo"] = "No se pudo añadir",
        ["Det_OrdenAnadirErrorMsj"] = "Comprueba la conexión e inténtalo de nuevo.",
```

Mitad `En`, tras su `["Det_Trailer"] = …`:

```csharp
        ["Det_OrdenVisionado"] = "Watch order",
        ["Det_OrdenVisionadoTip"] = "See the franchise in order: seasons, movies and OVAs",
        ["Det_OrdenSub"] = "Based on AniList's official relations",
        ["Det_OrdenCerrar"] = "Close",
        ["Det_OrdenAparte"] = "Separate: spin-offs and alternative versions",
        ["Det_OrdenVacio"] = "This anime has no prequels, sequels or extras on AniList",
        ["Det_OrdenSinConexion"] = "Offline: some parts may be missing",
        ["Det_OrdenSinDatos"] = "No data for this part yet",
        ["Det_OrdenEstasAqui"] = "You are here",
        ["Det_OrdenSiguiente"] = "Up next",
        ["Det_OrdenVista"] = "Watched",
        ["Det_OrdenAMedias"] = "In progress",
        ["Det_OrdenDescargada"] = "Downloaded",
        ["Det_OrdenEnBiblioteca"] = "In your library",
        ["Det_OrdenNoLaTienes"] = "Not in your library",
        ["Det_OrdenAnadir"] = "Add to library",
        ["Det_OrdenAnadirErrorTitulo"] = "Couldn't add it",
        ["Det_OrdenAnadirErrorMsj"] = "Check your connection and try again.",
```

- [ ] **Paso 2: pruebas (no compilan todavía)**

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Franquicias;
using AnimeLocalTracker.ViewModels;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>Ventana "Orden de visionado" de la ficha: qué se pinta, "Siguiente", "Estás aquí", añadir y abrir.</summary>
public sealed class OrdenVisionadoFichaViewModelTests
{
    private readonly Mock<IOrdenVisionadoService> _servicio = new();
    private readonly Mock<IDialogService> _dialogos = new();

    private static ParteOrdenVisionado P(int id, int numero, EstadoParteSaga estado, bool extra = false, bool aparte = false) =>
        new(id, numero, extra, aparte, estado == EstadoParteSaga.NoLaTienes ? null : new AnimeItem { AniListId = id, Titulo = $"A{id}" },
            $"A{id}", "TV", 2013, null, estado);

    private OrdenVisionadoFichaViewModel CrearSut(int animeId, OrdenVisionadoLeido guardado, OrdenVisionadoLeido actualizado)
    {
        _servicio.Setup(s => s.LeerGuardadoAsync(animeId)).ReturnsAsync(guardado);
        _servicio.Setup(s => s.ActualizarAsync(animeId, It.IsAny<CancellationToken>())).ReturnsAsync(actualizado);
        var sut = new OrdenVisionadoFichaViewModel(_servicio.Object, _dialogos.Object);
        sut.Preparar(animeId);
        return sut;
    }

    private static async Task AbrirYEsperar(OrdenVisionadoFichaViewModel sut)
    {
        await sut.AbrirCommand.ExecuteAsync(null);
        await sut.Carga;
    }

    private static List<FilaOrdenVisionado> Filas(OrdenVisionadoFichaViewModel sut) => sut.Filas.OfType<FilaOrdenVisionado>().ToList();

    [Fact]
    public async Task Abrir_MarcaEstasAqui_YLaSiguienteEsLaPrimeraDeLaLineaSinVer()
    {
        var leido = new OrdenVisionadoLeido(new[]
        {
            P(1, 1, EstadoParteSaga.Vista), P(10, 0, EstadoParteSaga.NoLaTienes, extra: true),
            P(2, 2, EstadoParteSaga.AMedias), P(3, 3, EstadoParteSaga.Descargada), P(20, 0, EstadoParteSaga.NoLaTienes, aparte: true),
        }, false);
        var sut = CrearSut(2, leido, leido);

        await AbrirYEsperar(sut);

        sut.MostrandoOrdenVisionado.Should().BeTrue();
        Filas(sut).Single(f => f.EsActual).AniListId.Should().Be(2);
        Filas(sut).Single(f => f.EsSiguiente).AniListId.Should().Be(2, "la de la ficha está a medias: es la siguiente");
        sut.Filas.OfType<string>().Should().ContainSingle("cabecera del grupo aparte");
        sut.Filas.IndexOf(sut.Filas.OfType<string>().Single()).Should().Be(sut.Filas.Count - 2, "la cabecera va justo antes del grupo");
        sut.SinPartes.Should().BeFalse();
        sut.MostrarVacio.Should().BeFalse();
    }

    [Fact]
    public async Task ExtrasYAparte_NuncaSonLaSiguiente_YTodoVisto_NoMarcaNinguna()
    {
        var leido = new OrdenVisionadoLeido(new[]
        {
            P(1, 1, EstadoParteSaga.Vista), P(10, 0, EstadoParteSaga.NoLaTienes, extra: true), P(20, 0, EstadoParteSaga.NoLaTienes, aparte: true),
        }, false);
        var sut = CrearSut(1, leido, leido);

        await AbrirYEsperar(sut);

        Filas(sut).Should().NotContain(f => f.EsSiguiente);
    }

    [Fact]
    public async Task SoloElPropioAnime_EstadoVacio()
    {
        var leido = new OrdenVisionadoLeido(new[] { P(1, 1, EstadoParteSaga.EnBiblioteca) }, false);
        var sut = CrearSut(1, leido, leido);

        await AbrirYEsperar(sut);

        sut.SinPartes.Should().BeTrue();
        sut.MostrarVacio.Should().BeTrue();
    }

    [Fact]
    public async Task PintaLoGuardado_YLuegoLoActualizado_ConAvisoSinConexion()
    {
        var guardado = new OrdenVisionadoLeido(new[] { P(1, 1, EstadoParteSaga.Vista) }, false);
        var actualizado = new OrdenVisionadoLeido(new[] { P(1, 1, EstadoParteSaga.Vista), P(2, 2, EstadoParteSaga.NoLaTienes) }, true);
        var sut = CrearSut(1, guardado, actualizado);

        await AbrirYEsperar(sut);

        Filas(sut).Select(f => f.AniListId).Should().Equal(1, 2);
        sut.SinConexion.Should().BeTrue();
        sut.EstaCargando.Should().BeFalse();
        sut.MostrarCargando.Should().BeFalse();
    }

    [Fact]
    public async Task ActualizadoVacio_ConservaLoGuardado_YAvisa()
    {
        var guardado = new OrdenVisionadoLeido(new[] { P(1, 1, EstadoParteSaga.Vista), P(2, 2, EstadoParteSaga.NoLaTienes) }, false);
        var sut = CrearSut(1, guardado, new OrdenVisionadoLeido(System.Array.Empty<ParteOrdenVisionado>(), true));

        await AbrirYEsperar(sut);

        Filas(sut).Should().HaveCount(2);
        sut.SinConexion.Should().BeTrue();
    }

    [Fact]
    public async Task CerrarMientrasCarga_NoPintaLoQueLlegaDespues()
    {
        var guardado = new OrdenVisionadoLeido(new[] { P(1, 1, EstadoParteSaga.Vista) }, false);
        var tardio = new TaskCompletionSource<OrdenVisionadoLeido>();
        _servicio.Setup(s => s.LeerGuardadoAsync(1)).ReturnsAsync(guardado);
        _servicio.Setup(s => s.ActualizarAsync(1, It.IsAny<CancellationToken>())).Returns(tardio.Task);
        var sut = new OrdenVisionadoFichaViewModel(_servicio.Object, _dialogos.Object);
        sut.Preparar(1);

        await sut.AbrirCommand.ExecuteAsync(null);
        sut.CerrarCommand.Execute(null);
        tardio.SetResult(new OrdenVisionadoLeido(new[] { P(1, 1, EstadoParteSaga.Vista), P(2, 2, EstadoParteSaga.Vista) }, false));
        await sut.Carga;

        Filas(sut).Should().HaveCount(1);
        sut.MostrandoOrdenVisionado.Should().BeFalse();
    }

    [Fact]
    public async Task Anadir_ConExito_LaFilaPasaAEnTuBiblioteca()
    {
        var leido = new OrdenVisionadoLeido(new[] { P(1, 1, EstadoParteSaga.Vista), P(2, 2, EstadoParteSaga.NoLaTienes) }, false);
        var sut = CrearSut(1, leido, leido);
        _servicio.Setup(s => s.AnadirABibliotecaAsync(2)).ReturnsAsync(new AnimeItem { AniListId = 2, Titulo = "A2" });
        await AbrirYEsperar(sut);
        var fila = Filas(sut).Single(f => f.AniListId == 2);

        await sut.AnadirCommand.ExecuteAsync(fila);

        fila.Estado.Should().Be(EstadoParteSaga.EnBiblioteca);
        fila.Anime.Should().NotBeNull();
        fila.PuedeAnadir.Should().BeFalse();
        fila.EsSiguiente.Should().BeTrue("sigue siendo la primera de la línea sin ver");
    }

    [Fact]
    public async Task Anadir_ConError_AvisaYNoCambia()
    {
        var leido = new OrdenVisionadoLeido(new[] { P(1, 1, EstadoParteSaga.Vista), P(2, 2, EstadoParteSaga.NoLaTienes) }, false);
        var sut = CrearSut(1, leido, leido);
        await AbrirYEsperar(sut);
        var fila = Filas(sut).Single(f => f.AniListId == 2);

        await sut.AnadirCommand.ExecuteAsync(fila);

        fila.Estado.Should().Be(EstadoParteSaga.NoLaTienes);
        fila.EstaAnadiendo.Should().BeFalse();
        _dialogos.Verify(d => d.MostrarToast(LocalizationService.T("Det_OrdenAnadirErrorTitulo"), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task Anadir_MientrasAnade_NoRepite()
    {
        var leido = new OrdenVisionadoLeido(new[] { P(1, 1, EstadoParteSaga.Vista), P(2, 2, EstadoParteSaga.NoLaTienes) }, false);
        var sut = CrearSut(1, leido, leido);
        var alta = new TaskCompletionSource<AnimeItem?>();
        _servicio.Setup(s => s.AnadirABibliotecaAsync(2)).Returns(alta.Task);
        await AbrirYEsperar(sut);
        var fila = Filas(sut).Single(f => f.AniListId == 2);

        var primera = sut.AnadirCommand.ExecuteAsync(fila);
        await sut.AnadirCommand.ExecuteAsync(fila);
        alta.SetResult(new AnimeItem { AniListId = 2 });
        await primera;

        _servicio.Verify(s => s.AnadirABibliotecaAsync(2), Times.Once);
    }

    [Fact]
    public async Task AbrirFila_DeLaBiblioteca_NavegaASuFichaYCierra()
    {
        var leido = new OrdenVisionadoLeido(new[] { P(1, 1, EstadoParteSaga.Vista), P(2, 2, EstadoParteSaga.EnBiblioteca) }, false);
        var sut = CrearSut(1, leido, leido);
        await AbrirYEsperar(sut);
        NavegarMensaje_Detalle? recibido = null;
        var receptor = new object();
        WeakReferenceMessenger.Default.Register<NavegarMensaje_Detalle>(receptor, (_, m) => recibido = m);
        try
        {
            sut.AbrirFilaCommand.Execute(Filas(sut).Single(f => f.AniListId == 2));
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(receptor);
        }

        recibido!.AnimeSeleccionado.AniListId.Should().Be(2);
        sut.MostrandoOrdenVisionado.Should().BeFalse();
    }

    [Fact]
    public async Task ComprobarDisponible_DaElValorDelServicio()
    {
        _servicio.Setup(s => s.TieneFranquiciaOSinConsultarAsync(7)).ReturnsAsync(true);
        var sut = new OrdenVisionadoFichaViewModel(_servicio.Object, _dialogos.Object);
        sut.Preparar(7);

        await sut.ComprobarDisponibleAsync(CancellationToken.None);

        sut.HayOrdenVisionado.Should().BeTrue();
    }

    [Fact]
    public void Fila_Subtitulo_TraduceElFormato_YSinDatosTieneTitulo()
    {
        var fila = new FilaOrdenVisionado(P(3, 3, EstadoParteSaga.Vista) with { Formato = "MOVIE", Anio = 2020 }, esActual: false);
        var sinDatos = new FilaOrdenVisionado(P(4, 4, EstadoParteSaga.NoLaTienes) with { Titulo = null, Formato = "", Anio = 0 }, esActual: false);

        fila.Subtitulo.Should().Be($"{LocalizationService.T("Fmt_MOVIE")} · 2020");
        sinDatos.Titulo.Should().Be(LocalizationService.T("Det_OrdenSinDatos"));
        sinDatos.Subtitulo.Should().BeEmpty();
    }
}
```

- [ ] **Paso 3: comprobar que fallan** — filtro `"FullyQualifiedName~OrdenVisionadoFichaViewModelTests"`. Esperado: error de compilación.

- [ ] **Paso 4: implementación `AnimeLocalTracker/ViewModels/OrdenVisionadoFichaViewModel.cs`**

```csharp
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Franquicias;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AnimeLocalTracker.ViewModels;

/// <summary>Una fila de la ventana "Orden de visionado".</summary>
public partial class FilaOrdenVisionado : ObservableObject
{
    public FilaOrdenVisionado(ParteOrdenVisionado parte, bool esActual)
    {
        AniListId = parte.AniListId;
        NumeroTexto = parte.Numero > 0 ? $"{parte.Numero}." : string.Empty;
        Titulo = parte.Titulo ?? LocalizationService.T("Det_OrdenSinDatos");
        string formato = DetalleViewModel.TraducirValor("Fmt_", parte.Formato);
        string anio = parte.Anio > 0 ? parte.Anio.ToString(LocalizationService.Cultura) : string.Empty;
        Subtitulo = string.Join(" · ", new[] { formato, anio }.Where(t => t.Length > 0));
        UrlPortada = parte.UrlPortada;
        EsExtra = parte.EsExtra;
        EsAparte = parte.EsAparte;
        EsDeLaLineaPrincipal = !parte.EsExtra && !parte.EsAparte;
        EsActual = esActual;
        _estado = parte.Estado;
        _anime = parte.Anime;
    }

    public int AniListId { get; }
    public string NumeroTexto { get; }
    public string Titulo { get; }
    public string Subtitulo { get; }
    public string? UrlPortada { get; }
    public bool EsExtra { get; }
    public bool EsAparte { get; }
    public bool EsDeLaLineaPrincipal { get; }
    public bool EsActual { get; }

    [ObservableProperty] private bool _esSiguiente;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TextoEstado), nameof(IconoEstado))]
    private EstadoParteSaga _estado;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PuedeAnadir), nameof(PuedeAbrir))]
    private AnimeItem? _anime;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PuedeAnadir))]
    private bool _estaAnadiendo;

    public bool PuedeAnadir => Anime == null && !EstaAnadiendo;
    public bool PuedeAbrir => Anime != null && !EsActual;

    public string TextoEstado => LocalizationService.T(Estado switch
    {
        EstadoParteSaga.Vista => "Det_OrdenVista",
        EstadoParteSaga.AMedias => "Det_OrdenAMedias",
        EstadoParteSaga.Descargada => "Det_OrdenDescargada",
        EstadoParteSaga.EnBiblioteca => "Det_OrdenEnBiblioteca",
        _ => "Det_OrdenNoLaTienes",
    });

    public string IconoEstado => Estado switch
    {
        EstadoParteSaga.Vista => "CheckCircle",
        EstadoParteSaga.AMedias => "ProgressClock",
        EstadoParteSaga.Descargada => "Download",
        EstadoParteSaga.EnBiblioteca => "BookmarkOutline",
        _ => "PlusCircleOutline",
    };
}

/// <summary>
/// Ventana "Orden de visionado" de la ficha (ver docs/plan-orden-visionado-franquicia.md). Pieza de <see cref="DetalleViewModel"/>
/// como <see cref="MusicaFichaViewModel"/>: se crea con cada ficha, y su vista (<c>OrdenVisionadoView</c>) la crea la ficha la primera
/// vez que se abre.
/// </summary>
public partial class OrdenVisionadoFichaViewModel : ObservableObject, IDisposable
{
    private readonly IOrdenVisionadoService? _servicio;
    private readonly IDialogService _dialogService;
    private int _animeId;
    private int _version;
    private CancellationTokenSource? _cts;

    public OrdenVisionadoFichaViewModel(IOrdenVisionadoService? servicio, IDialogService dialogService)
    {
        _servicio = servicio;
        _dialogService = dialogService;
    }

    /// <summary>Filas <see cref="FilaOrdenVisionado"/> y, antes del grupo aparte, su cabecera (string).</summary>
    public ObservableCollection<object> Filas { get; } = new();

    [ObservableProperty] private bool _hayOrdenVisionado;
    [ObservableProperty] private bool _mostrandoOrdenVisionado;
    [ObservableProperty] private bool _sinConexion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MostrarCargando), nameof(MostrarVacio))]
    private bool _estaCargando;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MostrarVacio))]
    private bool _sinPartes;

    /// <summary>El indicador solo mientras no hay nada que enseñar (lo guardado se pinta enseguida).</summary>
    public bool MostrarCargando => EstaCargando && Filas.Count == 0;

    public bool MostrarVacio => SinPartes && !EstaCargando;

    /// <summary>La vista lleva la lista hasta esta fila (la siguiente, o la de la ficha si no hay siguiente).</summary>
    public event Action<FilaOrdenVisionado>? FilaDestacadaSolicitada;

    /// <summary>La última carga lanzada (para que las pruebas puedan esperarla).</summary>
    internal Task Carga { get; private set; } = Task.CompletedTask;

    public void Preparar(int aniListId) => _animeId = aniListId;

    public async Task ComprobarDisponibleAsync(CancellationToken ct)
    {
        if (_servicio == null || _animeId <= 0) return;
        bool hay = await _servicio.TieneFranquiciaOSinConsultarAsync(_animeId);
        if (!ct.IsCancellationRequested) HayOrdenVisionado = hay;
    }

    [RelayCommand]
    private Task AbrirAsync()
    {
        if (_servicio == null || _animeId <= 0) return Task.CompletedTask;
        MostrandoOrdenVisionado = true;
        Core.Cancelacion.Reemplazar(ref _cts);
        Carga = CargarAsync(++_version, _cts!.Token);
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void Cerrar()
    {
        MostrandoOrdenVisionado = false;
        _version++;
        _cts?.Cancel();
        EstaCargando = false;
    }

    private async Task CargarAsync(int version, CancellationToken ct)
    {
        EstaCargando = true;
        OnPropertyChanged(nameof(MostrarCargando));
        try
        {
            var guardado = await _servicio!.LeerGuardadoAsync(_animeId);
            if (version != _version) return;
            Pintar(guardado);

            var actualizado = await _servicio.ActualizarAsync(_animeId, ct);
            if (version != _version) return;
            if (actualizado.Partes.Count > 0) Pintar(actualizado);
            SinConexion = actualizado.SinConexion;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("OrdenVisionadoFichaViewModel", $"No se pudo cargar el orden de visionado: {ex.Message}");
        }
        finally
        {
            if (version == _version) EstaCargando = false;
        }
    }

    private void Pintar(OrdenVisionadoLeido leido)
    {
        Filas.Clear();
        bool cabeceraAparte = false;
        foreach (var parte in leido.Partes)
        {
            if (parte.EsAparte && !cabeceraAparte)
            {
                Filas.Add(LocalizationService.T("Det_OrdenAparte"));
                cabeceraAparte = true;
            }
            Filas.Add(new FilaOrdenVisionado(parte, esActual: parte.AniListId == _animeId));
        }
        MarcarSiguiente();
        SinPartes = leido.Partes.Count <= 1;
        OnPropertyChanged(nameof(MostrarCargando));

        var filas = Filas.OfType<FilaOrdenVisionado>().ToList();
        var destacada = filas.FirstOrDefault(f => f.EsSiguiente) ?? filas.FirstOrDefault(f => f.EsActual);
        if (destacada != null) FilaDestacadaSolicitada?.Invoke(destacada);
    }

    private void MarcarSiguiente()
    {
        var filas = Filas.OfType<FilaOrdenVisionado>().ToList();
        var siguiente = filas.FirstOrDefault(f => f.EsDeLaLineaPrincipal && f.Estado != EstadoParteSaga.Vista);
        foreach (var fila in filas) fila.EsSiguiente = ReferenceEquals(fila, siguiente);
    }

    [RelayCommand]
    private void AbrirFila(FilaOrdenVisionado? fila)
    {
        if (fila?.Anime == null || fila.EsActual) return;
        Cerrar();
        WeakReferenceMessenger.Default.Send(new NavegarMensaje_Detalle(fila.Anime));
    }

    // AllowConcurrentExecutions: sin él el comando descartaría por su cuenta la segunda pulsación y la prueba
    // Anadir_MientrasAnade_NoRepite no comprobaría la guarda de la fila (PuedeAnadir), que es la que protege cada fila por separado.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task AnadirAsync(FilaOrdenVisionado? fila)
    {
        if (_servicio == null || fila == null || !fila.PuedeAnadir) return;
        fila.EstaAnadiendo = true;
        try
        {
            var anime = await _servicio.AnadirABibliotecaAsync(fila.AniListId);
            if (anime == null)
            {
                // Mismo color que los demás avisos de error (AvisarSinVideo del reproductor).
                _dialogService.MostrarToast(LocalizationService.T("Det_OrdenAnadirErrorTitulo"), LocalizationService.T("Det_OrdenAnadirErrorMsj"),
                    "AlertCircleOutline", "#EF4444");
                return;
            }
            fila.Anime = anime;
            fila.Estado = EstadoParteSaga.EnBiblioteca;
            MarcarSiguiente();
        }
        finally
        {
            fila.EstaAnadiendo = false;
        }
    }

    public void Dispose()
    {
        _version++;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        GC.SuppressFinalize(this);
    }
}
```

`#EF4444` es el color que ya usan los avisos de error del reproductor (`AvisarSinVideo`): no es un color nuevo. `Core.Cancelacion.Reemplazar(ref CancellationTokenSource?)` admite `null` y deja el campo con la fuente nueva.

- [ ] **Paso 5: conectar en `DetalleViewModel.cs`**

Constructor, al final de los parámetros:

```csharp
        Services.Recomendaciones.IParecidosService? parecidos = null,
        Services.Franquicias.IOrdenVisionadoService? ordenVisionado = null)
```

En el cuerpo, tras crear `Musica`:

```csharp
        OrdenVisionado = new OrdenVisionadoFichaViewModel(ordenVisionado, dialogService);
```

Propiedad, junto a `public MusicaFichaViewModel Musica { get; private set; }`:

```csharp
    /// <summary>Ventana "Orden de visionado" de la saga (su vista es <c>OrdenVisionadoView</c>).</summary>
    public OrdenVisionadoFichaViewModel OrdenVisionado { get; }
```

En `InicializarAsync`, tras `EsFavoritoAnime = anime.EsFavorito;`:

```csharp
        OrdenVisionado.Preparar(anime.AniListId);
```

y tras `CargaParecidos = CargarParecidosAsync(anime, ctFicha);`:

```csharp
        _ = OrdenVisionado.ComprobarDisponibleAsync(ctFicha);
```

En `Dispose`, tras `Episodios.Dispose();`:

```csharp
        OrdenVisionado.Dispose();
```

- [ ] **Paso 6: pruebas en verde** — filtro `"FullyQualifiedName~OrdenVisionado|FullyQualifiedName~LocalizationServiceTests|FullyQualifiedName~OfflineFase2|FullyQualifiedName~DescargarTemporada|FullyQualifiedName~DescargasMusica"` (las últimas crean `DetalleViewModel`).

---

## Tarea 5: la ventana y el chip de la ficha

**Archivos:**
- Crear: `AnimeLocalTracker/Views/OrdenVisionadoView.xaml` y `OrdenVisionadoView.xaml.cs`
- Modificar: `AnimeLocalTracker/Views/DetalleView.xaml` (chip tras el de Openings & Endings; anfitrión tras `AnfitrionMusica`)
- Modificar: `AnimeLocalTracker/Views/DetalleView.xaml.cs` (`MostrarVentanasSiCorresponde`, `AlSalirDeLaFicha`, observar `OrdenVisionado`)
- Pruebas: ampliar `AnimeLocalTracker.Tests/Views/EnlacesDeLaFichaTests.cs`

**Interfaces:**
- Consume: tarea 4 (todo `OrdenVisionadoFichaViewModel` y `FilaOrdenVisionado`).

- [ ] **Paso 1: prueba de enlaces (falla: la vista no existe)**

En `Ficha_LasRutasHaciaSusPiezas_Existen`:

```csharp
        RutasRotas(xaml, "{Binding OrdenVisionado", typeof(OrdenVisionadoFichaViewModel)).Should().BeEmpty();
```

En `VentanasDeLaFicha_LosComandosDesdeSusFilas_Existen`:

```csharp
        RutasRotas(LeerVista("OrdenVisionadoView.xaml"), "{Binding DataContext", typeof(OrdenVisionadoFichaViewModel)).Should().BeEmpty();
```

En `Ficha_UsaSusTresPiezas`, añadir a la cadena de `Contain`:

```csharp
            .And.Contain("{Binding OrdenVisionado.AbrirCommand}")
```

- [ ] **Paso 2: chip y anfitrión en `DetalleView.xaml`**

Justo después del `Button` de Openings & Endings (mismo estilo que el de Tráiler):

```xml
                        <!-- Orden de visionado de la saga (ventana encima; ver OrdenVisionadoView) -->
                        <Button Command="{Binding OrdenVisionado.AbrirCommand}" Style="{StaticResource DetChipButton}" Margin="0,0,8,6" VerticalAlignment="Center"
                                Visibility="{Binding OrdenVisionado.HayOrdenVisionado, Converter={StaticResource BoolToVis}}"
                                ToolTip="{loc:T Det_OrdenVisionadoTip}" AutomationProperties.Name="{loc:T Det_OrdenVisionado}">
                            <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
                                <materialDesign:PackIcon Kind="FormatListNumbered" Width="13" Height="13" Foreground="{StaticResource Brush.Accent}" VerticalAlignment="Center" Margin="0,0,5,0"/>
                                <TextBlock Text="{loc:T Det_OrdenVisionado}" FontSize="11" Foreground="{StaticResource Brush.AccentSoft}" FontWeight="SemiBold" VerticalAlignment="Center"/>
                            </StackPanel>
                        </Button>
```

Tras `<ContentControl x:Name="AnfitrionMusica" … />`:

```xml
        <!-- PARTE 3c: VENTANA DE ORDEN DE VISIONADO. Vive en OrdenVisionadoView y se crea la primera vez que se abre. -->
        <ContentControl x:Name="AnfitrionOrdenVisionado" Panel.ZIndex="96" Focusable="False"/>
```

- [ ] **Paso 3: `AnimeLocalTracker/Views/OrdenVisionadoView.xaml`**

```xml
<UserControl x:Class="AnimeLocalTracker.Views.OrdenVisionadoView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:materialDesign="http://materialdesigninxaml.net/winfx/xaml/themes"
             xmlns:loc="clr-namespace:AnimeLocalTracker.Services"
             xmlns:vm="clr-namespace:AnimeLocalTracker.ViewModels"
             xmlns:sys="clr-namespace:System;assembly=System.Runtime"
             TextElement.Foreground="{DynamicResource MaterialDesignBody}"
             Visibility="{Binding MostrandoOrdenVisionado, Converter={StaticResource BoolToVis}}">
    <!-- Ventana "Orden de visionado" de la ficha. DataContext: OrdenVisionadoFichaViewModel. Se dibuja encima, como la de música;
         clic fuera de la tarjeta o Escape la cierran. -->
    <Grid x:Name="FondoOrden" Background="{StaticResource Brush.Scrim}" MouseLeftButtonDown="FondoOrden_MouseLeftButtonDown">
        <materialDesign:Card x:Name="OrdenCard" Width="640" MaxHeight="640" Margin="28" VerticalAlignment="Center" HorizontalAlignment="Center"
                             UniformCornerRadius="16" Background="{StaticResource Brush.SurfaceDialog}" BorderBrush="{StaticResource Brush.BorderStrong}" BorderThickness="1"
                             materialDesign:ElevationAssist.Elevation="Dp16" Padding="20,16" Focusable="True" FocusVisualStyle="{x:Null}">
            <materialDesign:Card.InputBindings>
                <KeyBinding Key="Escape" Command="{Binding CerrarCommand}"/>
            </materialDesign:Card.InputBindings>
            <Grid>
                <Grid.RowDefinitions>
                    <RowDefinition Height="Auto"/>
                    <RowDefinition Height="Auto"/>
                    <RowDefinition Height="*"/>
                </Grid.RowDefinitions>

                <!-- Cabecera -->
                <Grid Grid.Row="0" Margin="0,0,0,10">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="*"/>
                        <ColumnDefinition Width="Auto"/>
                    </Grid.ColumnDefinitions>
                    <StackPanel>
                        <TextBlock Text="{loc:T Det_OrdenVisionado}" FontWeight="Bold" FontSize="14" Foreground="{StaticResource Brush.TextPrimary}"/>
                        <TextBlock Text="{loc:T Det_OrdenSub}" FontSize="11" Foreground="{StaticResource Brush.TextTertiary}" Margin="0,2,0,0"/>
                    </StackPanel>
                    <Button Grid.Column="1" Command="{Binding CerrarCommand}" Style="{StaticResource MaterialDesignToolButton}" Width="28" Height="28"
                            VerticalAlignment="Top" Cursor="Hand" ToolTip="{loc:T Det_OrdenCerrar}" AutomationProperties.Name="{loc:T Det_OrdenCerrar}">
                        <materialDesign:PackIcon Kind="Close" Width="18" Height="18" Foreground="{StaticResource Brush.IconMuted}"/>
                    </Button>
                </Grid>

                <!-- Aviso sin conexión -->
                <Border Grid.Row="1" Visibility="{Binding SinConexion, Converter={StaticResource BoolToVis}}" Background="{StaticResource Brush.WarningTint}"
                        CornerRadius="8" Padding="10,6" Margin="0,0,0,8">
                    <StackPanel Orientation="Horizontal">
                        <materialDesign:PackIcon Kind="WifiOff" Width="14" Height="14" Foreground="{StaticResource Brush.Warning}" VerticalAlignment="Center" Margin="0,0,6,0"/>
                        <TextBlock Text="{loc:T Det_OrdenSinConexion}" FontSize="11" Foreground="{StaticResource Brush.WarningSoft}" VerticalAlignment="Center"/>
                    </StackPanel>
                </Border>

                <!-- Cargando (solo mientras no hay nada que enseñar) -->
                <ProgressBar Grid.Row="2" Style="{StaticResource MaterialDesignCircularProgressBar}" IsIndeterminate="True" Width="32" Height="32"
                             HorizontalAlignment="Center" VerticalAlignment="Center" Margin="0,24"
                             Visibility="{Binding MostrarCargando, Converter={StaticResource BoolToVis}}"/>

                <!-- Estado vacío -->
                <StackPanel Grid.Row="2" HorizontalAlignment="Center" VerticalAlignment="Center" Margin="0,24"
                            Visibility="{Binding MostrarVacio, Converter={StaticResource BoolToVis}}">
                    <materialDesign:PackIcon Kind="FormatListNumbered" Width="36" Height="36" HorizontalAlignment="Center" Foreground="{StaticResource Brush.IconMuted}"/>
                    <TextBlock Text="{loc:T Det_OrdenVacio}" Style="{StaticResource AppEmptyText}" TextAlignment="Center" TextWrapping="Wrap" MaxWidth="360" Margin="0,8,0,0"/>
                </StackPanel>

                <!-- Lista -->
                <ListBox x:Name="ListaOrden" Grid.Row="2" ItemsSource="{Binding Filas}"
                         VirtualizingPanel.IsVirtualizing="True" VirtualizingPanel.VirtualizationMode="Recycling" VirtualizingPanel.ScrollUnit="Pixel"
                         ScrollViewer.CanContentScroll="True" ScrollViewer.VerticalScrollBarVisibility="Auto" ScrollViewer.HorizontalScrollBarVisibility="Disabled"
                         Background="Transparent" BorderThickness="0"
                         Visibility="{Binding SinPartes, Converter={StaticResource InverseBoolToVis}}">
                    <ListBox.ItemContainerStyle>
                        <Style TargetType="ListBoxItem">
                            <Setter Property="Focusable" Value="False"/>
                            <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
                            <Setter Property="Template">
                                <Setter.Value>
                                    <ControlTemplate TargetType="ListBoxItem"><ContentPresenter/></ControlTemplate>
                                </Setter.Value>
                            </Setter>
                        </Style>
                    </ListBox.ItemContainerStyle>
                    <ListBox.Resources>
                        <!-- Cabecera del grupo "Aparte" -->
                        <DataTemplate DataType="{x:Type sys:String}">
                            <TextBlock Text="{Binding}" FontSize="11" FontWeight="SemiBold" Foreground="{StaticResource Brush.TextTertiary}" Margin="4,14,0,6"/>
                        </DataTemplate>

                        <!-- Una parte de la saga -->
                        <DataTemplate DataType="{x:Type vm:FilaOrdenVisionado}">
                            <Border x:Name="Fondo" CornerRadius="10" Padding="8,6" Margin="0,0,0,4" Background="Transparent" Cursor="Hand"
                                    MouseLeftButtonUp="Fila_MouseLeftButtonUp">
                                <Grid>
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width="28"/>
                                        <ColumnDefinition Width="Auto"/>
                                        <ColumnDefinition Width="*"/>
                                        <ColumnDefinition Width="Auto"/>
                                    </Grid.ColumnDefinitions>
                                    <TextBlock Text="{Binding NumeroTexto}" FontSize="12" FontWeight="SemiBold" Foreground="{StaticResource Brush.TextTertiary}" VerticalAlignment="Center"/>

                                    <!-- Portada: el marcador va debajo (se ve si la imagen no carga) -->
                                    <Grid Grid.Column="1" Width="36" Height="52" Margin="0,0,10,0">
                                        <Border CornerRadius="6" Background="{StaticResource Brush.SurfaceChip}">
                                            <materialDesign:PackIcon Kind="ImageOutline" Width="16" Height="16" HorizontalAlignment="Center" VerticalAlignment="Center" Foreground="{StaticResource Brush.IconMuted}"/>
                                        </Border>
                                        <Image Source="{Binding UrlPortada, TargetNullValue={x:Null}}" Stretch="UniformToFill" RenderOptions.BitmapScalingMode="HighQuality">
                                            <Image.Clip><RectangleGeometry Rect="0,0,36,52" RadiusX="6" RadiusY="6"/></Image.Clip>
                                        </Image>
                                    </Grid>

                                    <StackPanel Grid.Column="2" VerticalAlignment="Center">
                                        <TextBlock Text="{Binding Titulo}" FontSize="13" FontWeight="SemiBold" Foreground="{StaticResource Brush.TextPrimary}" TextTrimming="CharacterEllipsis"/>
                                        <StackPanel Orientation="Horizontal" Margin="0,2,0,0">
                                            <TextBlock Text="{Binding Subtitulo}" FontSize="11" Foreground="{StaticResource Brush.TextTertiary}" VerticalAlignment="Center"/>
                                            <Border Visibility="{Binding EsActual, Converter={StaticResource BoolToVis}}" Background="{StaticResource Brush.AccentTint}" CornerRadius="6" Padding="6,1" Margin="8,0,0,0">
                                                <TextBlock Text="{loc:T Det_OrdenEstasAqui}" FontSize="10" FontWeight="SemiBold" Foreground="{StaticResource Brush.AccentSoft}"/>
                                            </Border>
                                            <Border Visibility="{Binding EsSiguiente, Converter={StaticResource BoolToVis}}" Background="{StaticResource Brush.SuccessTint}" CornerRadius="6" Padding="6,1" Margin="8,0,0,0">
                                                <TextBlock Text="{loc:T Det_OrdenSiguiente}" FontSize="10" FontWeight="SemiBold" Foreground="{StaticResource Brush.Success}"/>
                                            </Border>
                                        </StackPanel>
                                    </StackPanel>

                                    <!-- Estado, o el botón de añadir -->
                                    <StackPanel Grid.Column="3" Orientation="Horizontal" VerticalAlignment="Center" Margin="8,0,0,0">
                                        <StackPanel Orientation="Horizontal" Visibility="{Binding PuedeAnadir, Converter={StaticResource InverseBoolToVis}}">
                                            <materialDesign:PackIcon x:Name="IconoEstado" Kind="{Binding IconoEstado}" Width="15" Height="15" VerticalAlignment="Center" Margin="0,0,5,0" Foreground="{StaticResource Brush.TextSoft}"/>
                                            <TextBlock Text="{Binding TextoEstado}" FontSize="11" VerticalAlignment="Center" Foreground="{StaticResource Brush.TextSoft}"/>
                                        </StackPanel>
                                        <Button Visibility="{Binding PuedeAnadir, Converter={StaticResource BoolToVis}}" Style="{StaticResource DetChipButton}"
                                                Command="{Binding DataContext.AnadirCommand, RelativeSource={RelativeSource AncestorType=UserControl}}" CommandParameter="{Binding}"
                                                ToolTip="{loc:T Det_OrdenAnadir}" AutomationProperties.Name="{loc:T Det_OrdenAnadir}" Cursor="Hand">
                                            <StackPanel Orientation="Horizontal">
                                                <materialDesign:PackIcon Kind="PlusCircleOutline" Width="13" Height="13" Foreground="{StaticResource Brush.Accent}" VerticalAlignment="Center" Margin="0,0,5,0"/>
                                                <TextBlock Text="{loc:T Det_OrdenAnadir}" FontSize="11" FontWeight="SemiBold" Foreground="{StaticResource Brush.AccentSoft}" VerticalAlignment="Center"/>
                                            </StackPanel>
                                        </Button>
                                    </StackPanel>
                                </Grid>
                            </Border>
                            <DataTemplate.Triggers>
                                <DataTrigger Binding="{Binding EsExtra}" Value="True">
                                    <Setter TargetName="Fondo" Property="Margin" Value="28,0,0,4"/>
                                </DataTrigger>
                                <DataTrigger Binding="{Binding EsActual}" Value="True">
                                    <Setter TargetName="Fondo" Property="Background" Value="{StaticResource Brush.SurfaceControl}"/>
                                </DataTrigger>
                                <DataTrigger Binding="{Binding PuedeAbrir}" Value="False">
                                    <Setter TargetName="Fondo" Property="Cursor" Value="Arrow"/>
                                </DataTrigger>
                                <DataTrigger Binding="{Binding Estado}" Value="Vista">
                                    <Setter TargetName="IconoEstado" Property="Foreground" Value="{StaticResource Brush.Success}"/>
                                </DataTrigger>
                                <DataTrigger Binding="{Binding Estado}" Value="AMedias">
                                    <Setter TargetName="IconoEstado" Property="Foreground" Value="{StaticResource Brush.Accent}"/>
                                </DataTrigger>
                                <DataTrigger Binding="{Binding Estado}" Value="Descargada">
                                    <Setter TargetName="IconoEstado" Property="Foreground" Value="{StaticResource Brush.AccentSoft}"/>
                                </DataTrigger>
                            </DataTemplate.Triggers>
                        </DataTemplate>
                    </ListBox.Resources>
                </ListBox>
            </Grid>
        </materialDesign:Card>
    </Grid>
</UserControl>
```

- [ ] **Paso 4: `AnimeLocalTracker/Views/OrdenVisionadoView.xaml.cs`**

Antes de escribir `EsDentroDeUnBoton`, buscar si `DetalleView` ya tiene un ayudante equivalente (p. ej. el que usa `FilaDe(…, out bool sobreUnBoton)`) y reutilizarlo.

```csharp
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;

namespace AnimeLocalTracker.Views;

/// <summary>Ventana "Orden de visionado" de la ficha. La crea <see cref="DetalleView"/> la primera vez que se abre.</summary>
public partial class OrdenVisionadoView : UserControl
{
    private OrdenVisionadoFichaViewModel? _observado;

    public OrdenVisionadoView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_observado != null) _observado.FilaDestacadaSolicitada -= Destacar;
            _observado = DataContext as OrdenVisionadoFichaViewModel;
            if (_observado != null) _observado.FilaDestacadaSolicitada += Destacar;
        };
        // El foco dentro de la tarjeta hace que Escape la cierre.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) Dispatcher.BeginInvoke(new Action(() => OrdenCard.Focus()), System.Windows.Threading.DispatcherPriority.Input);
        };
    }

    private void Destacar(FilaOrdenVisionado fila) =>
        Dispatcher.BeginInvoke(new Action(() => ListaOrden.ScrollIntoView(fila)), System.Windows.Threading.DispatcherPriority.Loaded);

    private void Fila_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (e.OriginalSource is DependencyObject origen && EsDentroDeUnBoton(origen)) return; // "Añadir" tiene su comando
            if (sender is FrameworkElement { DataContext: FilaOrdenVisionado fila } && _observado?.AbrirFilaCommand.CanExecute(fila) == true)
                _observado.AbrirFilaCommand.Execute(fila);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("OrdenVisionadoView", $"No se pudo abrir la parte: {ex.Message}");
        }
    }

    private static bool EsDentroDeUnBoton(DependencyObject origen)
    {
        for (var actual = origen; actual != null;
             actual = actual is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(actual) : LogicalTreeHelper.GetParent(actual))
        {
            if (actual is Button) return true;
        }
        return false;
    }

    /// <summary>Un clic en el fondo oscuro (fuera de la tarjeta) cierra la ventana.</summary>
    private void FondoOrden_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, FondoOrden)) return;
        _observado?.CerrarCommand.Execute(null);
        e.Handled = true;
    }
}
```

- [ ] **Paso 5: `DetalleView.xaml.cs`** — crear/reasignar la ventana, observarla y cerrarla al salir

Al final de `MostrarVentanasSiCorresponde`:

```csharp
        var orden = _vmObservado?.OrdenVisionado;
        if (AnfitrionOrdenVisionado.Content is OrdenVisionadoView ventanaOrden) ventanaOrden.DataContext = orden;
        else if (orden?.MostrandoOrdenVisionado == true) AnfitrionOrdenVisionado.Content = new OrdenVisionadoView { DataContext = orden };
```

En el `DataContextChanged`, tras `_vmObservado.Seguimiento.PropertyChanged += Seguimiento_PropertyChanged;`:

```csharp
                _vmObservado.OrdenVisionado.PropertyChanged += OrdenVisionado_PropertyChanged;
```

En `DejarDeObservar`, junto a la baja de `Seguimiento_PropertyChanged`:

```csharp
        _vmObservado.OrdenVisionado.PropertyChanged -= OrdenVisionado_PropertyChanged;
```

Manejador, junto a `Seguimiento_PropertyChanged`:

```csharp
    private void OrdenVisionado_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OrdenVisionadoFichaViewModel.MostrandoOrdenVisionado)) Core.HiloUi.Ejecutar(MostrarVentanasSiCorresponde);
    }
```

En `AlSalirDeLaFicha`, tras `_ultimoVm?.Musica.AlOcultarLaFicha();`:

```csharp
        // La ventana del orden de visionado no debe seguir abierta al volver a la ficha desde otra pestaña.
        if (_ultimoVm?.OrdenVisionado.MostrandoOrdenVisionado == true) _ultimoVm.OrdenVisionado.CerrarCommand.Execute(null);
```

- [ ] **Paso 6: compilar (0 advertencias), pruebas y literales**

Filtro `"FullyQualifiedName~EnlacesDeLaFicha|FullyQualifiedName~OrdenVisionado|FullyQualifiedName~LocalizationServiceTests"`; después `python .claude/skills/localizacion/scripts/buscar_literales.py AnimeLocalTracker/Views/OrdenVisionadoView.xaml AnimeLocalTracker/ViewModels/OrdenVisionadoFichaViewModel.cs AnimeLocalTracker/Services/Franquicias/OrdenVisionadoService.cs`. Esperado: todo en verde; los literales que salgan son solo mensajes de registro.

---

## Tarea 6: cierre

- [ ] **Paso 1: suite completa, una vez** (skill `repo-build-test`), sin filtro. Esperado: todo en verde; el total sube respecto a 3437.
- [ ] **Paso 2: benchmarks compilan** (cambió `IDatabaseService`).
- [ ] **Paso 3: en vivo con perfil aislado y red** (skills `perfil-aislado` y `wpf-visual-verification`):
  1. Foto de la carpeta real; `crear` → `iniciar PERFIL --con-red`; en el registro de sesión, `Migración aplicada: v27`.
  2. Ficha de *Kage no Jitsuryokusha ni Naritakute!* (buscador de la Galería por UIA, `ValuePattern` en "Buscar anime guardado..."); invocar el chip "Orden de visionado" por su nombre; esperar a que termine de cargar y capturar: línea principal numerada, estados, "Siguiente", "Estás aquí".
  3. Repetir con la saga más larga de la biblioteca (en la copia de la base, la de más aristas PREQUEL/SEQUEL).
  4. "Añadir a la biblioteca" en una parte que no esté: la fila cambia y el anime aparece en la Galería.
  5. `cerrar` e `iniciar` **sin** `--con-red`: la ventana muestra lo guardado (aviso de sin conexión si falta algo).
  6. `cerrar`; comparar la carpeta real (0 diferencias); `borrar` el perfil.
- [ ] **Paso 4: informe al usuario** con un ejemplo de uso, lo verificado y lo no verificado; proponer el commit (`commit-es`) y esperar su orden.
