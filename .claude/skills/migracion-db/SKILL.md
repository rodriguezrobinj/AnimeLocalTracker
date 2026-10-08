---
name: migracion-db
description: Procedimiento para cambiar el esquema de la base SQLite de AnimeLocalTracker (columna, tabla o índice nuevos, índice único, corrección o borrado de datos) con una migración versionada en DatabaseService.Migraciones, su prueba con una "base vieja" y la verificación sobre una copia de la base real. Usar al tocar un modelo persistido, añadir un índice, o al ver user_version, Migraciones, CreateTableAsync o "migración" en la tarea.
---

# Migración de la base de datos — AnimeLocalTracker

La base del usuario (`biblioteca.db`, SQLite en modo WAL, con meses de historial) evoluciona **solo** con la lista `Migraciones` de `AnimeLocalTracker/Services/DatabaseService.cs` (`persistence.md` #6). Cada entrada es `(versión, descripción, acción)`; `PRAGMA user_version` avanza al terminar cada una, y si una lanza, el arranque falla y se reintenta en el siguiente. Por eso una migración mala no es un bug de una pantalla: es la app sin abrir o datos perdidos.

## 1. Elegir el tipo

| Necesitas | Migración | Modelo a seguir |
|---|---|---|
| **Columna nueva** en un modelo | Entrada nueva que llama `CreateTableAsync<Modelo>()` (sqlite-net hace `ALTER TABLE ADD COLUMN`; idempotente). Las filas que ya existen quedan con NULL: si el valor por defecto no es el de C# (`false`/`0`/`null`), añade un `UPDATE`. | v15 `AgregarColumnasMusicaHistorialAsync`; v21 (`ConservarVideos`) |
| **Tabla nueva** | `CreateTableAsync<T>()` + sus índices con `CREATE [UNIQUE] INDEX IF NOT EXISTS IX_<Tabla>_<Uso> …` | v13 `CrearTablasSkipAsync`, v17 |
| **Índice nuevo** en tabla existente | `CREATE INDEX IF NOT EXISTS`. **Nunca `[Indexed]` en el modelo**: solo aplica a bases nuevas y duplica índices (DB-01, v10). | v2, v3 |
| **Índice único** sobre datos que ya existen | Primero unir o limpiar los repetidos, luego el índice, **en la misma transacción**. | v22 `DeduplicarRegistrosEpisodioAsync` |
| **Corregir o borrar datos** | Ver "Migración destructiva". | v16, v22 |

El hook `aviso_reglas.py` avisa si escribes `CreateTable`/`[Indexed]` fuera de `DatabaseService`.

## 2. Reglas de la entrada

- Versión = la mayor + 1. **Nunca** editar, reordenar ni reutilizar una entrada ya publicada (hay bases en cualquier versión; restaurar un respaldo viejo vuelve a ejecutar las migraciones sobre datos con la forma antigua).
- Comentario `/// v<N>: …` con el *por qué* y qué pasa con las filas existentes, y descripción de una línea en la tabla, como las demás.
- Idempotente: `IF NOT EXISTS`, `CreateTableAsync`. Si la acción se corta a medias y se reintenta, no debe fallar ni duplicar.
- Fechas en UTC. sqlite-net devuelve `DateTime` con `Kind = Unspecified`: no compares `Kind`.
- Si el cambio añade una restricción (único, NOT NULL), revisa que el guardado normal **y la importación de JSON** (`AplicarUpsertRegistros`) no la violen con datos legítimos (el JSON puede traer repetidos).

## 3. Migración destructiva (borra, une o reescribe datos)

Copia el patrón de v22, que ya está probado:

1. **Copia previa solo si hay algo que tocar** (`CopiaAntesDeUnirFilasAsync` → `CrearSnapshotAtomicoAsync`, `VACUUM INTO`; nombre con fecha para no pisar una copia anterior). Nunca `File.Copy` de la base abierta.
2. Si la copia falla (disco lleno, carpeta de solo lectura), **no se borra nada**: `throw new MigracionAplazadaException(...)`. El ejecutor la deja pendiente (`user_version` no avanza), registra un aviso y la app abre; se reintenta en el siguiente arranque.
3. Los cambios y la creación del índice van en **una sola** `RunInTransactionAsync`. El primer paso es una escritura (p. ej. `DROP INDEX IF EXISTS`) para tomar ya el bloqueo de escritura antes de leer.
4. Reglas de unión que no pierdan información del usuario: "visto" y favorito si alguna fila lo era; la fecha de reproducción **nunca se inventa** (`persistence.md` #5).

## 4. Prueba (xUnit + FluentAssertions, en `AnimeLocalTracker.Tests/Services/`)

Patrón "base vieja": crear la base con `new DatabaseService(rutaTemporal)`, poblarla, `SQLiteAsyncConnection.ResetPool()`, revertir a mano con `SQLiteConnection` (`ALTER TABLE … DROP COLUMN`, `DROP INDEX`, `PRAGMA user_version = N-1`), reabrir con un `DatabaseService` nuevo y comprobar el resultado y `user_version >= N`. Modelos reales: `DatabaseServiceConservarVideosTests.BaseEnV20SinLaColumna_…` (columna) y `DatabaseServiceDeduplicarRegistrosTests` (datos: unión, copia previa, aplazamiento, reabrir no cambia nada, restaurar una copia vieja).

Casos que no se saltan: la base **nueva** llega a la última versión; la **vieja** conserva sus filas; reabrir dos veces no cambia nada; en destructivas, la copia existe solo si había algo que tocar y el fallo de la copia no borra ni impide abrir.

Reglas de las pruebas: carpeta y base temporales (`Path.GetTempPath()`), `ResetPool()` en `Dispose`, **nunca** `AppDataPaths`. Verifica con `repo-build-test`: `--filter "FullyQualifiedName~DatabaseService"` mientras trabajas y la suite completa una vez al cerrar.

## 5. Verificar sobre una copia de la base real

La migración debe correr una vez sobre los datos reales del usuario antes de dar el trabajo por bueno, **sin tocarlos**: usa el skill `perfil-aislado` (`crear` → `iniciar`). La copia conserva el `user_version` real, así que la migración se ejecuta en ese primer arranque.

- En el registro de sesión del perfil debe verse `Migración aplicada: v<N>` (o el aviso de aplazada si probaste el fallo).
- Compara antes y después con Python en solo lectura (`sqlite3.connect(ruta_uri + "?mode=ro", uri=True)`): conteos de las tablas tocadas y los casos límite (filas contradictorias, vacíos). En v22 se verificó así: 3507 → 3468 filas, 0 discrepancias con las reglas de unión.
- Si la migración hizo copia previa, está en `<perfil>\AppData\Local\AnimeLocalTrackerData\Backups\`.

## 6. Antes de dar por terminado

- [ ] Entrada nueva al final, versión consecutiva, comentario `/// v<N>`.
- [ ] Prueba de base vieja y de base nueva; si es destructiva, también copia previa y aplazamiento.
- [ ] Build con 0 advertencias y pruebas en verde (`repo-build-test`).
- [ ] Verificada sobre una copia de la base real (`perfil-aislado`).
- [ ] Actualizado el modelo y, si procede, la importación/exportación JSON.
- [ ] Si cambia lo que el usuario ve (datos que antes no se guardaban), dilo en el informe con un ejemplo concreto de la app.
