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
        
    elif command == "resolve-stream":
        url = payload.get("url", "")
        headers = payload.get("headers")
        return StreamExtractor.extract_stream_info(url, headers)
        
    elif command == "detect-scenes":
        video_path = payload.get("video_path", "")
        max_sec = int(payload.get("max_seconds", 300))
        return SceneDetector.detect_skip_candidates(video_path, max_sec)
        
    elif command == "mock-db":
        db_path = payload.get("db_path", "mock_anime.db")
        count = int(payload.get("count", 500))
        return DbMockGenerator.populate_sqlite(db_path, count)
        
    elif command == "ping":
        return {"success": True, "version": "1.0.0", "engine": "AnimeTrackerTools Python"}
        
    else:
        return {"success": False, "error": f"Comando desconocido: '{command}'"}

def main():
    parser = argparse.ArgumentParser(description="AnimeTrackerTools CLI Dispatcher")
    parser.add_argument("--command", type=str, help="Nombre del comando a ejecutar")
    parser.add_argument("--json", type=str, help="Payload JSON como argumento (opcional)")
    
    args = parser.parse_args()
    
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
