import sys
import json
import argparse
import threading
from typing import Any, Dict, Optional

# Forzar codificación UTF-8 en streams estándar de Windows
if hasattr(sys.stdin, 'reconfigure'):
    sys.stdin.reconfigure(encoding='utf-8')
if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8')

from resolvers.stream_extractor import StreamExtractor
from media.episode_fingerprint import EpisodeFingerprint


def process_command(command: str, payload: Dict[str, Any], cancelada: Optional[threading.Event] = None) -> Dict[str, Any]:
    """
    Despacha el comando recibido con sus parámetros en formato JSON. "cancelada" se activa si la app deja de esperar la
    respuesta: los comandos largos que puedan parar a medias la consultan.
    """
    if command == "resolve-stream":
        url = payload.get("url", "")
        headers = payload.get("headers")
        return StreamExtractor.extract_stream_info(url, headers, payload.get("server"))

    elif command == "download-stream":
        url = payload.get("url", "")
        output_path = payload.get("output_path", "")
        headers = payload.get("headers")
        return StreamExtractor.download_stream(url, output_path, headers)

    elif command == "fingerprint":
        video_path = payload.get("video_path", "")
        timestamp = float(payload.get("timestamp", 30.0))
        return EpisodeFingerprint.compute_fingerprint(video_path, timestamp)

    elif command == "find-duplicates":
        paths = payload.get("video_paths", [])
        max_distance = int(payload.get("max_distance", 8))
        return EpisodeFingerprint.find_duplicates(paths, max_distance, cancelada=cancelada)

    elif command == "run-plugin":
        plugin_path = payload.get("plugin_path", "")
        func_name = payload.get("func_name", "")
        args_dict = payload.get("args", {})
        sha256_aprobado = payload.get("sha256")

        try:
            import hashlib
            import os
            import types

            if not os.path.isfile(plugin_path):
                return {"success": False, "error": f"El archivo de plugin no existe: {plugin_path}"}

            # SEC-01: el archivo se lee UNA vez y se ejecuta exactamente lo leído. La app aprueba un plugin por la huella
            # de su contenido y la manda con la petición: si entre su comprobación y esta lectura el archivo cambió, no
            # se ejecuta (cargándolo por su ruta se volvía a leer del disco, ya sin comprobar).
            with open(plugin_path, "rb") as archivo:
                codigo = archivo.read()
            if sha256_aprobado and hashlib.sha256(codigo).hexdigest().lower() != str(sha256_aprobado).lower():
                return {"success": False, "error": "El plugin cambió desde que se aprobó: no se ejecuta."}

            # La carpeta del plugin NO se añade a sys.path: se quedaba ahí para siempre y cualquier "import" posterior del
            # daemon podía resolverse con un archivo dejado en esa carpeta, sin pasar por la aprobación.
            module_name = os.path.splitext(os.path.basename(plugin_path))[0]
            plugin_module = types.ModuleType(module_name)
            plugin_module.__file__ = plugin_path
            exec(compile(codigo, plugin_path, "exec"), plugin_module.__dict__)

            if not hasattr(plugin_module, func_name):
                return {"success": False, "error": f"La función '{func_name}' no existe en el plugin '{module_name}'."}
                
            func = getattr(plugin_module, func_name)
            result = func(**args_dict)
            return {"success": True, "result": result}
            
        except Exception as e:
            return {"success": False, "error": f"Error ejecutando plugin: {str(e)}"}

    elif command == "ping":
        return {"success": True, "version": "1.0.0", "engine": "AnimeTrackerTools Python"}

    else:
        return {"success": False, "error": f"Comando desconocido: '{command}'"}


def run_daemon():
    """Modo daemon: lee mensajes JSON (una línea cada uno) por stdin y responde por stdout con una línea JSON por
    petición. Persistente para evitar el coste de arranque de Python+imports en cada llamada.

    Cada petición {"id", "command", "payload"} se atiende en su propio hilo, así que una lenta no hace esperar a las
    demás y las respuestas pueden salir en otro orden: la app las reparte por el "id", que vuelve en la respuesta.
    {"cancel": id} avisa de que esa respuesta ya no se espera: no se escribe, y el comando para si sabe hacerlo."""
    sys.stdout.reconfigure(encoding='utf-8', newline='\n')
    sys.stdin.reconfigure(encoding='utf-8')
    escritura = threading.Lock()
    en_curso: Dict[Any, threading.Event] = {}

    def responder(respuesta: Dict[str, Any]) -> None:
        with escritura:
            print(json.dumps(respuesta, ensure_ascii=False), flush=True)

    def atender(mensaje: Dict[str, Any], cancelada: threading.Event) -> None:
        try:
            result = process_command(mensaje.get("command", "ping"), mensaje.get("payload", {}), cancelada)
        except Exception as ex:
            result = {"success": False, "error": f"excepción daemon: {str(ex)}"}
        en_curso.pop(mensaje.get("id"), None)
        if not cancelada.is_set():
            responder({**result, "id": mensaje.get("id")} if isinstance(result, dict) else result)

    # Primer mensaje de saludo para confirmar que el daemon está vivo
    # INT-02: protocolVersion detecta desajustes entre la app C# y el daemon Python (2 = peticiones a la vez y cancelación)
    responder({"success": True, "daemon": "ready", "version": "1.0.0", "protocolVersion": 2})
    for line in sys.stdin:
        try:
            mensaje = json.loads(line.strip().lstrip('\ufeff'))
        except json.JSONDecodeError:
            responder({"success": False, "error": "JSON inválido en línea de comando"})
            continue
        if "cancel" in mensaje:
            cancelada = en_curso.get(mensaje["cancel"])
            if cancelada is not None:
                cancelada.set()
            continue
        cancelada = en_curso[mensaje.get("id")] = threading.Event()
        # ponytail: un hilo no se puede matar desde fuera; un plugin que no termine sigue vivo hasta que se cierre la app.
        threading.Thread(target=atender, args=(mensaje, cancelada), daemon=True).start()


def main():
    parser = argparse.ArgumentParser(description="AnimeTrackerTools CLI Dispatcher")
    parser.add_argument("--command", type=str, help="Nombre del comando a ejecutar")
    parser.add_argument("--json", type=str, help="Payload JSON como argumento (opcional)")
    parser.add_argument("--daemon", action="store_true", help="Modo daemon persistente (JSON lines por stdin/stdout)")

    args = parser.parse_args()

    if args.daemon:
        run_daemon()
        return

    payload: Dict[str, Any] = {}

    # Si viene por argumento --json
    if args.json:
        try:
            payload = json.loads(args.json.lstrip('\ufeff'))
        except Exception as e:
            print(json.dumps({"success": False, "error": f"JSON inválido en argumentos: {str(e)}"}))
            sys.exit(1)
    elif args.command == "ping":
        payload = {}
    else:
        try:
            stdin_content = sys.stdin.read().strip().lstrip('\ufeff')
            if stdin_content:
                payload = json.loads(stdin_content)
        except Exception as e:
            print(json.dumps({"success": False, "error": f"JSON inválido en stdin: {str(e)}"}))
            sys.exit(1)

    command = args.command or payload.get("command", "ping")
    result = process_command(command, payload)

    # Salida estándar única en formato JSON
    print(json.dumps(result, ensure_ascii=False))


if __name__ == "__main__":
    main()
