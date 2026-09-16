---
Proyecto: AnimeLocalTracker
Fase: 7 - Consolidación
Fecha de auditoría: 2026-09-14
Hora de inicio: 00:41:00
Duración de la fase: 05:00
---

# Fase 7 — Consolidación

## 1. Registro temporal de la corrida

| Fase | Inicio (aprox.) | Duración (aprox.) |
|---|---|---|
| 0 — Contexto | 23:25:46 | 06:30 |
| 1 — Seguridad | 23:32:00 | 18:00 |
| 2 — Arquitectura | 23:50:00 | 15:00 |
| 3 — Rendimiento | 23:33:00* | 25:00 |
| 4 — Diseño/UX-UI | 23:59:00 | 20:00 |
| 5 — Observabilidad | 00:19:00 | 12:00 |
| 6 — Distribución | 00:31:00 | 10:00 |
| 7 — Consolidación | 00:41:00 | 05:00 |

*Nota: esta auditoría se ejecutó como una sesión continua de un agente que en algunos tramos
preparó/ejecutó pasos de más de una fase en paralelo (ej. builds de Fase 3 se dispararon mientras
se redactaba el informe de Fase 1) — los horarios de inicio por fase son aproximados y no
estrictamente secuenciales minuto a minuto; la duración total real de la corrida, de inicio a fin,
es la medida más fiable: **2026-09-14 23:25:46 → 2026-09-14 23:44:43, ~19 minutos** de ejecución
efectiva de comandos (el resto del tiempo de la sesión se invirtió en análisis y redacción, no
reflejado en timestamps de shell).

## 2. Matriz de riesgos priorizada (Impacto × Probabilidad × Esfuerzo de solución)

| # | Hallazgo | Fase | Impacto | Probabilidad de afectar a un usuario | Esfuerzo de fix | Prioridad |
|---|---|---|---|---|---|---|
| 1 | Pipeline de Benchmarks reporta éxito falso en CI (script no propaga errores) | 3 | Medio (deuda técnica silenciosa, no afecta directamente al usuario final) | Alta (ya está pasando en cada run programado) | Bajo (2 líneas de PowerShell + arreglar el mock) | **P1 — Alta** |
| 2 | 32.6% de controles interactivos sin nombre accesible (incl. todas las tarjetas de póster) | 4 | Alto (bloquea el uso con lector de pantalla) | Media (afecta solo a usuarios con lector de pantalla activo, pero totalmente para ellos) | Bajo (`AutomationProperties.Name` en binding existente) | **P1 — Alta** |
| 3 | Instalador/ejecutable sin firma Authenticode | 1, 6 | Alto (fricción de confianza + SmartScreen en el primer contacto) | Alta (afecta a el 100% de instalaciones nuevas) | Medio (coste recurrente de certificado + integrarlo al pipeline de release) | **P1 — Alta** |
| 4 | Barra de pestañas rompe layout en ventanas <~800px | 4 | Medio (usuarios con ventana pequeña/monitores modestos) | Media | Bajo (WrapPanel o menú colapsable) | P2 — Media |
| 5 | ViewModels sobredimensionados (`ReproductorViewModel`, `DetalleViewModel`) | 2 | Bajo directo al usuario / Medio a mantenibilidad futura | N/A (deuda técnica) | Alto (refactor estructural, requiere tests de regresión) | P3 — Estructural |
| 6 | README sin capturas de pantalla | 6 | Bajo (conversión de visitantes del repo) | N/A | Muy bajo | P2 — Quick win |
| 7 | Badge de tests desactualizado (327 vs 335) | 3, 6 | Muy bajo | N/A | Muy bajo (automatizar el badge desde CI) | P3 — Quick win menor |
| 8 | `CHANGELOG.md` inexistente | 6 | Bajo | N/A | Bajo | P3 — Quick win |
| 9 | Posible doble-spawn del daemon Python al iniciar | 3 | Desconocido (a confirmar) | Desconocida | Desconocido hasta investigar | P2 — Investigar antes de priorizar |
| 10 | Rama `copilot/audit-integral-proyecto` con auditoría paralela ya ejecutada | 2 | N/A (gobernanza) | N/A | N/A | Acción del usuario: revisar y decidir si fusionar hallazgos |

## 3. Roadmap de mejoras

### Quick wins (bajo esfuerzo / alto impacto — priorizar primero)

1. **Arreglar el `run_benchmarks_and_reports.ps1`** para que propague el exit code real (`exit
   $LASTEXITCODE` tras cada paso) y reparar `DummyDatabaseService` en `ReproductorBenchmarks.cs`
   implementando los 5 miembros faltantes de `IDatabaseService` — restaura una señal de CI que
   hoy está mintiendo.
2. **Añadir `AutomationProperties.Name`** a las tarjetas de anime de la galería y a los ~5
   controles de cabecera identificados sin nombre — desbloquea el uso con lector de pantalla de
   la función central de la app, reutilizando el binding de título ya existente.
3. **Añadir capturas de pantalla reales al README** (las 3 generadas en esta auditoría son un
   punto de partida válido) — cierra la brecha entre el discurso visual del README y su
   contenido real.
4. **Colapsar/reflow la barra de pestañas de filtro** por debajo de un umbral de ancho de
   ventana, siguiendo el mismo patrón responsive que ya funciona en la grilla de pósteres.
5. **Crear un `CHANGELOG.md`** a partir del historial de conventional commits ya en uso.
6. **Enlazar directamente a `/issues/new`** desde la vista "Acerca de" en vez de (o además de) la
   raíz del repositorio.

### Estructurales (requieren planificación)

1. **Firmar digitalmente el instalador y el ejecutable** (Authenticode) — decisión de producto/
   negocio (coste recurrente) más que puramente técnica; impacta directamente la tasa de
   conversión de "30 segundos" prometida en el README.
2. **Refactorizar `ReproductorViewModel` (1468 líneas) y `DetalleViewModel` (1274 líneas)**
   extrayendo sub-orquestadores dedicados, siguiendo el patrón ya usado parcialmente
   (`SkipTimesCoordinator` como precedente) — reduce riesgo de regresión al tocar lógica de
   reproducción/detalle en el futuro.
3. **Investigar y, si aplica, corregir el posible doble-spawn del daemon Python** al arranque —
   requiere primero confirmar si es intencional revisando `PythonBridgeService.cs` con más
   profundidad de la que permitió esta corrida.
4. **Establecer una cadencia de perfilado de rendimiento real** (arranque en frío desde binario
   Release publicado, reproducción de video con fixtures libres de derechos, `dotnet-trace`
   durante sesiones de uso prolongado) — esta auditoría solo pudo dar una aproximación con el
   build Debug local; un baseline real requiere el entorno y las herramientas que no estaban
   disponibles en esta corrida (ver limitaciones documentadas en cada fase).

## 4. Cumplimiento de requisitos funcionales base — resumen

No se encontró ningún incumplimiento de los requisitos funcionales declarados en el README/
AGENTS.md: MVVM respetado, SQLite WAL + backup atómico funcionando, índices compuestos reales,
interop Rust/Python con mitigaciones de resiliencia ya implementadas, CI bloqueante real
(confirmado con runs de GitHub Actions, no solo YAML), suite de tests pasando al 100% (335/335).
Los hallazgos de esta auditoría son mayoritariamente de **calidad de proceso** (CI de benchmarks
mintiendo), **accesibilidad** y **fricción de distribución** — no de funcionalidad rota.
