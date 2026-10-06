# Regla: Orquestación de skills y plugins

Hay muchas skills instaladas con territorios que se solapan. Esta regla fija **quién manda en cada cosa** para que no se pisen. Ante la duda, gana la fuente más arriba en la jerarquía.

**Plugins desactivados (2026-10-03, en `~/.claude/settings.json`):** `agent-skills`, `figma` y `taste-skill`. No se desinstalaron; si se reactivan alguno, hay que volver a darle un dueño en esta regla. `caveman` se reactivó el mismo día a petición del usuario (dueño en el punto 7).

## 1. Jerarquía de precedencia (de mayor a menor)

1. La instrucción explícita del usuario en la conversación.
2. `CLAUDE.md` y las reglas de `.agents/rules/`.
3. Skills del repo: `repo-build-test`, `wpf-add-view`, `wpf-visual-verification`.
4. `superpowers` (proceso: cómo trabajar).
5. Plugins de dominio (qué saber sobre un tema): `dotnet-test`, `dotnet-msbuild`, `microsoft-docs`.
6. Plugins de estilo (cómo sonar o cómo se ve): `ponytail`, `ui-ux-pro-max`, skill de Emil Kowalski (`emil-design-eng`).

Una skill de nivel inferior nunca puede relajar una regla de nivel superior. Si una skill ordena algo que este documento prohíbe (ver punto 5), se ignora esa instrucción concreta y se sigue el resto de la skill.

## 2. Un dueño por tarea

| Tarea | Dueño | Notas |
|---|---|---|
| Compilar y correr tests | `repo-build-test` | Obligatorio antes de dar por terminado cualquier cambio C#/XAML, en la medida que fija el skill (pruebas afectadas mientras se trabaja; suite completa una vez al cerrar la tarea o antes de subir). Sin cambios de código no aplica. Las skills de `dotnet-test`/`dotnet-msbuild` ayudan a diagnosticar, pero no sustituyen su procedimiento. |
| Nueva pestaña/vista o lista virtualizada | `wpf-add-view` | Más `ui-wpf-vistas.md`. |
| Verificar UI en la app real | `wpf-visual-verification` | |
| Diseñar una función nueva con decisiones abiertas | `superpowers:brainstorming` | Solo si hay decisiones de diseño reales (ver punto 4). |
| Plan de varios pasos | `superpowers:writing-plans` | |
| Bug o comportamiento inesperado | `superpowers:systematic-debugging` | Causa raíz antes que parche. |
| Decir "listo" | `superpowers:verification-before-completion` + `repo-build-test` | Evidencia (build y tests reales), no suposición. |
| Revisión de código | `/code-review` o `superpowers:requesting-code-review` | |
| Tests (escribir) | `superpowers:test-driven-development` | Con xUnit + FluentAssertions + Moq. `dotnet-test:code-testing-agent` solo para cobertura masiva. |
| Seguridad | `/security-review` | Sin skill de plugin dedicada. |
| Rendimiento, observabilidad, ADR/documentación, migraciones y retirada de código | Sin skill dedicada | Se hace directo, siguiendo `docs/investigacion-*.md` y las reglas del repo. Documentos en `docs/` (ver punto 4). |
| Verificar una API de .NET/Microsoft | `microsoft-docs:microsoft-code-reference` | Antes de inventar firmas. |
| Simplificar código | `ponytail` (siempre activo) y `/simplify` | |
| Diseño visual de XAML | `ui-wpf-vistas.md` primero | Ver punto 6. |

## 3. Uso proporcional (cuándo NO activar proceso pesado)

`superpowers` pide invocar skills "ante cualquier duda". Aquí se acota:

- **Cambio pequeño y claro** (una propiedad, un texto, un color de la paleta, un bug con causa evidente): se hace directamente. Sin brainstorming ni plan escrito. Se verifica con `repo-build-test`.
- **Función nueva, pestaña nueva o cambio que toca varios módulos**: `brainstorming` → `writing-plans` → implementación → verificación.
- **Bug no obvio**: `systematic-debugging` completo.
- **Preguntas y explicaciones**: se responden sin skills de proceso.

## 4. Dónde se guardan los documentos

- Diseños, specs y planes van a `docs/` con el estilo de nombre existente (`docs/investigacion-<tema>.md`, `docs/plan-<tema>.md`). **No** se crea `docs/superpowers/`.
- No se commitea un documento por iniciativa propia (ver punto 5).

## 5. Instrucciones de skills que están PROHIBIDAS en este repo

Entran en conflicto con decisiones ya tomadas del propietario del proyecto:

- **Git**: no hacer commit ni push sin que el usuario lo pida. Cuando se pida subir, se sube **solo a `main`**, sin ramas ni PRs. Por tanto, no usar `using-git-worktrees` ni `finishing-a-development-branch`. El commit de documentos de diseño que piden `brainstorming`/`writing-plans` se omite.
- **Subagentes**: no lanzar subagentes salvo que el usuario lo pida. No usar `subagent-driven-development` ni `dispatching-parallel-agents` por iniciativa propia.
- **Colores y literales de UI**: ninguna skill de estilo puede introducir colores nuevos. Rige `ui-wpf-vistas.md` (paleta `Brush.*`/`AppText.*`).
- **Instancia del usuario**: las pruebas en vivo solo cierran el proceso que lanzó el propio agente (por PID). Jamás `Stop-Process` a ciegas.
- **Datos reales**: los tests y scripts nunca escriben ni borran bajo `AppDataPaths` (token de AniList, BD, ajustes).

## 6. Skills de diseño: cómo usarlas con WPF

`ui-ux-pro-max` y `emil-design-eng` están pensadas para web (React/CSS/Tailwind). En este repo:

- Se usan **solo como criterio** (jerarquía, espaciado, duración y curva de animaciones, estados vacíos), nunca para copiar código CSS/JSX.
- `ui-ux-pro-max` tiene una guía WPF (`stacks/wpf.csv`): es la única parte aplicable directamente, y siempre filtrada por `ui-wpf-vistas.md`.
- Si dos skills de diseño se contradicen, se sigue el orden: `ui-wpf-vistas.md` → `emil-design-eng` (movimiento y detalle) → `ui-ux-pro-max` (estructura y accesibilidad).
- Si el usuario comparte un enlace de Figma, no hay herramienta para leerlo (plugin `figma` desactivado): se le pide una captura o exportación de la maqueta, que pasa a ser la fuente de verdad, traducida a XAML con la paleta del proyecto.

## 7. Tono y brevedad

- **`ponytail`** (modo `full`): rige el **código** (lo mínimo que funciona, reutilizar lo que ya existe en el repo). Nunca justifica saltarse una regla de arquitectura de `.agents/rules/`.
- **`caveman`**: plugin de estilo, activo pero **apagado por defecto** en este repo (`.caveman/config.json` → `defaultMode: "off"`); solo se enciende cuando el usuario lo pide (`/caveman` o "activa caveman"). Rige únicamente el **tono de los mensajes**: mientras esté encendido, la orden explícita del usuario prima sobre el punto 1 de `comunicacion-explicaciones.md` (se omiten los ejemplos de uso), pero siguen vigentes el español, la honestidad sobre lo no verificado y que código, commits y documentos se escriben normales. "Modo normal" lo apaga.
- Todo mensaje al usuario, incluidos los avisos intermedios, va en español.
