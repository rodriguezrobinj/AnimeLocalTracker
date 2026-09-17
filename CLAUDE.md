# AnimeLocalTracker

App de escritorio WPF (.NET 8) para trackear anime local: biblioteca, reproductor (Flyleaf), sincronización con AniList, descargas, y un núcleo políglota (Rust FFI + daemon Python) para parsing/fingerprinting/scraping.

Reglas de arquitectura y convenciones del proyecto (compartidas con otras herramientas de agente vía `.agents/rules/`, importadas aquí para que Claude Code también las use):

@.agents/rules/wpf-mvvm.md
@.agents/rules/polyglot-ffi.md
@.agents/rules/persistence.md
@.agents/rules/ui-wpf-vistas.md
@.agents/rules/comunicacion-explicaciones.md

## Build y tests

Antes de dar por terminado cualquier cambio en C#/XAML, usa el skill `repo-build-test` (`.claude/skills/repo-build-test/SKILL.md`) — documenta un flake conocido del SDK de WPF que puede reportar "OK" con errores ocultos, y cómo evitar falsos positivos/negativos al compilar y correr la suite de pruebas.

## UI

- Al crear o modificar una pestaña/vista (o portar una lista a virtualizada), usa el skill `wpf-add-view` (`.claude/skills/wpf-add-view/SKILL.md`) — la cadena completa mensaje→NavigationService→DI→DataTemplate→botón, patrón de `ListBox` virtualizado, y las convenciones de accesibilidad/color/localización ya establecidas.
- Al verificar visualmente un cambio de UI o de comportamiento de ventana (compilar, lanzar, capturar pantalla, simular clicks), usa el skill `wpf-visual-verification` (`.claude/skills/wpf-visual-verification/SKILL.md`) — evita perder tiempo con las trampas ya conocidas del entorno (foco/z-order poco fiable, animaciones de `WindowState`, controles que se ocultan por inactividad).

## Estructura políglota

- `AnimeLocalTracker/` — app WPF principal (C#).
- `AnimeLocalTracker.Tests/` — xUnit + FluentAssertions + Moq.
- `native/animetracker_core/` — núcleo Rust (parsing, fingerprint perceptual, spritesheets), expuesto vía FFI (`LibraryImport`).
- `tools/python/` — daemon Python (`AnimeTrackerTools.exe` empaquetado) para scraping/resolvers/detección de escenas, controlado por `PythonBridgeService`.
