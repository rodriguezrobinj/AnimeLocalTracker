import os
import subprocess
import sys
import re
from typing import Dict, Any

class ReleasePackager:
    @staticmethod
    def create_release(version: str, release_notes: str = "") -> Dict[str, Any]:
        """
        Automatiza la creación de un release de AnimeLocalTracker:
        1. Compila el ejecutable nativo de herramientas Python con PyInstaller.
        2. Compila AnimeLocalTracker en modo Release para win-x64.
        3. Empaqueta el instalador con Velopack (vpk).
        """
        script_dir = os.path.dirname(os.path.abspath(__file__))
        project_root = os.path.dirname(os.path.dirname(script_dir))
        csharp_proj = os.path.join(project_root, "AnimeLocalTracker", "AnimeLocalTracker.csproj")
        publish_dir = os.path.join(project_root, "publish")
        releases_dir = os.path.join(project_root, "Releases")
        
        os.makedirs(publish_dir, exist_ok=True)
        os.makedirs(releases_dir, exist_ok=True)
        
        print(f"[Release] Iniciando empaquetado de versión v{version}...")
        
        # 1. Compilar binario de Python
        build_py = os.path.join(script_dir, "..", "build_binary.py")
        if os.path.exists(build_py):
            print("[Release] Compilando AnimeTrackerTools.exe...")
            subprocess.run([sys.executable, build_py], cwd=os.path.dirname(build_py))
            
        # 2. Publicar proyecto C# .NET 8
        print("[Release] Publicando AnimeLocalTracker (.NET 8 Release)...")
        dotnet_cmd = [
            "dotnet", "publish", csharp_proj,
            "-c", "Release",
            "-r", "win-x64",
            "--self-contained", "false",
            "-o", publish_dir
        ]
        res = subprocess.run(dotnet_cmd)
        if res.returncode != 0:
            return {"success": False, "error": "Fallo dotnet publish"}

        # 3. Velopack pack (si vpk está disponible)
        vpk_cmd = [
            "vpk", "pack",
            "-u", "AnimeLocalTracker",
            "-v", version,
            "-p", publish_dir,
            "-e", "AnimeLocalTracker.exe",
            "-o", releases_dir
        ]
        try:
            vpk_res = subprocess.run(vpk_cmd)
            if vpk_res.returncode == 0:
                print(f"[Release] ¡Paquete Velopack generado exitosamente en: {releases_dir}!")
        except Exception:
            print("[Release] 'vpk' no encontrado, omitiendo empaquetado de instalador Velopack.")

        return {
            "success": True,
            "version": version,
            "publish_dir": publish_dir,
            "releases_dir": releases_dir
        }

if __name__ == "__main__":
    ver = sys.argv[1] if len(sys.argv) > 1 else "1.0.0"
    res = ReleasePackager.create_release(ver)
    print(res)
