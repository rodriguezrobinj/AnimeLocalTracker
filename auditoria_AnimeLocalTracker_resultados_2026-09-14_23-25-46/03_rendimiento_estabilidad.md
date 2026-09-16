---
Proyecto: AnimeLocalTracker
Fase: 3 - Rendimiento y Estabilidad
Fecha de auditoría: 2026-09-14
Hora de inicio: 23:33:00
Duración de la fase: 25:00
---

# Fase 3 — Rendimiento y Estabilidad

## 1. Build, tests y flake conocido — verificado

`dotnet build` en frío falló inicialmente con 16 errores `CSC : error CS2001` (fuentes `.g.cs`
de XAML no encontradas) — **este es exactamente el "flake conocido del build WPF" que el propio
`AGENTS.md:48` documenta** ("si `obj` queda con BAMLs bloqueados... matar procesos
`dotnet`/`MSBuild`/`testhost` colgados y borrar `obj`"). Se siguió esa guía (matar procesos
`dotnet.exe` colgados, borrar `AnimeLocalTracker/obj`) y el build resultante fue limpio:

- **Build Debug**: 64s, **0 advertencias, 0 errores** (confirma `TreatWarningsAsErrors=true`
  real, no solo declarado).
- **Tests**: `dotnet test` → **335/335 pruebas superadas, 0 fallidas**, 23s de ejecución.
  **Discrepancia menor con el README**: el badge declara "327/327 Tests Passing", el conteo real
  actual es **335** — 8 tests más de los documentados (desviación en dirección positiva/inocua,
  pero confirma que el badge no se actualiza en cada commit; ver Fase 6).

## 2. HALLAZGO CRÍTICO: el pipeline de Benchmarks reporta "éxito" en CI mientras está roto

**Severidad: Alta (calidad de proceso / observabilidad de CI — falso positivo).**

Al intentar ejecutar el proyecto `AnimeLocalTracker.Benchmarks` (requisito explícito de esta
fase: "documenta sus resultados actuales como línea base"), la compilación falla:

```
ReproductorBenchmarks.cs(116,42): error CS0535: 'ReproductorBenchmarks.DummyDatabaseService'
no implementa el miembro de interfaz 'IDatabaseService.ConservarRegistroTrasEliminarArchivoAsync(int, int)'
... (+ 4 miembros más de IDatabaseService no implementados)
```

El mock `DummyDatabaseService` dentro de `ReproductorBenchmarks.cs` quedó desalineado con
`IDatabaseService` (la interfaz creció con nuevos miembros que el stub del benchmark nunca
implementó) — **el proyecto de Benchmarks no compila en el HEAD actual del repositorio.**

**Lo más importante de este hallazgo no es que esté roto, sino que nadie lo sabe:** se verificó
con `gh run list --workflow benchmarks.yml` que el workflow "Benchmarks (manual / semanal)" tiene
runs recientes marcados **`success`** (2026-09-07 y 2026-09-14, ambos en ~1-2 minutos — demasiado
rápido para una ejecución real de BenchmarkDotNet, que normalmente toma varios minutos por cada
categoría). El log completo del run del 2026-09-14T08:16 (`gh run view 34821871920 --log`)
confirma la causa raíz exacta:

```
##[error]...ReproductorBenchmarks.cs(116,42): error CS0535: ... (los mismos 5 errores)
The build failed. Fix the build errors and run again.
Proceso completado exitosamente.          <-- se imprime SIEMPRE, sin comprobar el resultado
```

**Causa raíz (confirmada leyendo `run_benchmarks_and_reports.ps1`):** la línea 35 ejecuta
`dotnet run ... -c Release -- $BenchmarkCategory` **sin comprobar `$LASTEXITCODE`**, y la línea 45
imprime incondicionalmente `"Proceso completado exitosamente."` al final del script,
independientemente de si el paso de benchmarks (o el de tests, línea 25-30, que sí comprueba el
código de salida pero solo para el mensaje en consola, no para el exit code del script) falló.
Como el script termina con exit code 0 pase lo que pase, el step de GitHub Actions —y por tanto
el job entero— se reporta como verde.

**Impacto:** el objetivo declarado del propio workflow (comentario `OPS-01`: "los benchmarks de
rendimiento existían pero nunca se ejecutaban ni en CI ni de forma persistida... genera la línea
base y el historial comparativo") **lleva al menos desde el 2026-09-07 sin cumplirse** —
cero reportes de rendimiento se han generado ni subido como artefacto en las últimas ejecuciones
programadas (el propio log muestra `##[warning]No files were found with the provided path:
BenchmarkHistory/**`), pese al checkmark verde. Este es un caso de libro de "CI decorativo": la
señal existe, pero no mide lo que dice medir.

**Remediación sugerida:** añadir `if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }` después de
cada paso relevante en `run_benchmarks_and_reports.ps1` (tests y benchmarks), y arreglar
`DummyDatabaseService` implementando los 5 miembros faltantes de `IDatabaseService`
(`ConservarRegistroTrasEliminarArchivoAsync`, `ObtenerHistorialEpisodiosAsync`,
`LimpiarRegistroHistorialAsync`, `LimpiarTodoElHistorialAsync`, `VaciarBibliotecaAsync`) o
delegar a un stub compartido/`Moq` en vez de una implementación manual completa, para que no se
desalinee de nuevo cada vez que crezca la interfaz. **No se aplicó el fix** (Regla 4: modo
seguro, no se modifica código de producción/tooling durante la auditoría) — queda documentado
como hallazgo accionable.

## 3. Arranque en frío y consumo de recursos en reposo (build Debug local)

Medido con `Start-Process` + polling de `MainWindowHandle` y muestreo de `Process.WorkingSet64`
(no se instaló `dotnet-trace`/`dotnet-counters` para ETW completo por límite de tiempo de esta
corrida — el muestreo directo de `System.Diagnostics.Process` ya da una medición real, aunque
menos granular que un trace ETW):

| Métrica | Valor medido |
|---|---|
| Tiempo hasta `MainWindowHandle` visible | **0.31 s** |
| RAM (Working Set) a los 5 s de iniciado | 226.9 MB |
| RAM (Working Set) a los 15 s de iniciado | 275.8 MB |
| CPU acumulada en los primeros ~5 s | 5.64 s (multi-hilo: 24 threads) |
| Handles abiertos | 640 |
| Daemon Python (`AnimeTrackerTools.exe`) tras iniciar la app | **2 procesos activos simultáneos** (6.4 MB y 65.8 MB) |
| Daemon Python tras cerrar la app (`Stop-Process` + 2 s) | 0 procesos — **cierre limpio confirmado** |

**Advertencias sobre esta medición (léase antes de usar estos números como baseline oficial):**
- Es un **build Debug local**, no el binario Release/publicado que reciben los usuarios (sin
  trimming, sin ReadyToRun, con símbolos de depuración) — el arranque en frío real de la versión
  publicada probablemente sea más rápido en JIT pero el tamaño en disco es mayor.
- `MainWindowHandle` visible no equivale a "UI interactiva y renderizada" — es una cota inferior
  aproximada del arranque, consistente con la metodología típica pero no exhaustiva.
- Disco/caché de SO ya estaban "calientes" (se acababa de compilar el proyecto) — un cold start
  tras reinicio completo del sistema sería un dato más representativo y no se pudo obtener en
  esta corrida sin reiniciar la máquina del usuario (acción invasiva fuera de alcance).

**Hallazgo (Severidad: Baja, a confirmar con más muestreo):** el incremento de RAM de 226.9 MB a
275.8 MB (+49 MB) entre el segundo 5 y el segundo 15 **sin interacción del usuario** podría ser
JIT/carga perezosa de recursos (MaterialDesign, imágenes cacheadas, inicialización de Flyleaf) o
el comienzo de una fuga — una sola muestra de 10 segundos no es concluyente. Recomendación:
perfilar con `dotnet-counters monitor -p <pid> --counters System.Runtime` durante una sesión de
uso real de 10+ minutos para diferenciar "warm-up normal" de una fuga sostenida.

**[ACTUALIZADO 2026-09-15 — investigado durante la ronda de correcciones] Confirmado: es diseño
intencional, no un bug.** Lectura completa de `PythonBridgeService.cs` confirma dos rutas de
proceso distintas y deliberadas: (1) un único daemon persistente (`--daemon`, campo estático
`_daemonProcess`, arrancado perezosamente en la primera llamada y reutilizado) para comandos
frecuentes/baratos, y (2) procesos "one-shot" spawneados puntualmente vía
`EjecutarOneShotAsync`/`ExecuteCommandOneShotAsync` para comandos largos (ej. descargas HLS) que
no deben bloquear la cola serializada del daemon (comentario del propio código, línea 68-74, lo
documenta explícitamente). Ver los dos procesos coexistiendo brevemente al arrancar es el
comportamiento esperado cuando una llamada de arranque usa la ruta one-shot mientras el daemon
también se está iniciando — no requiere corrección.

## 4. Reproducción de video (FlyleafLib/DirectX 11) — NO EVALUADO

**No se pudo evaluar el rendimiento real de decodificación (caída de frames, uso de GPU/CPU en
HEVC/AV1/10-bit)** porque requiere un archivo de video real y una sesión de reproducción activa
con herramientas de perfilado de GPU (PresentMon, GPU-Z o `dotnet-trace` con proveedores ETW de
DirectX) — ninguna biblioteca de anime local está configurada en este entorno de auditoría, y
generar/descargar contenido de video con derechos inciertos para esta prueba está fuera de
alcance. Se marca explícitamente como **"no evaluado"** en vez de asumir un resultado.
**Recomendación para una corrida futura:** preparar un directorio con 2-3 clips cortos de prueba
libres de derechos (ej. Big Buck Bunny en HEVC/AV1) y repetir esta fase con `dotnet-trace` activo
durante la reproducción.

## 5. Resiliencia

- **SQLite en modo WAL**: confirmado en código (`DatabaseService.cs:59`:
  `PRAGMA journal_mode=WAL;`), consistente con el claim del README.
- **Backup atómico**: confirmado uso de `VACUUM INTO` (no `File.Copy` sobre la DB abierta) según
  comentario en `DatabaseService.cs:172` y la invariante ya documentada en `AGENTS.md:18`.
- **Cierre inesperado / pérdida de progreso no sincronizado**: no se probó activamente un `kill -9`
  del proceso durante una reproducción activa en esta corrida (requeriría reproducción de video
  real, ver §4). Basado en el código: el progreso se persiste localmente en SQLite (no solo en
  memoria) y el umbral del 90% dispara el guardado — un cierre abrupto *antes* de cruzar el 90%
  de un episodio perdería ese progreso puntual por diseño (comportamiento esperado y documentado
  implícitamente, no un bug), pero no se verificó si hay guardados incrementales de
  `ProgresoSegundos` durante la reproducción (el campo existe, ver
  `LimpiarRegistroHistorialAsync` en `DatabaseService.cs:623` que lo resetea) — recomendación:
  confirmar con el desarrollador la cadencia real de guardado incremental.
- **Cierre del daemon Python**: confirmado empíricamente en esta fase (§3) — 0 procesos
  `AnimeTrackerTools.exe` huérfanos tras cerrar la app, consistente con el claim de
  `AGENTS.md:21`.

## 6. Resumen de hallazgos

| # | Hallazgo | Severidad |
|---|---|---|
| 1 | Pipeline de Benchmarks CI reporta éxito falso — proyecto no compila desde ≥2026-09-07 | **Alta** |
| 2 | Badge de tests del README desactualizado (327 vs. 335 reales) | Baja |
| 3 | Posible doble-spawn del daemon Python al arrancar (a confirmar) | Informativa |
| 4 | Incremento de RAM +49MB en primeros 15s (a confirmar con perfilado extendido) | Baja |
| 5 | Rendimiento de reproducción de video (FlyleafLib/DirectX) | No evaluado — requiere fixture de video |
| 6 | SQLite WAL + backup atómico + cierre limpio del daemon Python | Ninguna (positivo, verificado) |
