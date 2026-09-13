# INFORME DE AUDITORÍA — AnimeLocalTracker · Bloque 3: Rendimiento y Calidad/DevOps

| | |
|---|---|
| **Proyecto** | AnimeLocalTracker v1.0.5 (desktop Windows, .NET 8 WPF) |
| **Fecha** | 2026-09-02 |
| **Versión del informe** | 1.0 (entrega parcial del plan por bloques) |
| **Alcance cubierto** | Bloque 3: §4.4 Rendimiento (benchmarks, DB real en modo lectura, UI/rendering, procesos satélite) y §4.6 Calidad de código y DevOps (CI/CD, gates, observabilidad, DR) |
| **Confidencialidad** | Interno — uso exclusivo del equipo del proyecto |
| **Equipo firmante** | SRE · Full-stack (perfil rendimiento) · Analista (mediciones) |
| **Metodología** | Estática con `file:line`; **análisis real de la BD de producción local en modo read-only** (sqlite3 `mode=ro`, sin escrituras) y del `app.log` real; **no se ejecutaron benchmarks** (ver PERF/OPS-001: nunca se han ejecutado). Complementa a Bloques 1-2 sin repetirlos. |

---

## 1. Resumen ejecutivo

**Madurez rendimiento: 3.0/5 · DevOps/calidad: 3.6/5.** El diseño general es correcto (WAL, upserts masivos, caché de imágenes con `Freeze()` y decode 220 px, virtualización en Galería, logger por lotes, parsing batch con Rayon, fingerprint por bloques — no O(tamaño)). El problema no es que vaya lento: es que **no hay ninguna medición** — los benchmarks existen pero **nunca se han ejecutado**, ni en CI ni en local (historial vacío), y la única "medición" disponible es el gate de cobertura del CI. Además se detectaron **4 puntos con coste cuadrático o de carga innecesaria** en la UI/DB (PERF-001/002/003/005) y un **dato operativo importante**: el daemon Python falló el handshake en 2/2 sesiones del log real (PERF-004), degradando a one-shot con un binario de 73,4 MB.

### Top 5 riesgos (Bloque 3)

| # | Riesgo | ID | Impacto |
|---|---|---|---|
| 1 | Benchmarks construidos pero **nunca ejecutados** + **discrepancia de rutas**: el script escribe el historial en la raíz del repo y el manager en `bin\` — aunque se ejecuten, `-Target history` no los vería | OPS-001 | **Alto** (sin gate de regresión de rendimiento) |
| 2 | Lista de episodios de Detalle **sin virtualizar** (StackPanel) + refresco O(N) por episodio enriquecido → **O(N²)** en UI para series de 300-3000 episodios | PERF-001 | **Alto** |
| 3 | `ObtenerTodosLosAnimesAsync` trae **siempre la Sinopsis completa** (~hasta 20 000 chars) y hace `File.Exists` por anime para cualquier vista (galería, calendario, notificador, "¿existe este id?") | PERF-002/003 | **Alto/Medio** |
| 4 | Daemon Python: handshake fallido 2/2 sesiones (log real) → **toda la sesión en one-shot** (spawn de 73 MB + imports por comando) | PERF-004 | **Medio** (perf) / INT-004 (funcional) |
| 5 | Enriquecimiento por episodio: 1 SELECT + 1 write individual por episodio en bucle existiendo ya el bulk | PERF-005 | **Medio** |

**Dato de madurez de CI:** cobertura real 46,92 % líneas / 34,31 % ramas (4799/10227 líneas) frente a un gate de 45 % **solo de líneas** (OPS-008) — margen de 1,92 puntos, frágil ante código nuevo sin tests.

---

## 2. Estado de los benchmarks (§4.4)

### 2.1 Qué existe

`AnimeLocalTracker.Benchmarks/` (BenchmarkDotNet 0.15.8, `MemoryDiagnoser`, referencia la app WPF completa). 3 clases, 9 benchmarks:

| Clase | Benchmarks | Mide |
|---|---|---|
| `ReproductorBenchmarks.cs:54-98` | Seeking continuo (1440 seeks 1s-1s), seeking aleatorio (100 saltos, seed 42), Anterior/Siguiente con 100 y 1000 episodios, formateo de tiempo (1000 ticks), umbral auto-tracking 90 % (1000 ticks) | Latencia de comandos del VM sin player real |
| `DatabaseBenchmarks.cs:57-67` | `GuardarRegistrosEpisodioBulkAsync` (500 registros 1 transacción, SQLite temp real) y SELECT completo de 500 filas | Throughput DB real |
| `FileScannerBenchmarks.cs:26-33` | `ExtraerNumeroEpisodio` (regex, 12 patrones hardcoded) | ns/op del parser |

`Program.cs:32-52` (menú 1-4) + `BenchmarkHistoryManager.cs:91-218` con comparación contra historial y **umbral de regresión ±5 % ya implementado** (`:158-169`).

### 2.2 Hallazgo: nunca se han ejecutado (OPS-001 — Alto)

- **Evidencia:** no existe carpeta `BenchmarkHistory/` en el repo ni reportes `.md` en ningún lado (verificado); **ningún paso de `ci.yml` ni `release.yml` invoca benchmarks**; y hay una **discrepancia de rutas**: `run_benchmarks_and_reports.ps1:12` crea el historial en `Join-Path $PSScriptRoot "BenchmarkHistory"` (raíz del repo) mientras `BenchmarkHistoryManager.GetDefaultHistoryDirectory()` (`BenchmarkHistoryManager.cs:53-58`) lo crea en `AppDomain.BaseDirectory` (= `bin\Release\net8.0-windows`) → el modo `-Target history` del script nunca listaría los reportes generados.
- **Causa raíz:** la infraestructura de benchmarks se construyó (proyecto + manager + script) pero nunca se integró en el flujo diario ni en CI.
- **Recomendación:** unificar la ruta del historial (variable de entorno o parámetro), ejecutar benchmarks en CI (job release con `--filter` para acotar tiempo) y fallar el pipeline si la regresión supera el ±5 % ya implementado.
- **Validación:** ejecutar `.\run_benchmarks_and_reports.ps1 -Target benchmarks` dos veces → 2º run debe leer el historial del 1º (comparación ±5 %) y escribir el reporte donde el script lo busca.

### 2.3 Áreas sin benchmark (a añadir en la fase de corrección)

Arranque completo de la app · carga de galería con 500+ animes (decode de portadas) · import/export JSON 50 MB · sync con 200 pendientes · enriquecimiento Python/Rust (ffmpeg por episodio) · parse batch Rust/Rayon con 10 000 nombres · handshake/arranque del daemon real.

---

## 3. Datos reales de la BD local (modo read-only, 2026-09-02)

> Base: `%LocalAppData%\AnimeLocalTrackerData\biblioteca.db`. Sin escrituras: `file:...?mode=ro`. `integrity_check = ok`.

| Métrica | Valor |
|---|---|
| Filas `AnimeItem` / `RegistroEpisodio` | 164 / 2053 |
| Índices existentes | `IX_RegistroEpisodio_AnimeEp (AniListId, NumeroEpisodio)` + auto `RegistroEpisodio_AniListId` |
| Índices en `AnimeItem` | **ninguno** (solo PK rowid `AniListId`) |
| `page_size`/`page_count`/`freelist` | 4096 / 106 / 0 (DB ≈ 0,41 MB) |
| **WAL pendiente en disco** | **4 120 032 B (≈ 3,9 MB)** vs 0,41 MB de la DB — checkpoint grande pendiente (el cierre de la app no lo fuerza; ver OPS nota) |
| `user_version` | 1 |
| EXPLAIN `WHERE AniListId=? AND NumeroEpisodio=?` | `SEARCH USING INDEX IX_RegistroEpisodio_AnimeEp` ✅ |
| EXPLAIN `WHERE VistoLocal=1 AND SincronizadoEnNube=0` (sync) | **`SCAN RegistroEpisodio`** ⚠️ sin índice (PERF-010) |
| EXPLAIN `SELECT * FROM AnimeItem` (galería) | `SCAN AnimeItem` (esperable; orden en memoria) |

**Lectura (desde SRE):** a 2 053 registros todo es trivial; los topes reales son 10⁵+ filas o bibliotecas de miles de animes. Los dos SCAN detectados solo importan si la biblioteca crece ~50×. El WAL de 3,9 MB sin checkpoint sugiere cerrar la app con checkpoint automático no forzado (revisar si sqlite-net lo hace en `CloseAsync`; en la app nunca se cierra la conexión salvo restore/cierre — bajo).

---

## 4. Hallazgos de rendimiento (PERF)

### PERF-001 — Lista de episodios de Detalle sin virtualizar + refresco O(N²) (Alto)

- **Evidencia:** `DetalleView.xaml:52-53` (`ScrollViewer` + `StackPanel` — sin virtualización) para hasta 3000 episodios materializados (límite `DetalleViewModel.cs:313`); `AplicarFiltrosYOrdenamiento` hace `Clear()` + re-add de toda la colección (`DetalleViewModel.cs:661-662`) y se invoca **por cada episodio enriquecido** (`:451,476,502`) → coste cuadrático en UI para series largas.
- **Recomendación:** virtualizar con `ListBox`/`ItemsControl` + `VirtualizingStackPanel` (costo S/M) y **batchear** el refresco: aplicar filtros/orden al terminar el lote de enriquecimiento, no por episodio.
- **Validación:** benchmark de carga de Detalle con 3000 episodios (agregar a `AnimeLocalTracker.Benchmarks`) + medición de frame time al hacer scroll (objetivo: sin jank >16 ms en p95).

### PERF-002 — `ObtenerTodosLosAnimesAsync` arrastra la Sinopsis completa en cada carga de lista (Alto)

- **Evidencia:** `DatabaseService.cs:154-165` — `Table<AnimeItem>().ToListAsync()` sin proyección (sinopsis validada hasta 20 000 chars en import, `:331`) + `ResolverPortadaLocal()` con 1 `File.Exists` por anime (`AnimeItem.cs:150-158`); consumido por galería (`GaleriaViewModel.cs:354`), calendario (`:59`) y notificador (`NewEpisodeNotifier.cs:48`).
- **Recomendación:** proyección ligera (sin `Sinopsis`) para listas, y `ObtenerAnimePorIdAsync` para detalle; mover `ResolverPortadaLocal` a la capa de caché (ya señalado en ARC-003).
- **Validación:** benchmark de `ObtenerTodosLosAnimesAsync` con 500 animes reales (objetivo: reducir bytes leídos ≥ 60 % por la sinopsis).

### PERF-003 — `ExisteEnBibliotecaAsync` carga toda la biblioteca para comprobar un id (Medio)

- **Evidencia:** `AnimeLibraryService.cs:32-36` → `ObtenerTodosLosAnimesAsync()` (con todos los `File.Exists`) solo para un `Any(AniListId == …)`.
- **Recomendación:** añadir `ExisteAnimeAsync(int aniListId)` a `IDatabaseService` (`SELECT EXISTS`) y usarlo. Esfuerzo S.

### PERF-004 — Daemon Python: degradación one-shot por handshake fallido (Medio)

- **Evidencia:** binario `AnimeTrackerTools.exe` onefile de **73,4 MB** (76 936 428 B medidos en `bin`) + `yt-dlp.exe` 17,4 MB; handshake timeout 8 s (`PythonBridgeService.cs:283`); log real (`app.log`, sesiones 00:33 y 00:40 del 2026-09-02): *"Fallo en handshake del daemon: … no incluyó protocolVersion. Se usará modo one-shot"* → `_daemonDescartado = true` (`:313,321`) para toda la sesión; cada comando posterior spawna 73 MB + imports.
- **Recomendación:** reintento con backoff, timeout de handshake mayor, o arranque onefile pre-calentado al inicio (ya hay `Task.Run` de precalentamiento en `App.xaml.cs:342-353`, pero falla silencioso); **evaluar `--onedir`** (la extracción temp es el cuello). Ver INT-004 (Bloque 2) para la parte funcional.
- **Validación:** test de integración cronometrado del arranque real del daemon (objetivo < 8 s en frío en runner de CI).

### PERF-005 — Persistencia del enriquecimiento: 1 SELECT + 1 write por episodio (Medio)

- **Evidencia:** `DetalleViewModel.cs:527-552` invoca `GuardarRegistroEpisodioAsync` (SELECT+merge+write individual, `DatabaseService.cs:380-415`) **en bucle** por episodio enriquecido, pese a existir `GuardarRegistrosEpisodioBulkAsync` (anti-N+1, `:432-482`).
- **Recomendación:** acumular el lote de episodios enriquecidos y persistir una sola vez con el bulk. Esfuerzo S.

### PERF-006 a PERF-011 (compacto)

| ID | Sev. | Evidencia | Recomendación |
|---|---|---|---|
| PERF-006 | Medio | `GaleriaViewModel.cs:811-834` — 1 UPDATE por anime + progreso por anime en hilo UI durante actualización masiva | `Task.Run` + un único UPDATE/transacción; notificar por lote |
| PERF-007 | Medio | `DatabaseService.cs:259-277` (export serializa en el hilo del llamador) y `:295-296` (import: `ReadAllTextAsync`+`Deserialize` en contexto UI) | Envolver en `Task.Run` (el snapshot de copia ya lo hace `:246`) |
| PERF-008 | Bajo | `ImageCacheService.cs:98-127` — portada corrupta en disco → 2 decodes fallidos + re-descarga en **cada** visita; decode síncrono en alta de anime (`GaleriaViewModel.cs:288`) | Borrar/marcar `{id}.jpg` corrupto; siempre vía async con `Task.Run` |
| PERF-009 | Bajo | `GaleriaViewModel.cs:158-163,616-656` — `ICollectionView.Refresh()` sobre toda la colección en el hilo UI por tecla; `GaleriaView.xaml:592` `BitmapScalingMode=HighQuality` por item | Debounce del filtro local + `LowQuality` al hacer scroll |
| PERF-010 | Bajo | `DatabaseService.cs:497-502` — sync de pendientes sin índice: **SCAN** (verificado con EXPLAIN en BD real) | Índice `(VistoLocal, SincronizadoEnNube)` cuando la biblioteca crezca |
| PERF-011 | Bajo | `ReproductorViewModel.cs:1150-1155` — save cada 5 s dispara mensaje que provoca 1 SELECT + marshalling en Galería aunque no sea visible (`GaleriaViewModel.cs:321-348`) | Notificar solo cuando el estado visible del episodio cambie |

**Positivos verificados:** galería con `VirtualizingPanel` + Recycling + ScrollUnit Pixel + CacheLength (`GaleriaView.xaml:501-519`); BitmapImage congelados y decodificados a 220 px (`ImageCacheService.cs:218-231`); logger asíncrono con batching (500 ms/128) y rotación; fingerprinting por 5 bloques de 64 KB (no O(archivo)); parse batch con Rayon usado desde el escáner; progreso de descarga con throttling (0,5 % o ≥150 ms); sin `.Result`/`.Wait()` bloqueantes; carga de biblioteca no bloquea `Show()`.

---

## 5. Hallazgos de DevOps / calidad (OPS)

### OPS-002 — CI no detecta los fallos reales del daemon empaquetado (Alto)

- **Evidencia:** el fallo de handshake del log real (PERF-004) **no lo detectaría ningún pipeline**: `ci.yml`/`release.yml` hacen `pip install -e tools/python` (dev, no el onefile) y nunca ejecutan pytest ni un smoke del daemon; los 4 tests pytest (`tools/python/tests/test_parser.py`) no corren en CI; no hay smoke test del Setup/vpk.
- **Recomendación:** paso en CI: `python -m PyInstaller` build + `AnimeTrackerTools.exe --daemon` con ping y timeout (8-15 s) + `pytest tools/python`; smoke de instalación del vpk en runner limpio (lanzar app con `--smoke` que inicialice y salga 0).
- **Validación:** el job falla si el handshake del binario real supera el timeout.

### OPS-003 a OPS-008 (compacto)

| ID | Sev. | Evidencia | Recomendación |
|---|---|---|---|
| OPS-003 | Medio | `release.yml:148-157` verifica firma solo del `Setup.exe`; sin SBOM (0 hits cyclonedx/trivy) | Generar SBOM en release; verificar firma también del nupkg/delta |
| OPS-004 | Medio | CI sin `clippy`/`fmt`/`deny` para Rust (solo `cargo audit`) ni ruff/mypy para Python; .NET sí 0-warnings (`Directory.Build.props:11-14`) | `cargo clippy -- -D warnings` + `ruff check` en CI |
| OPS-005 | Medio | Sin log de versión al arrancar (verificado en código y en el log real); telemetría/crash-reporting ausente (verificado) | Loguear versión+entorno en `OnStartup`; decidir si se quiere crash reporter mínimo |
| OPS-006 | Medio | Restore de backup sin prueba en CI; sin copia fuera de la máquina; **testhost colgado reciente**: `AnimeLocalTracker.Tests\TestResults\testhost_*_hangdump.dmp` de **428 MB** (31/08 20:36) sin investigar | Test de restore (backup→restore→`integrity_check`) en CI; investigar el hang (¿test de estrés? ¿Rust?) |
| OPS-007 | Bajo | `pyproject.toml:6-13` — deps Python con rangos abiertos (`>=`) salvo `yt-dlp==2026.8.19`; sin dev-deps declaradas; rollback Velopack sin gestión explícita (0 hits); sin manejo de disco lleno | Pinning por lockfile; revisar API de rollback; manejar `IOException` de disco lleno en descargas |
| OPS-008 | Bajo | Umbral de cobertura 45 % **solo líneas** (`ci.yml:97-110`); medida real 46,92 % líneas / **34,31 % ramas** | Añadir umbral de ramas y subir progresivamente |

**Estado de CI/CD (resumen):** `ci.yml` — 1 job `build-and-test` (windows-latest, timeout 45 min), cachés Cargo/NuGet, SCA triple bloqueante (cargo-audit 0.22.2 / pip-audit 2.10.1 / `dotnet list --vulnerable`), cargo build release + copia DLL, `build.ps1 -RunTests -Coverage`, umbral 45 %. `release.yml` — tag `v*`, firma bloqueante (sin `SIGN_CERT_BASE64` → `exit 1`), `vpk pack` con delta, verificación Authenticode del Setup, release **siempre pre-release** (`:166-167`). Dependabot activo en 4 ecosistemas con límites. Actions pinneadas por SHA (verificado Bloque 1).

---

## 6. Checklist §4.4/§4.6

| Práctica | Estado | Evidencia |
|---|---|---|
| Benchmarks de regresión en CI | ❌ | OPS-001 (existen, nunca corren) |
| N+1 en accesos a datos | ⚠️ | bulk upsert ✅ (`DatabaseService.cs:432-482`); persistencia de enriquecimiento ❌ (PERF-005) |
| Índices vs consultas | ⚠️ | índice compuesto ✅; SCAN en sync pendientes y Sinopsis en listas (PERF-002/010) |
| Caché con límites y congelado de imágenes | ✅ | `Freeze()` + 220 px + LRU 135 MB |
| UI virtualizada en listas grandes | ⚠️ | Galería ✅ · Detalle ❌ (PERF-001) |
| Timeouts/cancelación en IO | ⚠️ | Ver Bloque 2 (INT-002, FUN-015) |
| 0 warnings + analizadores (gates .NET) | ✅ | `Directory.Build.props:11-14`; 9 CA suprimidas con justificación en `.editorconfig` |
| Gates de estilo Rust/Python | ❌ | OPS-004 |
| SCA bloqueante en CI/release | ✅ | cargo-audit, pip-audit, dotnet vulnerable (con hueco en Tests: SEC-009 del Bloque 1) |
| Cobertura con umbral | ⚠️ | 46,92 % vs 45 % líneas; sin umbral de ramas (OPS-008) |
| Observabilidad (logs, rotación) | ⚠️ | AppLogger ✅; sin versión al arrancar, sin alertas/crash reporting (OPS-005) |
| Backup/DR probado | ⚠️ | rotación 5 + VACUUM INTO ✅; restore sin prueba CI, sin copia externa (OPS-006) |
| Release auditable (SBOM, firma completa) | ⚠️ | OPS-003 |

---

## 7. Plan de remediación (Bloque 3)

| Fase | Ítems | Esfuerzo |
|---|---|---|
| **Quick wins (≤1 semana)** | OPS-001 (unificar rutas + correr benchmarks 1 vez para línea base), OPS-004 (clippy/ruff), OPS-005 (log de versión), OPS-008 (umbral ramas), PERF-003/005/007 | S |
| **Corto plazo (2-4 semanas)** | PERF-001 (virtualizar Detalle + batch de refresco), PERF-002 (proyección ligera), OPS-002 (pytest + smoke daemon en CI), PERF-004/INT-004 (daemon: timeout/backoff/onedir) | M |
| **Mediano plazo** | PERF-006/008/009/010/011, OPS-003 (SBOM), OPS-006 (test restore + investigar hangdump), OPS-007 (pinning Python, rollback) | M |

**Comandos de verificación:**
```powershell
# Línea base de benchmarks (tras unificar rutas)
.\run_benchmarks_and_reports.ps1 -Target benchmarks

# Análisis read-only de la BD (reproducible)
python -c "import sqlite3;c=sqlite3.connect('file:%s?mode=ro'%r'%LOCALAPPDATA%\AnimeLocalTrackerData\biblioteca.db'.replace('\\','/'),uri=True).cursor();[print(r) for r in c.execute('EXPLAIN QUERY PLAN SELECT * FROM RegistroEpisodio WHERE VistoLocal=1 AND SincronizadoEnNube=0')]"

# Gates actuales
powershell -ExecutionPolicy Bypass -File .\build.ps1 -RunTests -Coverage
```

---

## 8. Limitaciones y no verificado

1. **No hay números de benchmark que citar** (nunca ejecutados): primera acción del plan es generar la línea base. Frame times, tiempos de decode y extracción onefile no medidos.
2. Rollback real de Velopack y comportamiento runtime de Flyleaf no verificables estáticamente.
3. El TRX más reciente registraba 162/218 ejecutados (run parcial o corte por el hang de OPS-006): la suite completa en verde no está confirmada en esta entrega.
4. El WAL pendiente de 3,9 MB se observó en un momento puntual; sin medición longitudinal.
5. Análisis de privacidad: fuera de este bloque (Bloque 4).

*Firmado por SRE/rendimiento. Sin cambios sobre el código: solo propuestas.*
