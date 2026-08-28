import os
import subprocess
import shutil
import sys

def build_standalone_binary():
    """
    Compila tools/python/cli.py en un ejecutable binario único autónomo (Zero-Setup)
    ubicado en AnimeLocalTracker/Tools/AnimeTrackerTools.exe
    """
    script_dir = os.path.dirname(os.path.abspath(__file__))
    project_root = os.path.dirname(os.path.dirname(script_dir))
    tools_output_dir = os.path.join(project_root, "AnimeLocalTracker", "Tools")
    os.makedirs(tools_output_dir, exist_ok=True)
    
    cli_path = os.path.join(script_dir, "cli.py")
    dist_dir = os.path.join(script_dir, "dist")
    build_dir = os.path.join(script_dir, "build")
    
    print(f"[Build] Compilando AnimeTrackerTools.exe desde: {cli_path}")
    
    cmd = [
        sys.executable, "-m", "PyInstaller",
        "--noconfirm",
        "--clean",
        "--onefile",
        "--name", "AnimeTrackerTools",
        "--distpath", tools_output_dir,
        "--workpath", build_dir,
        "--specpath", script_dir,
        cli_path
    ]
    
    res = subprocess.run(cmd, cwd=script_dir)
    if res.returncode == 0:
        exe_path = os.path.join(tools_output_dir, "AnimeTrackerTools.exe")
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
