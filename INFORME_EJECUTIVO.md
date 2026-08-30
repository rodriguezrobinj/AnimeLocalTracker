# 📊 INFORME EJECUTIVO — AnimeLocalTracker
### Análisis profundo del estado del proyecto · Pruebas de rendimiento reales · Mejoras · Optimizaciones · Integraciones

**Fecha del análisis:** 29/08/2026 · **Máquina de referencia:** Intel Core i5-7300U (2C/4T) · Windows 10 22H2 · .NET SDK 8.0.424 · .NET Runtime 8.0.30 (RyuJIT x64-v3, GC Workstation)

---

## 1. Resumen Ejecutivo (TL;DR)

| Dimensión | Estado | Veredicto |
|---|---|---|
| Funcionalidades | Reproductor Flyleaf, AniSkip, auto-tracking 90%, descargas segmentadas, calendario, tendencias, galería virtualizada, daemon Python, núcleo Rust FFI | 🟢 Muy completo para v1 |
| Calidad de código | MVVM estricto, DI, `GeneratedRegex`, cachés multi-nivel, logger canalizado | 🟢 Sólida |
| Pruebas | **131/131 correctas** (47 s) — README dice 98 (desactualizado) | 🟢 Sobra a la meta |
| Rendimiento | Seeking a **328 ns/op**, bulk DB 500 filas en **21,8 ms**, parser de archivos **6,4 µs/12 formatos** | 🟢 Excelente |
| Distribución | Velopack + Inno Setup; instalador ~165 MB (binario Python embebido) | 🟡 Pesado |
| Estado de trabajo | **12 archivos modificados sin commitear** (módulo Rust FFI en desarrollo activo) | 🟡 En curso |
| Riesgo principal | Scraper de animeav1.com (regex sobre HTML, sin tests, sin throttling) + dependencia de `ffmpeg.exe` en PATH que no se distribuye | 🟠 Medio |

**Conclusión ejecutiva:** El proyecto está **maduro y bien ingenierizado** — notablemente por encima del promedio de apps WPF personales: hay cachés de 2 niveles, canalización asíncrona de logs, transacciones SQLite optimizadas, virtualización visual, un daemon Python con protocolo JSON-lines y un núcleo Rust (FFI) para parsing y miniaturas. La prioridad no es añadir más funcionalidad, sino **blindar lo existente**: fijar la cadena de fallback Rust→Python, eliminar el scraping frágil, distribuir `ffmpeg.exe`, y automatizar CI/CD (hoy no hay GitHub Actions pese al badge del README).

---

## 2. Estado Actual del Proyecto

### 2.1 Métricas generales

| Métrica | Valor |
|---|---|
| Proyectos en solución | 4 (`App` WPF, `Tests` xUnit, `Benchmarks` BenchmarkDotNet, `animetracker_core` Rust cdylib) |
| Código C# (app) | ~9.000 líneas / ~60 archivos (115 incl. XAML/converters) |
| Tests | 21 archivos · **131 tests, 0 fallidos** (47 s de ejecución) |
| Benchmarks | 9 benchmarks reales (ver sección 4) |
| Rust | 4 módulos (`lib.rs`, `parser.rs`, `hasher.rs`, `spritesheet.rs`) + Rayon |
| Python | 10 comandos de daemon, 6 módulos, compilado a `AnimeTrackerTools.exe` (PyInstaller) |
| Último commit | `feat(native): modulo nativo en Rust FFI...` (29/08/2026) |
| Working tree | 🟡 12 archivos modificados / 1 nuevo (`spritesheet.rs`) sin commitear |

### 2.2 Stack tecnológico

```
┌──────────────────────────────────────────────────────────────┐
│  UI: WPF + MaterialDesignInXaml 5.3 + VirtualizingWrapPanel  │
│  Arquitectura: MVVM (CommunityToolkit.Mvvm 8.4 + Messenger)  │
├──────────────────────────────────────────────────────────────┤
│  Reproductor: FlyleafLib 3.11 (DirectX11/FFmpeg 7.x DLLs)    │
│  Persistencia: SQLite (sqlite-net-pcl, modo WAL + índices)   │
│  HTTP: IHttpClientFactory + Polly (retry 429/5xx)            │
│  Logs: System.Threading.Channels (batch 500ms, rotación 5MB) │
│  Actualizaciones: Velopack 1.2                               │
├──────────────────────────────────────────────────────────────┤
│  Núcleo nativo: Rust (anitomy-pure, FNV-1a, Rayon, ffmpeg)   │
│  Automatización: daemon Python (JSON-lines) + yt-dlp         │
│  APIs externas: AniList GraphQL, AniSkip REST, animeav1.com  │
└──────────────────────────────────────────────────────────────┘
```

---

## 3. Funcionalidades Implementadas (mapa con ejemplos)

### 3.1 Reproductor de élite (Flyleaf)
- **Seek por keyframe con coalescing "último-gana"** — `ReproductorViewModel.SolicitarSeekNativo` (máx. ~4 seeks/s, evita seeks fuera de orden de Flyleaf):
  ```csharp
  if (transcurrido >= IntervaloMinimoSeek) { AplicarSeekNativo(segundos); return; }
  _seekPendiente = segundos; // objetivo MÁS RECIENTE
  ```
- **Ventana de "settle" de 900 ms** tras un seek para que la barra no "rebote" (`_settleHastaUtc`).
- **Buffer de demuxer de 30 s en RAM** (`config.Demuxer.BufferDuration = 300_000_000L`), decodificador multi-hilo.
- **Hover thumbnail preview**: sprite sheet de 60 fotogramas generado en Rust FFI en paralelo (fallback a extracción de frame individual); corte instantáneo en RAM vía `CroppedBitmap`.
- **Reanudación exacta al milisegundo** con reglas de negocio en `PlaybackStateService` (mín. 5 s para reanudar, 95 % = visto, re-marcar como no visto si se abandona a medias).

### 3.2 AniSkip + auto-play
- **Doble fuente**: API comunitaria AniSkip (vía MAL ID memoizado) → detección local de escenas con el daemon Python (norm L1 + detector de ventanas de densidad).
- **Auto-skip con deduplicación por clave** (`{tipo}_{start:F1}`) y auto-play del siguiente episodio tras 1,5 s.
- Skip manual con tecla `S`, botones OP/ED/Recap con iconos dinámicos.

### 3.3 Auto-tracking al 90 % + sync offline
- Bucle de tracking con **sondeo adaptativo 250 ms/1 s** y guardado cada 5 s.
- Al alcanzar 90 %: `MarcarComoVistoYSincronizarAsync` → SQLite + AniList GraphQL en vivo.
- `SyncService` periódico cada 5 min con `SemaphoreSlim` anti-reentrada: agrupa por anime y envía **el máximo episodio** (`grupo.Max(e => e.NumeroEpisodio)`), con fallback offline (`SincronizadoEnNube`).

### 3.4 Biblioteca, catálogo y calendario
- Galería con `ICollectionView` (filtros: texto/estado/género/pendientes/carpeta + 7 criterios de orden) y **caché de imágenes 2 niveles** (RAM 500 entradas ≈ 135 MB + disco).
- Búsqueda en vivo con **debounce de 400 ms + CTS** y cancelación segura de la anterior.
- Tendencias (24 ítems, caché 15 min), calendario semanal con caché de 5 min y **retry al navegar si quedó vacío** (anti rate-limit).
- "Qué veo hoy": elige anime CURRENT con siguiente episodio sin ver y navega directo al reproductor.

### 3.5 Descargas
- **6 segmentos paralelos** con `RandomAccess.WriteAsync` + preasignación de archivo; reanudable vía `.state`.
- Gestor de **slots redimensionable en caliente** (cola FIFO de `TaskCompletionSource<bool>`), pausa/reanudación/cancelación, límite configurable.
- Resolución de fuente: yt-dlp (daemon Python) → scraper animeav1 (regex) → MP4Upload.

### 3.6 Seguridad y UX
- **Token AniList cifrado con DPAPI** (CurrentUser) + migración automática de tokens legacy en texto plano + validación de `state` anti-CSRF.
- Logger asíncrono no bloqueante con `Channel` unbounded, batching de 128 entradas / 500 ms y rotación de 5 MB.
- Fullscreen real con `WM_GETMINMAXINFO` (maximizar respeta taskbar), F11, overlay que se desvanece.
- Diálogos vía `AsyncRequestMessage<bool>` desacoplados (VM→MainViewModel).

---

## 4. Pruebas de Rendimiento (ejecutadas hoy)

**Entorno:** i5-7300U (2 núcleos físicos, 4 lógicos, 2,6 GHz) · Windows 10 · power plan "Alto rendimiento" · Release + BenchmarkDotNet 0.15.8.

### 4.1 Reproductor (6 benchmarks, CPU de gama baja)

| Benchmark | Media | Memoria/op | Rango (Min–Max) |
|---|---|---|---|
| Evaluación auto-tracking 90 % (1.000 ticks) | **1,43 µs** | 0 B | 1,41–1,46 µs |
| Resolución Anterior/Siguiente (100 eps) | **31,2 µs** | 8,2 KB | 27,8–33,3 µs |
| Seeking aleatorio (100 saltos / 24 min) | **32,8 µs** | 12 KB | 32,0–33,7 µs |
| Formateo de tiempo (1.000 ticks) | **149,9 µs** | 80 KB | 147,3–156,1 µs |
| Resolución Anterior/Siguiente (1.000 eps) | **222,7 µs** | 41,5 KB | 214,9–227,2 µs |
| Seeking continuo (1.440 seeks / 24 min) | **472,3 µs** | 172,8 KB | 466,1–479,2 µs |

➡️ **Lectura:** El seeking lógico cuesta **~328 ns por operación** (472 µs ÷ 1.440) incluso en un i5 de 2 núcleos; la resolución prev/siguiente escala de 31 µs → 223 µs al pasar de 100 a 1.000 episodios (crecimiento sub-lineal, OK). El formateo de tiempo asigna 80 bytes/tick (candidato a optimización con `string.Create`/pooling, ver §6).

### 4.2 Base de datos SQLite (modo WAL)

| Benchmark | Media | Memoria |
|---|---|---|
| Bulk upsert de **500 registros en 1 transacción** | **21,8 ms** (mediana 21,9) | 2,6 MB |
| Consulta completa de 500 registros | **116,8 µs** | 18,1 KB |

➡️ **Lectura:** 500 filas en 21,8 ms ≈ **22.900 upserts/seg** con el patrón 1 SELECT + 1 UPDATE/INSERT masivo (sin N+1). Historial de BenchmarkHistoryManager marca la corrida como ✅ ESTABLE (+1,7 % vs anterior). El test de estrés `DatabaseServiceStressTests` verifica 5.000 registros bulk < 5 s.

### 4.3 File Scanner (regex `GeneratedRegex`)

| Benchmark | Media | Memoria |
|---|---|---|
| `ExtraerNumeroEpisodio` sobre **12 formatos reales** (fansub, `Ep 05`, `E1071`, `Cap 01`, resoluciones...) | **6,42 µs** | 5,4 KB |

➡️ **Lectura:** ~535 ns/archivo; un escaneo de 1.000 archivos cuesta **~6 ms** en CPU. Distribución bimodal (outliers de JIT), sin impacto práctico.

### 4.4 Tests de estrés (integrados en xUnit, 47 s total)

| Prueba | Umbral | Resultado |
|---|---|---|
| 1.440 seeks continuos | < 500 ms | ✅ |
| 1.000 seeks aleatorios | < 150 ms | ✅ |
| 500 cambios de episodio | < 5 s | ✅ |
| Maratón 24 episodios + 100 ciclos de vida del reproductor | — | ✅ |
| 100 navegaciones entre vistas + 200 mensajes de progreso | — | ✅ |
| 50 consultas concurrentes a SQLite | — | ✅ |

### 4.5 Hallazgo de infraestructura de benchmarks
`DatabaseBenchmarks` y `FileScannerBenchmarks` **fallan con el timeout por defecto de BenchmarkDotNet (120 s)** al recompilar el boilerplate (build WPF lento con `BuildInParallel=false`). Se resuelven con `--buildTimeout 900`, pero el script `run_benchmarks_and_reports.ps1` no lo pasa → **hay que corregirlo** (§6).

---

## 5. Puntos Fuertes (lo que está bien y por qué)

1. **Caché multi-nivel en toda la app** — Portadas (RAM 500 + disco), miniaturas (sprite sheet RAM + disco + MD5 determinista), API AniList (por-id 30 min, búsqueda 10 min, calendario 5 min), skip-times (2 h), MAL-ID memoizado. Verificado por tests (caché de calendario, invalidation por media).
2. **Transacciones SQLite sin N+1** — `GuardarRegistrosEpisodioBulkAsync` (DatabaseService.cs:122) hace 1 SELECT + batch de INSERT/UPDATE; `MarcarEpisodiosSincronizadosAsync` 1 SELECT IN + 1 UPDATE. PRAGMAs correctos (WAL, `synchronous=NORMAL`, `temp_store=MEMORY`, cache 64 MB).
3. **Logger que no bloquea al reproductor** — `Channel` unbounded + consumidor único con batch 500 ms + vaciado síncrono en `ProcessExit` + rotación 5 MB. Diseño correcto para logs a alta frecuencia desde el hilo de UI.
4. **Degradación elegante en cascada** — Reproductor (Flyleaf opcional), núcleo Rust (si DLL falta → Python → C#), daemon Python (→ one-shot → nulo). `CreateOptimizedPlayer` devuelve `null!` y la app sigue viva en tests/headless.
5. **Concurrencia bien resuelta en descargas** — Slots FIFO con TCS, resizing en caliente, liberación de slots fuera del lock (evita continuaciones bajo mutex), cancelación que retira al waiter de la cola.
6. **Rust con buena higiene FFI** — Validación de punteros null, `CString` liberado siempre en `finally`, batch paralelo con Rayon, spritesheets en paralelo.
7. **Documentación de bugs en código** — Comentarios que explican incidentes reales (BOM del daemon, `_wpftmp`, saludo síncrono, settle de seeks, respuestas cruzadas del daemon). Valiosísimo para mantenibilidad.
8. **Patrón MVVM sin code-behind** — Todo el flujo vía `WeakReferenceMessenger`; `MainViewModel` dispone al reproductor al salir por cualquier ruta (evita audio fantasma).

---

## 6. Oportunidades de Mejora (priorizadas)

### 🔴 P0 — Estabilidad y seguridad (hacer ya)

| # | Mejora | Dónde | Ejemplo / Impacto |
|---|---|---|---|
| 1 | **`catch_unwind` en todos los exports FFI de Rust** | `native/animetracker_core/src/lib.rs` | Un panic de Rayon cruzando `extern "C"` = UB/cierre del proceso .NET. Envolver cada `#[no_mangle]` en `std::panic::catch_unwind` y devolver error. |
| 2 | **Distribuir `ffmpeg.exe`/`ffprobe.exe`** | `AnimeLocalTracker.csproj` + instalador | Rust (`spritesheet.rs`) y Python (`episode_metadata.py`) ejecutan `ffmpeg` del PATH; si el usuario no lo tiene, miniaturas y enriquecimiento fallan en silencio. Hoy solo se distribuyen las DLLs (avcodec-63.dll, etc.). Añadir `ffmpeg.exe`/`ffprobe.exe` (≈80 MB) o usar la API de Flyleaf para extraer frames y eliminar la dependencia CLI. |
| 3 | **Throttling + retry al scraper animeav1** | `AnimeAv1VideoSourceResolver.cs` | Fase 2 lanza decenas de requests HTTP secuenciales sin límite; `Debug.WriteLine` en vez de `AppLogger`. Poner `SemaphoreSlim` de 2–3 peticiones concurrentes, Polly y loggear a `AppLogger`. |
| 4 | **Atomicidad de `DownloadStateStore` y `release_info.json`** | `DownloadStateStore.cs`, `UpdateService.cs` | Escribir `.state` y `release_info.json` como "write temp + `File.Move(overwrite)`" para no corromper en crash; serializar con lock (hoy múltiples segmentos pueden escribir a la vez). |
| 5 | **Fix `EpisodioItem.BadgeTecnico` (FPS fraccionarios)** | `AnimeLocalTracker/Models/EpisodioItem.cs:103` | ffprobe devuelve `"24000/1001"` y el badge muestra **"24000fps"**; hay que resolver la fracción (23,98). |

### 🟠 P1 — Rendimiento y UX (próximo sprint)

| # | Mejora | Dónde | Ejemplo / Impacto |
|---|---|---|---|
| 6 | **Formateo de tiempo sin GC** | `ReproductorViewModel.ActualizarTextosTiempo` | Asigna 80 B/tick × 4 ticks/s ≈ 320 B/s; con `TimeSpan.TryFormat` en `Span<char>` + `string.Create` → 0 B/tick. |
| 7 | **Índice por `NumeroEpisodio` en DescargasViewModel** | `DescargasViewModel.cs:52` | `FirstOrDefault` O(n) por tick de progreso; un `Dictionary<int, DescargaItem>` deja el tick en O(1). |
| 8 | **Pool acotado para spritesheet (Rust)** | `native/animetracker_core/src/spritesheet.rs` | Lanza hasta **60 procesos ffmpeg simultáneos** con Rayon; limitar con `ThreadPoolBuilder` (p.ej. 4–8) para no saturar CPU/IO. |
| 9 | **`Task.Run(async () => …)` → `async` directo** | `PythonFileScannerService.cs:22` | Anti-patrón: doble salto de hilo; el escaneo ya es `async`. |
| 10 | **Evitar `ObtenerTodosLosAnimesAsync()` en cada alta** | `MainViewModel.SeleccionarYCrearAnimeAsync:513` y `AgregarAnimeViewModel.cs:225` | N+1 sobre BD: reutilizar el set de IDs ya cargado (`_animesEnBibliotecaIds`). |
| 11 | **Eliminar dependencia muerta `pydantic`** | `tools/python/pyproject.toml` | No se usa en ningún módulo y engorda `AnimeTrackerTools.exe` (~30 MB). |
| 12 | **Fix `width=0` en fingerprint Python** | `tools/python/media/episode_fingerprint.py:46` | `cap.get(CAP_PROP_FRAME_WIDTH)` se evalúa después de `cap.release()` → siempre 0. |

### 🟡 P2 — Higiene y proceso

| # | Mejora | Ejemplo |
|---|---|---|
| 13 | **CI/CD real (GitHub Actions)** | El README presume badge de "Tests 98/98" pero **no existe `.github/workflows`**; añadir workflow: `dotnet build` → `dotnet test` → `cargo build --release` → opcional `vpk pack`. |
| 14 | **Fix `--buildTimeout` en `run_benchmarks_and_reports.ps1`** | Pasar `--buildTimeout 900` al `dotnet run` de benchmarks (hoy DB y FileScanner fallan en la primera ejecución). |
| 15 | **Corregir `AppId` de Inno Setup** | `installer.iss:12` — `AppId={{D8C3E5A4-…B2C3D}` tiene llaves malformadas; impide identificación canónica del instalador. |
| 16 | **Actualizar README (98 → 131 tests)** | El badge y la sección de pruebas están desactualizados. |
| 17 | **Eliminar binario huérfano `yt-dlp.exe`** | `AnimeLocalTracker/Tools/yt-dlp.exe` no es usado (el daemon Python usa el módulo `yt_dlp` embebido); infla el instalador. |
| 18 | **Limpieza de repo** | `AnimeLocalTracker_0r5ufzfn_wpftmp.csproj` en la raíz sin ignorar; `animetracker_core.dll` commiteado (debe generarse en build). |
| 19 | **Tests que pasan "vacíos"** | `RustNativeTests` e `ImageCacheServiceTests` usan `if (!IsAvailable) return;` → en máquinas sin DLL/carpeta pasan sin probar nada; marcar como `Skip` condicional con mensaje. |
| 20 | **Sincronizar esquema de `db_mock_generator.py`** | El SQL mock no coincide con sqlite-net (PK/columnas distintas) → los datos generados no son utilizables por la app. |

---

## 7. Integraciones: actuales y propuestas

### 7.1 Actuales (7)
1. **AniList GraphQL** — auth OAuth implicit (DPAPI), tracking, búsqueda, tendencias, calendario, perfil.
2. **AniSkip API** — skip-times comunitarios (OP/ED/recap/mixed) con fallback a detección local.
3. **animeav1.com + mp4upload.com** — resolución de streams y descarga segmentada (riesgo: scraping frágil, §6-P0-3).
4. **yt-dlp** — extracción de URLs directas vía daemon Python.
5. **Núcleo Rust (FFI)** — anitomy-pure (parsing fansub), FNV-1a (fingerprint), ffmpeg (spritesheets/frames).
6. **Daemon Python (JSON-lines)** — 10 comandos: parse, enriquecer, thumbnails, escenas, duplicados, extractor, ping.
7. **Velopack + GitHub Releases** — actualizaciones silenciosas con caché offline de 3 niveles.

### 7.2 Propuestas (valor/impacto)
| Integración | Valor | Esfuerzo |
|---|---|---|
| **MyAnimeList (MAL)** | Compatibilidad con el mayor competidor de AniList; arquitectura DI ya preparada (`IAnimeTrackingService` como plantilla) | Medio |
| **TMDB/Kitsu** para posters 4K | Sustituir portadas por `coverImage.extraLarge` en 4K | Bajo |
| **MalSync/AniList al mal-id** ya cubierto; **Jikan v4** como respaldo si AniList cae | Resiliencia | Bajo |
| **Discord Rich Presence** (mostrar "viendo X ep Y") | Diferenciador social | Bajo |
| **Escaneo de carpetas con FileSystemWatcher** (auto-refresco al añadir archivos) | UX local | Medio |
| **Export/Import de biblioteca (JSON/CSV) y backup SQLite** | Seguridad de datos del usuario | Bajo |
| **Búsqueda fuzzy de episodios en AniList por hash de archivo** (Kitsu/AniList "file-hash" scraping de Nyaa) | Auto-match fansubs | Alto |
| **Soporte Windows ARM64 / Linux (WPF → Avalonia)** | Alcance | Muy alto |

---

## 8. Optimizaciones recomendadas (impacto estimado)

| Optimización | Código actual | Propuesta | Impacto |
|---|---|---|---|
| Formateo de tiempo sin asignación | `TimeSpan.ToString()` por tick | `TimeSpan.TryFormat` + `string.Create` | −80 B/tick (~−100 % GC en tracking) |
| O(1) en ticks de descarga | `FirstOrDefault` por mensaje | `Dictionary<int, DescargaItem>` | O(n) → O(1) |
| Spritesheet con pool | 60 procesos ffmpeg | Rayon con 4–8 hilos | −80 % CPU en generación |
| Parser batch en un solo sitio | 4 fuentes de verdad (C# regex, Rust, Python, scraper) | Unificar en Rust FFI con fallback C# | Menos inconsistencia |
| Caché de portadas | Limpiar todo al llegar a 500 | LRU por antigüedad | Evita "recarga de toda la galería" |
| Build | `BuildInParallel=false` global | Restringir solo al proyecto WPF | Builds de tests/benchmarks en paralelo |

---

## 9. Riesgos y Deuda Técnica

1. **Dependencia de scraping web (animeav1/mp4upload)** — Sin tests, sin throttling, regex sobre HTML; se rompe con cualquier cambio del sitio y viola potencialmente ToS. **Mitigación:** aislar detrás de `IVideoSourceResolver` (ya hecho), añadir tests con fixtures HTML y considerar fuentes alternativas (Nyaa RSS).
2. **Rust: panics a través de FFI** — riesgo P0 (§6-1).
3. **Token OAuth legacy (implicit flow)** — El token queda en el historial del navegador; migrar a PKCE cuando AniList lo soporte. El port-squatting en `localhost:5050` es teórico (mitigado por `state`).
4. **Distribución pesada (~165 MB)** — El binario PyInstaller onefile + FFmpeg DLLs; considerar eliminar `pydantic` y `yt-dlp.exe` huérfano, y subir las DLLs de FFmpeg a "opcional".
5. **Fire-and-forget generalizado** — 10+ `_ = Task...` sin observación de excepciones (`UpdateService`, `GaleriaViewModel`, `ConfiguracionViewModel`, `AcercaDeViewModel`); en `AgregarAnimeViewModel.cs:152` es `async void` (peor caso). Un `UnobservedTaskException` global ya captura (App.xaml.cs:62) pero conviene `try/catch` local.
6. **Modelos con lógica de presentación** — `AniListMedia.FormattedStatus`, `AnimeBusquedaItem`, `DescargaItem` mezclan UI con DTO; deuda de diseño menor.
7. **Pruebas "que pasan vacías"** — RustNativeTests/ImageCacheServiceTests condicionadas al entorno: falsa sensación de cobertura.

---

## 10. Roadmap sugerido (8 semanas)

**Sprint 1 (Blindaje):** P0-1 a P0-5 (catch_unwind Rust, ffmpeg.exe distribuido, throttling scraper, atomicidad .state, fix BadgeTecnico) + P1-12 (fingerprint).
**Sprint 2 (Rendimiento):** P1-6 a P1-11 (formateo sin GC, diccionario descargas, pool Rust, limpieza deuda) + tests de regresión de los mismos.
**Sprint 3 (Proceso):** GitHub Actions (build+test+benchmark), fix script de benchmarks, Inno AppId, README actualizado, limpieza de repo.
**Sprint 4 (Valor):** Integraciones de bajo esfuerzo (§7.2): export/backup, FileSystemWatcher, Rich Presence, y evaluar Jikan como respaldo.

---

## 11. Anexo — Comandos de verificación

```powershell
# Tests completos (131)
dotnet test

# Benchmarks (todos; requiere --buildTimeout por el build WPF)
dotnet run --project AnimeLocalTracker.Benchmarks -c Release -- 4 --buildTimeout 900

# Build de Release
dotnet build -c Release

# Núcleo Rust
cargo build --release --manifest-path native/animetracker_core/Cargo.toml

# Reporte histórico (tras ejecutar benchmarks)
.\run_benchmarks_and_reports.ps1 -Target history
```
