import os
import subprocess
import shutil
import sys

# PERF-05 (auditoría 2026-09-25): módulos que NO se empaquetan en el .exe.
#
# Playwright (con su driver Node, ~105 MB en disco) solo lo usa BrowserStreamExtractor.extract_byse, y esa función
# devuelve error nada más empezar cuando corre dentro del .exe (sys.frozen): el desafío anti-bot de Byse falla el
# 100 % de las veces en el binario de PyInstaller (ver el docstring de BrowserStreamExtractor). Es decir, empaquetarlo
# solo añadía ~105 MB a cada release sin que jamás se ejecutara. Sigue funcionando en desarrollo (`python cli.py`),
# donde sí se importa desde el entorno de Python. greenlet y pyee solo son dependencias de Playwright.
# Si algún día se resuelve lo de Byse en el binario, esa función podría descargar Playwright bajo demanda en lugar de
# volver a incluirlo aquí.
#
# cv2 (OpenCV, ~110 MB) ya no es dependencia: la huella perceptual y las miniaturas usan el ffmpeg de la app. Se excluye
# por si algún entorno de desarrollo aún lo tiene instalado, para que PyInstaller no lo arrastre de vuelta.
MODULOS_EXCLUIDOS = ("playwright", "greenlet", "pyee", "cv2")


def argumentos_pyinstaller(python_exe, cli_path, tools_output_dir, build_dir, script_dir):
    """Línea de comandos de PyInstaller para el daemon (separada de la ejecución para poder probarla)."""
    argumentos = [
        python_exe, "-m", "PyInstaller",
        "--noconfirm",
        "--clean",
        "--onedir",
        "--name", "AnimeTrackerTools",
        "--distpath", tools_output_dir,
        "--workpath", build_dir,
        "--specpath", script_dir,
    ]
    for modulo in MODULOS_EXCLUIDOS:
        argumentos += ["--exclude-module", modulo]
    argumentos.append(cli_path)
    return argumentos


def build_standalone_binary():
    """
    Compila tools/python/cli.py en un ejecutable autónomo (Zero-Setup), empaquetado
    como carpeta (--onedir) en AnimeLocalTracker/Tools/AnimeTrackerTools/AnimeTrackerTools.exe.
    Se usa --onedir en vez de --onefile para evitar la extracción a un directorio
    temporal en cada arranque, causa raíz de fallos de handshake observados en producción
    con el onefile de 73MB (ver PythonBridgeService.ResolveExecutable).
    """
    script_dir = os.path.dirname(os.path.abspath(__file__))
    project_root = os.path.dirname(os.path.dirname(script_dir))
    tools_output_dir = os.path.join(project_root, "AnimeLocalTracker", "Tools")
    os.makedirs(tools_output_dir, exist_ok=True)

    cli_path = os.path.join(script_dir, "cli.py")
    dist_dir = os.path.join(script_dir, "dist")
    build_dir = os.path.join(script_dir, "build")

    print(f"[Build] Compilando AnimeTrackerTools.exe desde: {cli_path}")

    cmd = argumentos_pyinstaller(sys.executable, cli_path, tools_output_dir, build_dir, script_dir)

    res = subprocess.run(cmd, cwd=script_dir)
    if res.returncode == 0:
        exe_path = os.path.join(tools_output_dir, "AnimeTrackerTools", "AnimeTrackerTools.exe")
        print(f"[Build] ¡Compilado exitosamente! Binario generado en: {exe_path}")

        # Limpieza de temporales
        if os.path.exists(build_dir):
            shutil.rmtree(build_dir, ignore_errors=True)
        spec_file = os.path.join(script_dir, "AnimeTrackerTools.spec")
        if os.path.exists(spec_file):
            os.remove(spec_file)
        return True
    else:
        print("[Build] Error compilando con PyInstaller.")
        return False

if __name__ == "__main__":
    build_standalone_binary()
