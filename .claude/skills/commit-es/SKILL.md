---
name: commit-es
description: Hacer commit y subir cambios en AnimeLocalTracker con las convenciones del proyecto (conventional commits en español, con tildes, sin firma de Claude, solo a main). Usar SOLO cuando el usuario pida hacer commit, "súbelo" o hacer push; sin esa orden nunca se hace commit ni push.
---

# Commit y subida — AnimeLocalTracker

**Sin orden explícita no hay commit ni push** (`CLAUDE.md`). La orden de commit no incluye push, y la de subir un trabajo no se extiende al siguiente que hagas después. Se sube **solo a `main`**: sin ramas, PR, worktrees, `--amend`, rebase ni `--force` (el hook `guardia_comandos.py` bloquea lo que sea de riesgo). Nunca `--no-verify`.

## Estado actual

Archivos pendientes:
!`git status --short`

Asuntos recientes con tildes, como referencia de estilo:
!`git log -4 -E --grep='[áéíóúñ]' --format='%h %s'`

## 1. Qué entra

- Solo lo que pertenece a la tarea: `git add` por ruta, no `git add -A` si hay cambios ajenos. Si hay archivos modificados que no reconoces, **pregunta** antes de incluirlos o dejarlos fuera.
- **Cada commit debe compilar** (`AGENTS.md`): si el código depende de un archivo nuevo, ese archivo va en el mismo commit; mira los `??` de arriba.
- Nada de datos reales ni generados: `.db`, tokens, logs, `Releases/`, `bin/`, `obj/` (ya ignorados; si aparece alguno en `git status`, para y avisa). Los documentos de diseño o planes de `docs/` no se commitean por iniciativa propia (`skills-orquestacion.md` #4).

## 2. Verificación previa (proporcional, `repo-build-test`)

- Si se tocó C#/XAML: compilación con 0 advertencias y la suite completa **una vez** con el código final. Si ya la corriste con exactamente este código en la tarea, no la repitas; si cambiaste algo después, sí.
- Si solo se tocaron documentos, hooks, skills o configuración: no se compila. Si se tocaron los hooks, `python .claude/hooks/test_hooks.py`; si se tocaron skills, `claude plugin validate .claude/skills`.

## 3. El mensaje

Formato `tipo(área): asunto`, **en español y con tildes** (el historial las usa; `git commit -F -` las conserva bien).

- **Tipos usados:** `feat`, `fix`, `refactor`, `perf`, `docs`, `chore`, `build`, `ci`, `test`.
- **Área** en minúsculas y por función, como en el historial: `reproductor`, `descargas`, `ficha`, `db`, `ui`, `detalle`, `auth`, `claude`, `deps`… Sin área solo para cambios transversales.
- **Asunto:** lo que cambia para quien usa la app o el proyecto, no cómo (≈ 85 caracteres; el máximo del historial es ~145). Con el ID del hallazgo cuando exista (`NAV-03`, `SEC-04`, `PERF-03`).
- **Cuerpo** (la mayoría de los commits lo llevan; omítelo solo si el asunto lo dice todo): primero el problema observable y cómo se reprodujo, luego qué cambia (viñetas si son varios puntos) y al final cómo se verificó (pruebas nuevas, ejecución con datos reales). Un ejemplo bueno: `cd44ac7`.
- **Prohibido:** líneas de coautor, "Generated with Claude Code" y `Claude-Session`. El hook bloquea un `git commit` cuyo texto contenga esas frases, así que al describirlo en un mensaje redáctalo con otras palabras ("firma de Claude").
- Escribe el mensaje con un heredoc entre comillas; en rutas usa una sola barra invertida:

```bash
git add ruta/uno ruta/dos
git commit -q -F - <<'EOF'
fix(ficha): el contador de episodios ya no se queda en 0 al volver de la reproducción

Cuerpo: problema, qué cambia, cómo se verificó.
EOF
git log -1 --stat --format='%h %s'
```

## 4. Subir (solo si se pidió)

```bash
git push origin main
git status -sb          # debe decir: main...origin/main sin "ahead"
gh run list --limit 2   # el CI de este push: en cola/en curso/éxito
```

Si el CI falla por una prueba que no tocaste, mira el registro (`gh run view <id> --log-failed`) antes de culpar o arreglar a ciegas; hay intermitentes conocidas (`registros-y-entorno` en la memoria).

## 5. Qué decir al usuario

Hash y asunto, qué entró y qué se dejó fuera a propósito, si se subió y el estado del CI (o que no se ha visto aún). Si algo de la verificación no se pudo hacer, dilo.
