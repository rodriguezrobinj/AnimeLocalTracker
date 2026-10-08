# AnimeLocalTracker

App de escritorio WPF (.NET 8) para trackear anime local: biblioteca, reproductor (Flyleaf), sincronización con AniList, descargas, y un núcleo políglota (Rust FFI + daemon Python) para parsing/fingerprinting/scraping.

Reglas de arquitectura y convenciones del proyecto (compartidas con otras herramientas de agente vía `.agents/rules/`, importadas aquí para que Claude Code también las use):

@.agents/rules/wpf-mvvm.md
@.agents/rules/polyglot-ffi.md
@.agents/rules/persistence.md
@.agents/rules/ui-wpf-vistas.md
@.agents/rules/comunicacion-explicaciones.md
@.agents/rules/skills-orquestacion.md

## Cómo trabajar (resumen)

Hay varios plugins de skills activos (superpowers, ponytail, ui-ux-pro-max, Emil Kowalski, microsoft-docs, dotnet-*, y `caveman`, apagado por defecto: solo se enciende con `/caveman`). `agent-skills`, `figma` y `taste-skill` están desactivados. **La jerarquía y el dueño de cada tarea están en `skills-orquestacion.md`; léelo antes de activar skills de proceso.** Lo esencial:

- Precedencia: instrucción del usuario > `CLAUDE.md`/reglas > skills del repo > superpowers > plugins de dominio > plugins de estilo.
- Proceso pesado (brainstorming, plan escrito) solo para funciones nuevas o cambios multi-módulo; los cambios pequeños se hacen directo.
- Sin commit ni push sin orden; al subir, solo a `main`. Sin ramas, worktrees ni subagentes por iniciativa propia.
- Responder siempre en español.

## Build y tests

Antes de dar por terminado cualquier cambio en C#/XAML, usa el skill `repo-build-test` (`.claude/skills/repo-build-test/SKILL.md`) — documenta un flake conocido del SDK de WPF que puede reportar "OK" con errores ocultos, y cómo evitar falsos positivos/negativos al compilar y correr la suite de pruebas.

La verificación es proporcional al cambio: si no se tocó C#/XAML (investigar, responder preguntas, documentos) no se compila ni se prueba; mientras se trabaja, compilar la app y correr las pruebas de lo tocado; la suite completa, una sola vez al cerrar la tarea o antes de subir. Sin doble pasada: se compila una vez y solo se reintenta si falla.

## Guardarraíles (hooks y ajustes del proyecto)

`.claude/settings.json` (compartido) activa dos hooks en Python (`.claude/hooks/`) que convierten en bloqueos reglas que antes solo estaban escritas:
- `guardia_comandos.py` (antes de ejecutar Bash/PowerShell): bloquea `--borrar-datos`, cerrar `AnimeLocalTracker` por nombre (solo por PID), `git push` con fuerza o a otra rama que no sea `main` (tags y `HEAD` piden confirmación), commits con firma de Claude y borrar/mover/sobrescribir en `%LocalAppData%\AnimeLocalTrackerData`.
- `aviso_reglas.py` (después de editar `.cs`/`.xaml` del proyecto principal): avisa de `DllImport`, `async void` suelto, `DateTime.Now`, `Debug.WriteLine`, `ProcessStartInfo` fuera de `Core/`, binding largo de localización, colores literales y `RepeatBehavior="Forever"`. No bloquea.
- Además: `attribution` vacío (sin línea de coautor en commits/PR) y `deny` para leer el token de AniList o editar la carpeta de datos real.
- Si un bloqueo estorba de verdad, se ajusta el hook (con su prueba: `python .claude/hooks/test_hooks.py`), no se rodea.

## UI

- Al crear o modificar una pestaña/vista (o portar una lista a virtualizada), usa el skill `wpf-add-view` (`.claude/skills/wpf-add-view/SKILL.md`) — la cadena completa mensaje→NavigationService→DI→DataTemplate→botón, patrón de `ListBox` virtualizado, y las convenciones de accesibilidad/color/localización ya establecidas.
- Al verificar visualmente un cambio de UI o de comportamiento de ventana (compilar, lanzar, capturar pantalla, simular clicks), usa el skill `wpf-visual-verification` (`.claude/skills/wpf-visual-verification/SKILL.md`) — evita perder tiempo con las trampas ya conocidas del entorno (foco/z-order poco fiable, animaciones de `WindowState`, controles que se ocultan por inactividad).

## Estructura políglota

- `AnimeLocalTracker/` — app WPF principal (C#).
- `AnimeLocalTracker.Tests/` — xUnit + FluentAssertions + Moq.
- `native/animetracker_core/` — núcleo Rust (parsing, fingerprint perceptual, spritesheets), expuesto vía FFI (`LibraryImport`).
- `tools/python/` — daemon Python (`AnimeTrackerTools.exe` empaquetado) para scraping/resolvers/detección de escenas, controlado por `PythonBridgeService`.
