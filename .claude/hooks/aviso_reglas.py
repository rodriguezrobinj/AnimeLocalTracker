"""Hook PostToolUse (Edit y Write): avisa de reglas del repo incumplidas en el texto que se acaba de escribir.

No bloquea: devuelve contexto para que se corrija en la misma tarea. Solo mira lo escrito (new_string / content), no el
archivo entero, para no repetir avisos de código antiguo. Solo archivos .cs/.xaml del proyecto principal (no Tests ni Benchmarks).
Prueba: python .claude/hooks/test_hooks.py
"""
import json
import re
import sys

I = re.IGNORECASE

# (extensión, patrón, exención de ruta o None, aviso)
REGLAS = [
    (".cs", r"\[\s*DllImport\b", None, "[DllImport] está prohibido: usa [LibraryImport] (polyglot-ffi.md)."),
    (".cs", r"\bDebug\.WriteLine\b", None, "No uses Debug.WriteLine: registra con AppLogger."),
    (".cs", r"\bDateTime\.Now\b", None, "DateTime.Now: lo que se guarda va en UTC (DateTime.UtcNow); para mostrar, convierte con LocalizationService.Cultura (persistence.md)."),
    (".cs", r"\bCreateTable(?:Async)?\s*<", r"/Services/DatabaseService\.cs$", "CreateTable suelto: el esquema evoluciona solo con la lista Migraciones de DatabaseService (skill migracion-db, persistence.md #6)."),
    (".cs", r"\[\s*Indexed\b", None, "[Indexed] solo aplica a bases NUEVAS y duplica índices (DB-01): crea el índice en una migración con CREATE INDEX IF NOT EXISTS (skill migracion-db)."),
    (".cs", r"new\s+ProcessStartInfo\b", r"/Core/", "ProcessStartInfo suelto: usa Core/ProcesoExterno.EjecutarAsync (o Core/Shell.Abrir para abrir carpetas/URL)."),
    (".xaml", r"Source\s*=\s*\{x:Static\s+loc:LocalizationService\.Instance\}", None, "Binding largo de localización: escribe {loc:T Clave} (solo en un <Binding> dentro de MultiBinding va la forma larga, con Mode=OneWay)."),
    (".xaml", r"\b(?:Foreground|Background|Fill|Stroke|BorderBrush)\s*=\s*\"#[0-9A-Fa-f]{3,8}\"", r"/App\.xaml$|/Themes/", "Color literal: usa un pincel de la paleta de App.xaml (Brush.*/AppText.*) en vez de inventar uno (ui-wpf-vistas.md)."),
    (".xaml", r"RepeatBehavior\s*=\s*\"Forever\"", None, "RepeatBehavior=Forever: solo es válido si el elemento se oculta (Collapsed) al terminar la carga; si no, acota las repeticiones (wpf-mvvm.md, punto 5)."),
]


def _async_void_suelto(texto):
    avisos = []
    for m in re.finditer(r"\basync\s+void\s+(\w+)\s*\(([^)]*)\)", texto):
        parametros = m.group(2)
        if "EventArgs" not in parametros and "sender" not in parametros:
            avisos.append(f"async void {m.group(1)}: solo se admite en manejadores de eventos de la vista (con try-catch); en el resto devuelve Task (wpf-mvvm.md).")
    return avisos


def revisar(ruta, texto):
    """Lista de avisos para el texto escrito en 'ruta' (vacía si no aplica)."""
    ruta = ruta.replace("\\", "/")
    # La carpeta raíz del repo también se llama AnimeLocalTracker: hay que excluir Tests y Benchmarks de forma explícita.
    if not texto or "/AnimeLocalTracker/" not in ruta or re.search(r"/AnimeLocalTracker\.(?:Tests|Benchmarks)/|/obj/|/bin/", ruta):
        return []
    extension = ".cs" if ruta.endswith(".cs") else ".xaml" if ruta.endswith(".xaml") else None
    if extension is None:
        return []
    if extension == ".cs":
        texto = re.sub(r"//[^\n]*", "", texto)  # los comentarios que citan una regla ("sin [Indexed]…") no la incumplen
    avisos = [aviso for ext, patron, exento, aviso in REGLAS
              if ext == extension and not (exento and re.search(exento, ruta)) and re.search(patron, texto)]
    if extension == ".cs":
        avisos += _async_void_suelto(texto)
        if re.search(r"\bFile\.Copy\(", texto) and re.search(r"Database|DatabasePath|BibliotecaDb|biblioteca\.db", ruta + texto, I):
            avisos.append("File.Copy sobre la base de datos: usa VACUUM INTO (snapshot atómico); con WAL una copia directa se corrompe (persistence.md).")
    return avisos


def main():
    try:
        entrada = json.load(sys.stdin)
        datos = entrada.get("tool_input") or {}
        avisos = revisar(datos.get("file_path", ""), datos.get("new_string") or datos.get("content") or "")
    except Exception:  # noqa: BLE001 - un fallo del hook nunca debe estorbar
        return 0
    if avisos:
        print(json.dumps({"hookSpecificOutput": {
            "hookEventName": "PostToolUse",
            "additionalContext": "Reglas del repo — revisa lo que acabas de escribir:\n- " + "\n- ".join(avisos),
        }}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
