---
name: delegar-antigravity
description: Delegar una tarea acotada a Antigravity desde Claude Code con su CLI (`agy -p`), de forma segura y sin tocar datos reales ni git. Usar cuando una tarea sea de lectura, revisión, búsqueda o un cambio pequeño y bien delimitado (textos ES/EN, pruebas de un ViewModel, documentación) y convenga repartir trabajo; nunca para migraciones, reproductor, descargas ni cambios multi-módulo.
---

# Delegar en Antigravity (`agy`)

El usuario autorizó (2026-10-15) que Claude Code delegue en Antigravity por iniciativa propia **cuando lo crea necesario**, dentro de los límites de este skill. El CLI ya está instalado (`agy`, v1.1.22, en `%LOCALAPPDATA%\agy\bin`) y con sesión iniciada: no hay nada que instalar.

## Cuándo sí y cuándo no

| Delegar | No delegar (se queda en Claude Code) |
|---|---|
| Revisar un diff ya hecho (segunda opinión), buscar usos de algo, leer logs | Migraciones de esquema, reproductor/libmpv, descargas, núcleo Rust/daemon Python |
| Textos ES/EN de una función ya diseñada | Cualquier cambio que toque varios módulos |
| Pruebas de un ViewModel concreto, documentación en `docs/` | Lanzar la app, tocar `AppDataPaths`, git (commit/push) |

Si dudas, no delegues: el coste de explicar la tarea supera al de hacerla.

## Cómo se lanza

El prompt va **por stdin** y la salida **siempre en `json`** (en modo `text` con la salida redirigida `agy` no cierra el turno y parece colgado). Siempre con tiempo límite:

```powershell
# Solo lectura / revisión: no puede editar
$raw = $prompt | agy --mode plan --print-timeout 6m --output-format json 2>&1 | Out-String
$j = $raw | ConvertFrom-Json; $j.status; $j.response

# Cambio acotado en archivos concretos: puede editar, pero pide permiso para lo demás
$raw = $prompt | agy --mode accept-edits --print-timeout 10m --output-format json 2>&1 | Out-String
```

- **No uses `-p $prompt` con texto multilínea:** PowerShell 5.1 lo parte en varios argumentos y `agy` falla con `unexpected argument`.
- **En modo sin interfaz `agy` no puede pedir permisos:** un comando como `git diff` se deniega solo (`no output produced`). Por eso el diff y el contexto van dentro del prompt (`git diff -- <archivos> | Out-String`) y se le pide solo leer archivos, no ejecutar comandos. Leer y escribir archivos del repo sí está permitido en el espacio de trabajo. Los permisos de comandos de `agy` viven en `~/.gemini/antigravity-cli/settings.json` (`permissions.allow`); Claude Code no puede editarlo, lo decide el usuario. Desde 2026-10-15 está permitido solo `git diff|status|log|show` (`command(regex:git (diff|status|log|show)( .*)?)`, probado): esos los puede ejecutar `agy` por su cuenta; cualquier otro comando se sigue denegando.
- **Protecciones de `agy` ya puestas y probadas (2026-10-15)** en ese mismo archivo, con `deny` (gana sobre `allow`): leer o escribir `C:/Users/HP/AppData/Local/AnimeLocalTrackerData/` y escribir en `.git/`; además `git blame` permitido y `trustedWorkspaces` reducido al repo. En Windows las rutas absolutas se escriben `C:/...` con `/` (con `/Users/...` sin la letra NO coincide) y una regla sobre un directorio cubre todo su contenido. Ojo: por defecto `agy` **sí lee archivos fuera del repo** sin preguntar, así que lo sensible debe estar en `deny`.
- **Acota siempre el trabajo:** sin límites `agy` lee medio repo (la primera revisión gastó 213 000 tokens y 6 min). Nombra como máximo 3 archivos y di «no explores el resto».
- **Modelo y esfuerzo por defecto:** el usuario fijó Gemini 3.8 Flash en High (`"model": "Gemini 3.8 Flash (High)"` en el `settings.json` de `agy`; `/effort` responde `high`). No pasar `--model` ni `--effort` salvo que haga falta; `--effort low` solo para consultas triviales o si hay que ahorrar cuota. Con High una revisión tarda más (3 a 6 min).
- **El servicio de Google a veces devuelve `503 UNAVAILABLE`** al final de una ejecución larga. El JSON puede traer `status: ERROR` y aun así una `response` completa: léela antes de descartarla. Si viene vacía, reintentar una vez con menos alcance y, si vuelve a fallar, hacerlo en Claude Code.
- **Evalúa cada hallazgo contra el código.** En la primera revisión real (2026-10-15) 1 de 5 hallazgos era útil y 2 contradecían reglas del repo. Antigravity propone; Claude Code decide.
- **Nunca** `--dangerously-skip-permissions`: los hooks de `.claude/hooks/` no existen en Antigravity, así que el permiso es la única barrera.
- Para continuar una conversación: `--continue` o `--conversation <id>`. Salida estructurada: `--output-format json`.
- Si se cuelga (sin salida y tiempo agotado), cancelar y hacerlo en Claude Code; no reintentar en bucle.

## Qué lleva todo prompt delegado

1. La tarea concreta y los **archivos exactos** que puede tocar.
2. "Sigue `AGENTS.md` y `.agents/rules/`". Recordar explícitamente: sin commit ni push, no ejecutar `AnimeLocalTracker.exe`, no escribir bajo `%LocalAppData%\AnimeLocalTrackerData`, textos con clave ES+EN.
3. Qué debe devolver (lista de archivos cambiados y por qué).

## Después de delegar (obligatorio)

1. `git status` y `git diff`: ver **todo** lo que cambió, también fuera de los archivos pedidos. Lo no pedido se revierte.
2. Verificar con `repo-build-test` (build, 0 advertencias, pruebas de lo tocado). Lo que Antigravity diga que "funciona" no cuenta como evidencia.
3. Nunca dos agentes editando lo mismo a la vez: mientras `agy` edita, Claude Code no toca esos archivos.
4. Commit y push solo si el usuario lo pide, con `commit-es`.
5. Decir al usuario qué se delegó, a qué modo y qué resultado dio.

## Privacidad

El prompt y los archivos que Antigravity lea salen hacia el servicio de Google. No incluir tokens, rutas con datos personales ni contenido de `%LocalAppData%\AnimeLocalTrackerData`.
