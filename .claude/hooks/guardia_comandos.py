"""Hook PreToolUse (Bash y PowerShell): convierte en bloqueos las reglas del repo que hoy solo están escritas.

Bloquea ("deny") o pide confirmación ("ask") antes de ejecutar:
  - lanzar la app con --borrar-datos (borra los datos reales del usuario);
  - cerrar AnimeLocalTracker por nombre (puede ser la app real con un episodio en marcha): solo por PID;
  - git push con fuerza, a otra rama o borrando refs (solo main); tags y HEAD piden confirmación;
  - git commit con línea de coautor/firma de Claude;
  - borrar, mover o sobrescribir algo dentro de %LocalAppData%\\AnimeLocalTrackerData REAL.

Es una heurística sobre el texto del comando: no entiende scripts arbitrarios. Si falla al leer la entrada, deja pasar.
Prueba: python .claude/hooks/test_hooks.py
"""
import json
import re
import sys

I = re.IGNORECASE

# Prefijos que apuntan a la carpeta de datos REAL del usuario (un perfil aislado vive bajo %TEMP%, que no coincide).
_BASE_REAL = (
    r"(?:users/[^/\s\"']+/appdata/local|~/appdata/local|\$\{?home\}?/appdata/local|\$env:userprofile/appdata/local"
    r"|%userprofile%/appdata/local|%localappdata%|\$env:localappdata|\$\{env:localappdata\}|\$\{?localappdata\}?)"
)
DATOS_REALES = re.compile(_BASE_REAL + r"/animelocaltrackerdata", I)
VERBOS_ESCRITURA = re.compile(
    r"(?<![\w-])(?:rm|rmdir|del|erase|rd|mv|move|move-item|remove-item|ri|clear-content|set-content|add-content"
    r"|out-file|rename-item|truncate|shred)(?![\w-])",
    I,
)
ASIGNACION = re.compile(r"(?:\$(\w+)|(?<![\w$])(\w+))\s*=\s*[^\n;]*" + _BASE_REAL + r"/animelocaltrackerdata", I)

VERBOS_KILL = re.compile(r"\b(?:stop-process|taskkill|pkill|killall|spps)\b|\bkill\s+-9", I)
MARCA_PID = re.compile(r"(?:-id\b|/pid\b|\$pid\b|\$\w+\.id\b|\$\{?\w*pid\w*\}?)", I)
KILL_GENERICO = re.compile(r"get-process\s*\||-name\s+['\"]?\*", I)

# Cuerpo de un heredoc (<<'EOF' ... EOF): es texto (mensaje de commit, script de otro lenguaje), no órdenes de la shell.
HEREDOC = re.compile(r"<<-?\s*(['\"]?)(\w+)\1[^\n]*\n.*?\n[ \t]*\2[ \t]*$", re.S | re.M)
ENTRE_COMILLAS = re.compile(r"'[^']*'|\"[^\"]*\"")

FIRMA_CLAUDE = re.compile(
    r"co-authored-by:\s*claude|noreply@anthropic\.com|generated with \[?claude code|claude-session", I
)


def _negar(razon):
    return "deny", razon


def _preguntar(razon):
    return "ask", razon


def _segmentos(comando, tuberias=True):
    separador = r"&&|\|\||[;\n|]" if tuberias else r"&&|\|\||[;\n]"
    return [s for s in re.split(separador, comando) if s.strip()]


def _git_push(segmento):
    m = re.search(r"\bgit\s+(?:-\S+\s+)*push\b(.*)", segmento, I | re.S)
    if not m:
        return None
    tokens = [t for t in m.group(1).split() if not re.fullmatch(r"\d*[<>]+&?\S*", t)]  # fuera redirecciones (2>&1, > log)
    flags = [t.lower() for t in tokens if t.startswith("-")]
    posicionales = [t.strip("'\"") for t in tokens if not t.startswith("-")]
    if any(f in ("-f", "--force", "--force-with-lease", "--force-if-includes") or f.startswith("--force") for f in flags) or any(
        p.startswith("+") for p in posicionales
    ):
        return _negar("git push con fuerza está prohibido: reescribiría el historial de main. Si de verdad hace falta, que lo ejecute el usuario.")
    if any(f in ("--delete", "-d", "--mirror", "--all", "--prune") for f in flags):
        return _negar("git push --delete/--mirror/--all/--prune no está permitido: el repo solo se sube a main.")
    if "--tags" in flags or "--follow-tags" in flags:
        return _preguntar("Subir tags dispara el pipeline de release (tag v*): confirma que el usuario lo pidió.")
    refspecs = posicionales[1:]  # el primero es el remoto
    for ref in refspecs:
        destino = ref.split(":")[-1]
        if destino in ("main", "refs/heads/main") and ref.split(":")[0] in ("main", "HEAD", "refs/heads/main"):
            continue
        if re.fullmatch(r"(?:refs/tags/)?v?\d[\w.\-]*", destino):
            return _preguntar(f"'{ref}' parece un tag de release: confirma que el usuario pidió cortar release.")
        if ref == "HEAD":
            return _preguntar("git push de HEAD: puede no ser main. Usa 'git push origin main' o confirma.")
        return _negar(f"git push a '{ref}' no está permitido: el repo solo se sube a main (sin ramas ni PR).")
    return None


def evaluar(herramienta, comando):
    """Devuelve (decision, razon) con decision 'deny' o 'ask', o None si el comando puede ejecutarse."""
    if not comando or herramienta not in ("Bash", "PowerShell"):
        return None
    completo = comando                      # la firma de Claude se busca en todo el texto, mensaje incluido
    comando = HEREDOC.sub(" ", comando)     # el resto de reglas miran solo las órdenes, no el texto de los heredocs
    norm = comando.replace("\\", "/")

    es_commit = re.search(r"\bgit\s+(?:-\S+\s+)*commit\b", comando, I)
    # En un commit, '--borrar-datos' es texto del mensaje (p. ej. el que describe este mismo hook), no un lanzamiento de la app.
    if not es_commit and re.search(r"--borrar-datos", comando, I):
        return _negar("--borrar-datos borra TODOS los datos reales del usuario (biblioteca, token, ajustes). No se lanza nunca para probar.")

    if VERBOS_KILL.search(comando) and not MARCA_PID.search(comando):
        if re.search(r"animelocal", comando, I) or KILL_GENERICO.search(comando):
            return _negar(
                "No se cierra AnimeLocalTracker por nombre (ni con comodines): puede ser la app real del usuario con un episodio "
                "en marcha. Cierra solo el proceso que lanzaste tú, por su PID (Stop-Process -Id $p.Id)."
            )

    if es_commit and FIRMA_CLAUDE.search(completo):
        return _negar("Los commits de este repo van sin coautor ni firma de Claude (Co-Authored-By, 'Generated with Claude Code', Claude-Session).")

    para_push = ENTRE_COMILLAS.sub("''", comando) if es_commit else comando  # '-m "...git push..."' es texto, no un push
    for seg in _segmentos(para_push):
        decision = _git_push(seg)
        if decision:
            return decision

    # Escritura destructiva sobre la carpeta de datos real (siguiendo variables que apuntan a ella).
    variables = {a or b for a, b in ASIGNACION.findall(norm)}
    patron_var = re.compile(r"\$\{?(?:%s)\b" % "|".join(map(re.escape, variables)), I) if variables else None
    for seg in _segmentos(norm, tuberias=False):  # sin partir por '|': 'Get-ChildItem real | Remove-Item' es una sola orden
        toca_real = DATOS_REALES.search(seg) or (patron_var and patron_var.search(seg))
        if toca_real and VERBOS_ESCRITURA.search(seg):
            return _negar(
                "No se borra, mueve ni sobrescribe nada dentro de %LocalAppData%\\AnimeLocalTrackerData (datos reales del usuario). "
                "Para probar usa un perfil aislado con rutas absolutas bajo %TEMP%; para copiar desde la carpeta real usa Copy-Item/cp."
            )
    if re.search(r">>?\s*[\"']?[^\s|;&]*" + _BASE_REAL + r"/animelocaltrackerdata", norm, I):
        return _negar("No se redirige salida a un archivo dentro de la carpeta de datos real del usuario (AnimeLocalTrackerData).")
    return None


def main():
    try:
        entrada = json.load(sys.stdin)
        decision = evaluar(entrada.get("tool_name", ""), (entrada.get("tool_input") or {}).get("command", ""))
    except Exception:  # noqa: BLE001 - un fallo del hook nunca debe bloquear el trabajo
        return 0
    if decision:
        print(json.dumps({"hookSpecificOutput": {
            "hookEventName": "PreToolUse",
            "permissionDecision": decision[0],
            "permissionDecisionReason": decision[1],
        }}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
