"""Comprobación de los hooks del repo. Ejecutar: python .claude/hooks/test_hooks.py"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from aviso_reglas import revisar  # noqa: E402
from guardia_comandos import evaluar  # noqa: E402

REAL = r"C:\Users\HP\AppData\Local\AnimeLocalTrackerData"
PROYECTO = r"C:\Users\HP\RiderProjects\AnimeLocalTracker\AnimeLocalTracker"


def decision(comando, herramienta="PowerShell"):
    r = evaluar(herramienta, comando)
    return r[0] if r else None


# (comando, esperado: 'deny' | 'ask' | None)
CASOS = [
    # --- seguro: tiene que pasar ---
    ("dotnet build AnimeLocalTracker/AnimeLocalTracker.csproj -c Debug", None),
    ("git push origin main", None),
    ("git push", None),
    ("git push -u origin main", None),
    ("git push origin main 2>&1 | tail -3", None),
    ("git push origin main 2>$null", None),
    ("git add -A && git commit -m 'fix: algo' && git push origin main", None),
    ("Stop-Process -Id $p.Id -Force", None),
    ("taskkill /PID 1234 /F", None),
    ("Get-Process dotnet,MSBuild,VBCSCompiler,testhost -ErrorAction SilentlyContinue | Stop-Process -Force", None),
    (f"Get-ChildItem '{REAL}' | Select-Object Name", None),
    (f"Copy-Item '{REAL}\\settings.json' $env:TEMP\\perfil\\settings.json", None),
    (r"Remove-Item -Recurse -Force C:\Users\HP\AppData\Local\Temp\perfil\AppData\Local\AnimeLocalTrackerData", None),
    (r"$perfil = Join-Path $env:TEMP 'p'; Remove-Item -Recurse -Force $perfil\AppData\Local\AnimeLocalTrackerData", None),
    ("git commit -m 'feat: sin firma'", None),
    ("git commit -m 'chore: el hook bloquea --borrar-datos'", None),  # texto de un mensaje, no un lanzamiento
    # --- el texto de un mensaje de commit no son órdenes ---
    ("git commit -q -F - <<'EOF'\nfix: el hook bloquea git push con fuerza, Stop-Process AnimeLocalTracker y --borrar-datos\n"
     "y Remove-Item en C:\\Users\\HP\\AppData\\Local\\AnimeLocalTrackerData\nEOF\ngit push origin main", None),
    ("git commit -m 'git push --force y Remove-Item AnimeLocalTrackerData'", None),
    # ...pero lo que se encadena después del commit sí se vigila
    ("git commit -m 'x' && git push --force origin main", "deny"),
    ("git commit -q -F - <<'EOF'\nmensaje\nEOF\ngit push origin otra-rama", "deny"),
    ("git commit -q -F - <<'EOF'\nmensaje\nEOF\nGet-Process AnimeLocalTracker | Stop-Process", "deny"),
    ("git commit -q -F - <<'EOF'\nx\n\nCo-Authored-By: Claude <noreply@anthropic.com>\nEOF", "deny"),
    # --- --borrar-datos ---
    (r"& .\AnimeLocalTracker.exe --borrar-datos 1234", "deny"),
    # --- cerrar la app por nombre ---
    ("Get-Process AnimeLocalTracker | Stop-Process -Force", "deny"),
    ("Get-Process dotnet,AnimeLocalTracker,testhost | Stop-Process -Force", "deny"),
    ("taskkill /F /IM AnimeLocalTracker.exe", "deny"),
    ("Stop-Process -Name AnimeLocalTracker", "deny"),
    ("Get-Process | Stop-Process", "deny"),
    ("pkill -f AnimeLocalTracker", "deny"),
    # --- git push ---
    ("git push --force origin main", "deny"),
    ("git push -f", "deny"),
    ("git push origin +main", "deny"),
    ("git push origin feature-x", "deny"),
    ("git push origin --delete main", "deny"),
    ("git push --all", "deny"),
    ("git push origin v1.0.6", "ask"),
    ("git push --tags", "ask"),
    ("git push origin HEAD", "ask"),
    # --- firma de Claude en commits ---
    ("git commit -m 'x\n\nCo-Authored-By: Claude Sonnet <noreply@anthropic.com>'", "deny"),
    ("git commit -m 'x' -m 'Generated with Claude Code'", "deny"),
    # --- datos reales ---
    (f"Remove-Item -Recurse '{REAL}\\Thumbnails'", "deny"),
    (f"Get-ChildItem '{REAL}\\Thumbnails' | Remove-Item", "deny"),
    (r"Remove-Item $env:LOCALAPPDATA\AnimeLocalTrackerData\anilist_token.txt", "deny"),
    ("rm -rf ~/AppData/Local/AnimeLocalTrackerData/Logs", "deny"),
    (f"mv /c/Users/HP/AppData/Local/AnimeLocalTrackerData/biblioteca.db /tmp/x", "deny"),
    (f"$d = '{REAL}'\nRemove-Item $d\\Covers -Recurse", "deny"),
    (f"'x' | Set-Content '{REAL}\\settings.json'", "deny"),
    (f"echo hola > {REAL}\\notas.txt", "deny"),
]

for comando, esperado in CASOS:
    obtenido = decision(comando)
    assert obtenido == esperado, f"{comando!r}: esperado {esperado}, obtenido {obtenido}"

assert decision("git push --force", herramienta="Read") is None, "solo Bash y PowerShell"
assert decision("") is None

# --- aviso de reglas ---
def avisos(archivo, texto, ruta=PROYECTO):
    return revisar(f"{ruta}\\{archivo}", texto)


assert avisos("Services\\X.cs", '[DllImport("a.dll")] static extern int F();')
assert avisos("Services\\X.cs", "async void Cargar() { }")
assert not avisos("Views\\X.xaml.cs", "private async void Boton_Click(object sender, RoutedEventArgs e) { }")
assert avisos("Services\\X.cs", "Debug.WriteLine(1);")
assert avisos("Services\\X.cs", "var a = DateTime.Now;")
assert not avisos("Services\\X.cs", "var a = DateTime.UtcNow;")
assert avisos("Services\\X.cs", "var p = new ProcessStartInfo(\"a\");")
assert not avisos("Core\\ProcesoExterno.cs", "var p = new ProcessStartInfo(\"a\");")
assert avisos("Services\\DatabaseService.cs", "File.Copy(DatabasePath, x);")
assert not avisos("Services\\Otro.cs", "File.Copy(a, b);")
assert avisos("Views\\X.xaml", '<TextBlock Text="{Binding [K], Source={x:Static loc:LocalizationService.Instance}}"/>')
assert avisos("Views\\X.xaml", '<Border Background="#FF0000"/>')
assert not avisos("App.xaml", '<SolidColorBrush x:Key="Brush.A" Color="#FF0000"/>')
assert not avisos("Themes\\WatchProgressBar.xaml", '<Rectangle Fill="#FF0000"/>')
assert avisos("Views\\X.xaml", '<DoubleAnimation RepeatBehavior="Forever"/>')
# fuera de alcance
assert not avisos("Services\\X.cs", "Debug.WriteLine(1);", ruta=r"C:\Users\HP\RiderProjects\AnimeLocalTracker\AnimeLocalTracker.Tests")
assert not revisar(PROYECTO + "\\README.md", "Debug.WriteLine")

print(f"OK: {len(CASOS)} casos de guardia y avisos de reglas")
