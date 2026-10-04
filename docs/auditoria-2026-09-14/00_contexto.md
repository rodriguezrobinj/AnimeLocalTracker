---
Proyecto: AnimeLocalTracker
Fase: 0 - Descubrimiento y Contexto
Fecha de auditoría: 2026-09-14
Hora de inicio: 23:25:46
Duración de la fase: 06:30
---

# Fase 0 — Descubrimiento y Contexto

## 1. Metodología y nota sobre el entorno de ejecución

Esta auditoría se ejecuta dentro de una sesión de Claude Code (agente de línea de comandos)
sobre la máquina local Windows del desarrollador, con acceso a shell (Git Bash/PowerShell),
.NET CLI y lectura de archivos. **No hay un entorno de automatización GUI (FlaUI, Accessibility
Insights for Windows) preinstalado**, ni una base de datos NVD local para OWASP Dependency-Check.
Siguiendo la Regla 3 (máx. 3 intentos, documentar como deuda técnica y continuar) y la Regla 6
(fases sin red: marcar explícitamente "no evaluado"), cada fase indicará con precisión:
- Qué se verificó con evidencia directa (código, salida de comandos reales).
- Qué se instaló/ejecutó bajo demanda.
- Qué se degradó a un método más liviano (p. ej. `dotnet list package --vulnerable` en vez de
  OWASP Dependency-Check completo, que requiere una descarga de la base NVD de varios GB y
  puede tardar >45 min en un primer run) — siempre indicando el motivo.
- Qué queda fuera de alcance por limitaciones del entorno, marcado explícitamente como tal, sin
  fabricar resultados.

## 2. Estructura del repositorio (confirmada)

```
AnimeLocalTracker/                  # App WPF principal (C# 12, .NET 8, MVVM)
AnimeLocalTracker.Tests/            # xUnit + FluentAssertions (327 tests, README/AGENTS.md)
AnimeLocalTracker.Benchmarks/       # Proyecto de benchmarking
native/animetracker_core/           # Crate Rust (cdylib + rlib) — 446 líneas en src/
tools/python/                       # Paquete Python (setuptools) — ~1436 líneas .py
.github/workflows/                  # ci.yml, benchmarks.yml, release.yml
.agents/                            # Reglas y skills para agentes de IA (wpf-mvvm, polyglot-ffi, etc.)
AGENTS.md                           # Guía interna para agentes — MÁS actualizada/precisa que el README
```

## 3. Resolución de la discrepancia Rust/Python (bloqueante para Fases 1-3)

**Conclusión: Rust y Python SÍ son componentes de producción integrados en el ejecutable que
reciben los usuarios finales — NO son tooling interno de build/CI únicamente.**

Esto contradice la premisa del brief de auditoría original (que asumía, citando una versión
anterior de la tabla de arquitectura del README, que el stack era "puramente .NET"). **El
README fue reescrito el 2026-09-14 (commit `3d5cdef`, "reescribir README con enfoque premium y
tono de marketing profesional")** y la versión actual, en la sección "🏗️ Ingeniería de Software
de Grado Empresarial" (línea 48), sí menciona explícitamente: *"un núcleo nativo ultrarrápido
programado en Rust (FFI), y herramientas analíticas delegadas a un daemon en Python"*. Es decir,
la discrepancia documental que motivó esta fase ya no existe en el HEAD actual del repo — el
README y `AGENTS.md` están alineados en que el stack es políglota. Esto en sí mismo es un
hallazgo para la Fase 6 (verificación de claims): el brief de auditoría fue escrito contra una
versión desactualizada de la documentación, lo que subraya el riesgo de auditar sin releer el
estado real del repo en cada corrida.

### Evidencia del mecanismo de integración real

**Rust → C# (FFI vía P/Invoke source-generated, no proceso separado):**
- `native/animetracker_core/Cargo.toml`: `crate-type = ["cdylib", "rlib"]` — compila a DLL nativa.
- `AnimeLocalTracker/Services/Native/NativeMethods.cs:17`: usa `LibraryImport` (P/Invoke
  source-generated de .NET 8, requiere `unsafe`), no `DllImport` clásico — comentario ARC-08 en
  el propio código lo documenta.
- `AnimeLocalTracker/AnimeLocalTracker.csproj:53`: `<None Include="animetracker_core.dll"
  Condition="Exists('animetracker_core.dll')">` — el `.dll` de Rust se empaqueta condicionalmente
  junto al ejecutable .NET.
- `.github/workflows/ci.yml:102-105`: el job de CI compila el crate en modo release y copia el
  binario resultante directamente a la carpeta del proyecto WPF (`Copy-Item
  native/animetracker_core/target/release/animetracker_core.dll AnimeLocalTracker/
  animetracker_core.dll -Force`) **antes** de compilar la solución .NET — confirma que el binario
  final que se distribuye a usuarios contiene el core Rust enlazado por FFI.

**Python → C# (proceso independiente, empaquetado con PyInstaller, IPC por proceso hijo):**
- `AGENTS.md:3`: "daemon Python (`AnimeTrackerTools.exe`, PyInstaller)".
- `AnimeLocalTracker/Services/Python/PythonBridgeService.cs`: lanza `AnimeTrackerTools.exe` como
  proceso hijo vía `ProcessStartInfo` (líneas 85, 262), buscándolo en rutas empaquetadas como
  `Tools/AnimeTrackerTools/AnimeTrackerTools.exe` junto al ejecutable principal (líneas 360-369).
- `AGENTS.md:21`: "El daemon Python se mata en `ProcessExit` (`PythonBridgeService`) — no
  eliminar" — confirma ciclo de vida acoplado al proceso principal de la app.
- `AnimeLocalTracker.csproj:48`: `<None Include="Tools\**">` empaqueta el binario PyInstaller
  compilado dentro del directorio de salida de la app.
- `tools/python/pyproject.toml`: dependencias reales de producción (no solo dev/test) incluyen
  `yt-dlp`, `opencv-python-headless`, `rapidfuzz`, `pydantic`, `numpy` — consistentes con tareas
  de parsing de nombres de archivo, extracción de streams y detección de escenas, no con scripts
  de build.

**Consecuencia para el alcance de Fases 1-3:** ambos componentes se auditan con el mismo rigor
que el código C#, tal como indica la regla condicional del brief para el caso "sí están
integrados en el producto". Esto incluye: `cargo audit` sobre el crate Rust, `pip-audit`/`bandit`
sobre el paquete Python, y análisis de interoperabilidad (fallo de uno de los dos componentes
puede dejar la app WPF en estado inconsistente) en la Fase 2.

## 4. Pipeline de CI declarado (`.github/workflows/ci.yml`) — confirmado por lectura directa

Corre en `windows-latest`, gate de 45 min, con concurrencia por rama (un push cancela el run
anterior). Orden real de pasos:
1. Checkout con submódulos.
2. Cache de Cargo registry/target y de NuGet.
3. Setup .NET 8 SDK, Rust stable, Python 3.11.
4. **Tests unitarios Python (pytest)** sobre `tools/python/tests` — bloqueante (comentario
   `ARC-14` en el propio workflow: "deben bloquear el CI, hoy solo existían en local").
5. **SCA bloqueante** (comentario `DEV-01` explícito en el workflow): `cargo audit`, `pip-audit`,
   `dotnet list package --vulnerable` (app y Tests) — los tres corren *antes* de intentar el
   build de Rust, y cualquier vulnerabilidad conocida rompe el pipeline según el propio comentario
   del archivo.
6. Build release del crate Rust + copia del `.dll` al proyecto WPF.
7. `cargo clippy --release -- -D warnings` (0 warnings obligatorio, igual que en C#).
8. `build.ps1 -RunTests -Coverage` (build de doble pasada + tests con cobertura).
9. **Gate de cobertura bloqueante**: falla el job (`exit 1`) si líneas < 45% o ramas < 30%.
   Comentario `OPS-08` deja constancia de la medición real más reciente conocida: 46.9%
   líneas / 34.3% ramas (fecha referenciada en el comentario: 2026-09-01).
10. Upload de resultados de tests y cobertura como artefacto.

Esto será verificado contra el comportamiento real del repo (no solo el YAML declarado) en la
Fase 2, incluyendo si `gh run list`/`gh run view` (si `gh` está disponible localmente) confirma
runs recientes exitosos con estos gates activos.

## 5. Flujos críticos de usuario identificados (para pruebas de Fases 4 y 5)

1. **Onboarding**: descargar instalador Velopack desde GitHub Releases → instalar → vincular
   cuenta AniList (OAuth) → seleccionar carpeta de biblioteca local.
2. **Importar/escanear biblioteca local**: el daemon Python (`AnimeTrackerTools.exe`) parsea
   nombres de archivo (anitopy/rapidfuzz) y hace fingerprinting de episodios.
3. **Reproducir un episodio con auto-tracking**: FlyleafLib (DirectX 11) reproduce el video;
   al cruzar 90% de progreso el registro se guarda en SQLite local y se sincroniza a AniList vía
   GraphQL si hay conexión.
4. **Auto-skip de OP/ED**: integración con la API de AniSkip.
5. **Descarga de episodios**: gestor de descargas integrado con métricas de velocidad en tiempo
   real (mencionado en README y en `Services/DownloadService.cs`).

## 6. Objetivos de negocio/producto declarados (README)

- **Local-first / privacidad**: "tus archivos... jamás salen de tu máquina"; only AniList sync
  data leaves the device, con consentimiento explícito del usuario (OAuth).
- **Calidad de reproducción premium**: HEVC/AV1/VP9, HDR/OLED, 60-144 FPS, subtítulos de alta
  fidelidad.
- **Cero fricción operativa**: actualizaciones silenciosas vía Velopack, sin UAC, "30 segundos"
  para empezar a usar la app.
- **Calidad de ingeniería como argumento de venta**: 327 tests, SCA bloqueante, 0 warnings —
  el README usa métricas de calidad interna como parte del discurso de marketing (relevante para
  Fase 6: estos claims deben coincidir con el estado real del pipeline, verificado en la Fase 2).

## 7. Alcance ajustado de fases siguientes

- **Fase 1 (Seguridad)**: incluye Rust y Python con el mismo rigor que C# (integrados en
  producción). Prioriza `dotnet list package --vulnerable`, `cargo audit`, `pip-audit`/`bandit`
  sobre OWASP Dependency-Check completo (este último requiere descarga de base NVD de varios GB;
  se intentará y, si excede el límite de reintentos/tiempo, se documentará como deuda técnica de
  entorno, no del proyecto).
- **Fase 2 (Arquitectura)**: incluye diagrama de interoperabilidad C#↔Rust (FFI in-process) y
  C#↔Python (subproceso independiente vía `ProcessStartInfo`), y evalúa el riesgo real de que un
  fallo del proceso Python (que no comparte memoria con la app WPF) o del binding FFI de Rust
  (que sí comparte proceso — un panic de Rust sin `catch_unwind` en el límite FFI puede tumbar la
  app completa) afecte la estabilidad de la app principal.
- **Fase 3 (Rendimiento)**: incluye `cargo bench` si existe target de benchmark en el crate, y
  perfil del daemon Python bajo carga típica (parsing/fingerprinting de una biblioteca simulada).
