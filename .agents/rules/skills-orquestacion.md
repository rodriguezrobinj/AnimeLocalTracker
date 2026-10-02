# Regla: Orquestación de skills y plugins

Hay muchas skills instaladas con territorios que se solapan. Esta regla fija **quién manda en cada cosa** para que no se pisen. Ante la duda, gana la fuente más arriba en la jerarquía.

## 1. Jerarquía de precedencia (de mayor a menor)

1. La instrucción explícita del usuario en la conversación.
2. `CLAUDE.md` y las reglas de `.agents/rules/`.
3. Skills del repo: `repo-build-test`, `wpf-add-view`, `wpf-visual-verification`.
4. `superpowers` (proceso: cómo trabajar).
5. Plugins de dominio (qué saber sobre un tema): `dotnet-test`, `dotnet-msbuild`, `microsoft-docs`, `agent-skills` (solo los nichos del punto 3), `figma`.
6. Plugins de estilo (cómo sonar o cómo se ve): `ponytail`, `caveman`, `ui-ux-pro-max`, `taste-skill`, skills de Emil Kowalski.

Una skill de nivel inferior nunca puede relajar una regla de nivel superior. Si una skill ordena algo que este documento prohíbe (ver punto 5), se ignora esa instrucción concreta y se sigue el resto de la skill.

## 2. Un dueño por tarea

| Tarea | Dueño | Notas |
|---|---|---|
| Compilar y correr tests | `repo-build-test` | Obligatorio antes de dar por terminado cualquier cambio C#/XAML. Las skills de `dotnet-test`/`dotnet-msbuild` ayudan a diagnosticar, pero no sustituyen su procedimiento. |
| Nueva pestaña/vista o lista virtualizada | `wpf-add-view` | Más `ui-wpf-vistas.md`. |
| Verificar UI en la app real | `wpf-visual-verification` | |
| Diseñar una función nueva con decisiones abiertas | `superpowers:brainstorming` | Solo si hay decisiones de diseño reales (ver punto 4). |
| Plan de varios pasos | `superpowers:writing-plans` | |
| Bug o comportamiento inesperado | `superpowers:systematic-debugging` | Causa raíz antes que parche. |
| Decir "listo" | `superpowers:verification-before-completion` + `repo-build-test` | Evidencia (build y tests reales), no suposición. |
| Revisión de código | `/code-review` o `superpowers:requesting-code-review` | `agent-skills:code-review-and-quality` solo como lista de comprobación extra. |
| Tests (escribir) | `superpowers:test-driven-development` | Con xUnit + FluentAssertions + Moq. `dotnet-test:code-testing-agent` solo para cobertura masiva. |
| Seguridad, rendimiento, observabilidad, ADR/documentación, migraciones y retirada de código | `agent-skills` (`security-and-hardening`, `performance-optimization`, `observability-and-instrumentation`, `documentation-and-adrs`, `deprecation-and-migration`) | Nichos que `superpowers` no cubre. |
| Verificar una API de .NET/Microsoft | `microsoft-docs:microsoft-code-reference` | Antes de inventar firmas. |
| Simplificar código | `ponytail` (siempre activo) y `/simplify` | |
| Diseño visual de XAML | `ui-wpf-vistas.md` primero | Ver punto 6. |

Para las demás fases del ciclo, **`agent-skills` NO se usa donde `superpowers` ya tiene skill**: TDD, depuración, planificación, flujo de ramas y revisión se hacen solo con `superpowers`. Si ambas se activan a la vez, se ejecuta la de `superpowers` y se descarta la otra.

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

- **Git**: no hacer commit ni push sin que el usuario lo pida. Cuando se pida subir, se sube **solo a `main`**, sin ramas ni PRs. Por tanto, no usar `using-git-worktrees` ni `finishing-a-development-branch`, ni la creación automática de ramas de `agent-skills:git-workflow-and-versioning`. El commit de documentos de diseño que piden `brainstorming`/`writing-plans` se omite.
- **Subagentes**: no lanzar subagentes salvo que el usuario lo pida. No usar `subagent-driven-development` ni `dispatching-parallel-agents` por iniciativa propia.
- **Colores y literales de UI**: ninguna skill de estilo puede introducir colores nuevos. Rige `ui-wpf-vistas.md` (paleta `Brush.*`/`AppText.*`).
- **Instancia del usuario**: las pruebas en vivo solo cierran el proceso que lanzó el propio agente (por PID). Jamás `Stop-Process` a ciegas.
- **Datos reales**: los tests y scripts nunca escriben ni borran bajo `AppDataPaths` (token de AniList, BD, ajustes).

## 6. Skills de diseño: cómo usarlas con WPF

`ui-ux-pro-max`, `taste-skill` y las skills de Emil Kowalski están pensadas para web (React/CSS/Tailwind). En este repo:

- Se usan **solo como criterio** (jerarquía, espaciado, duración y curva de animaciones, estados vacíos), nunca para copiar código CSS/JSX.
- `ui-ux-pro-max` tiene una guía WPF (`stacks/wpf.csv`): es la única parte aplicable directamente, y siempre filtrada por `ui-wpf-vistas.md`.
- Si dos skills de diseño se contradicen, se sigue el orden: `ui-wpf-vistas.md` → `emil-design-eng` (movimiento y detalle) → `ui-ux-pro-max` (estructura y accesibilidad) → `taste-skill` (solo inspiración estética).
- Si el usuario comparte un enlace de Figma, el diseño de Figma es la fuente de verdad de la maqueta, traducido a XAML con la paleta del proyecto.
- Las skills `animate-expo`, `mobile-native`, `write-swift`, `ask-sonner` y `apple-design` (web/móvil) no aplican a este proyecto y no deben activarse.

## 7. Tono y brevedad

- **`ponytail`** (modo `full`): rige el **código** (lo mínimo que funciona, reutilizar lo que ya existe en el repo). Nunca justifica saltarse una regla de arquitectura de `.agents/rules/`.
- **`caveman`**: queda **apagado** en este repo (`.caveman/config.json`) porque choca con `comunicacion-explicaciones.md` (explicaciones con ejemplos y en español). Solo se enciende si el usuario lo pide con `/caveman`.
- Todo mensaje al usuario, incluidos los avisos intermedios, va en español.
