import sys
import json
import argparse
from typing import Any, Dict

# Forzar codificación UTF-8 en streams estándar de Windows
if hasattr(sys.stdin, 'reconfigure'):
    sys.stdin.reconfigure(encoding='utf-8')
if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8')

from parsers.anime_parser import AnimeFileParser
from resolvers.stream_extractor import StreamExtractor
from media.scene_detector import SceneDetector
from media.episode_fingerprint import EpisodeFingerprint
from media.episode_metadata import EpisodeMetadata, Thumbnail
from automation.db_mock_generator import DbMockGenerator


def process_command(command: str, payload: Dict[str, Any]) -> Dict[str, Any]:
    """
    Despacha el comando recibido con sus parámetros en formato JSON.
    """
    if command == "parse-filename":
        filename = payload.get("filename", "")
        dir_context = payload.get("directory_context")
        return AnimeFileParser.parse_filename(filename, dir_context)

    elif command == "parse-batch":
        filenames = payload.get("filenames", [])
        dir_context = payload.get("directory_context")
        return {"success": True, "results": AnimeFileParser.parse_batch(filenames, dir_context)}

    elif command == "match-title":
        query = payload.get("query", "")
        candidates = payload.get("candidates", [])
        threshold = float(payload.get("threshold", 75.0))
        res = AnimeFileParser.match_title_fuzzy(query, candidates, threshold)
        return {"success": True, "match": res}

    elif command == "match-media":
        # Sistema riguroso de coincidencia: busca el MEJOR match de nombres entre
        # los títulos de la app y los del sitio (título + aka), usando rapidfuzz.
        titles = payload.get("titles", [])
        candidates = payload.get("candidates", [])
        threshold = float(payload.get("threshold", 75.0))
        best = None
        for t in titles:
            m = AnimeFileParser.match_title_fuzzy(t, candidates, threshold)
            if m and (best is None or m["score"] > best["score"]):
                best = m
        return {
            "success": best is not None,
            "score": float(best["score"]) if best else 0.0,
            "matched_title": best["matched_title"] if best else "",
        }

    elif command == "resolve-stream":
        url = payload.get("url", "")
        headers = payload.get("headers")
        return StreamExtractor.extract_stream_info(url, headers)

    elif command == "download-stream":
        url = payload.get("url", "")
        output_path = payload.get("output_path", "")
        headers = payload.get("headers")
        return StreamExtractor.download_stream(url, output_path, headers)

    elif command == "detect-scenes":
        video_path = payload.get("video_path", "")
        max_sec = int(payload.get("max_seconds", 300))
        return SceneDetector.detect_skip_candidates(video_path, max_sec)

    elif command == "inspect-episode":
        video_path = payload.get("video_path", "")
        return EpisodeMetadata.inspect_episode(video_path)

    elif command == "fingerprint":
        video_path = payload.get("video_path", "")
        timestamp = float(payload.get("timestamp", 30.0))
        return EpisodeFingerprint.compute_fingerprint(video_path, timestamp)

    elif command == "find-duplicates":
        paths = payload.get("video_paths", [])
        max_distance = int(payload.get("max_distance", 8))
        return EpisodeFingerprint.find_duplicates(paths, max_distance)

    elif command == "generate-thumbnail":
        video_path = payload.get("video_path", "")
        output_path = payload.get("output_path", "")
        timestamp = float(payload.get("timestamp", 30.0))
        width = int(payload.get("width", 240))
        return Thumbnail.generate_thumbnail(video_path, output_path, timestamp, width)

    elif command == "mock-db":
        db_path = payload.get("db_path", "mock_anime.db")
        count = int(payload.get("count", 500))
        return DbMockGenerator.populate_sqlite(db_path, count)

    elif command == "ping":
        return {"success": True, "version": "1.0.0", "engine": "AnimeTrackerTools Python"}

    else:
        return {"success": False, "error": f"Comando desconocido: '{command}'"}


def run_daemon():
    """Modo daemon: lee comandos JSON (una línea por comando) por stdin y
    responde por stdout con una línea JSON. Persistente para evitar el
    coste de arranque de Python+imports en cada llamada."""
    sys.stdout.reconfigure(encoding='utf-8', newline='\n')
    sys.stdin.reconfigure(encoding='utf-8')
    # Primer mensaje de saludo para confirmar que el daemon está vivo
    print(json.dumps({"success": True, "daemon": "ready", "version": "1.0.0"}), flush=True)
    for line in sys.stdin:
        try:
            payload = json.loads(line.strip().lstrip('\ufeff'))
            command = payload.get("command", "ping")
            data = payload.get("payload", {})
            result = process_command(command, data)
            print(json.dumps(result, ensure_ascii=False), flush=True)
        except json.JSONDecodeError:
            print(json.dumps({"success": False, "error": "JSON inválido en línea de comando"}), flush=True)
        except Exception as ex:
            print(json.dumps({"success": False, "error": f"excepción daemon: {str(ex)}"}), flush=True)


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
