import json
import subprocess
from typing import Dict, Any, List


class EpisodeMetadata:
    """Extrae metadatos técnicos de un video con ffprobe (duración, codecs, resolucion, bits)."""

    @staticmethod
    def inspect_episode(video_path: str) -> Dict[str, Any]:
        try:
            ffprobe = subprocess.run(
                ["ffprobe", "-v", "error", "-print_format", "json", "-show_format", "-show_streams", video_path],
                capture_output=True, text=True, timeout=30
            )
            if ffprobe.returncode != 0:
                return {"success": False, "error": ffprobe.stderr.strip() or "ffprobe fallo"}

            data = json.loads(ffprobe.stdout)

            video_stream = next((s for s in data.get("streams", []) if s.get("codec_type") == "video"), None)
            audio_stream = next((s for s in data.get("streams", []) if s.get("codec_type") == "audio"), None)
            sub_streams = [s for s in data.get("streams", []) if s.get("codec_type") == "subtitle"]

            subs = []
            for s in sub_streams:
                subs.append({
                    "language": s.get("tags", {}).get("language", ""),
                    "title": s.get("tags", {}).get("title", ""),
                    "codec": s.get("codec_name", ""),
                })

            duration = float(data.get("format", {}).get("duration", 0) or 0)
            bitrate = data.get("format", {}).get("bit_rate")

            return {
                "success": True,
                "duracion_segundos": duration,
                "ancho": int(video_stream.get("width", 0)) if video_stream else 0,
                "alto": int(video_stream.get("height", 0)) if video_stream else 0,
                "codec_video": video_stream.get("codec_name", "") if video_stream else "",
                "codigo_video": video_stream.get("codec_long_name", "") if video_stream else "",
                "fps": video_stream.get("r_frame_rate", "") if video_stream else "",
                "bitrate": int(bitrate) if bitrate and bitrate.isdigit() else 0,
                "codec_audio": audio_stream.get("codec_name", "") if audio_stream else "",
                "canales_audio": audio_stream.get("channels", 0) if audio_stream else 0,
                "subtitulos": subs,
                # Propiedades útiles para la UI
                "pix_fmt": video_stream.get("pix_fmt", "") if video_stream else "",
                "es_10bit": (video_stream.get("pix_fmt", "").find("10") >= 0) if video_stream else False,
            }
        except subprocess.TimeoutExpired:
            return {"success": False, "error": "timeout"}
        except Exception as ex:
            return {"success": False, "error": str(ex)}

    @staticmethod
    def inspect_batch(video_paths: List[str]) -> Dict[str, Any]:
        results = {}
        for p in video_paths:
            results[p] = EpisodeMetadata.inspect_episode(p)
        return {"success": True, "results": results}


class Thumbnail:
    """Genera una miniatura del video en un timestamp dado (para la UI)."""

    @staticmethod
    def generate_thumbnail(video_path: str, output_path: str, timestamp: float = 30.0, width: int = 320) -> Dict[str, Any]:
        try:
            import subprocess as sp
            result = sp.run(
                ["ffmpeg", "-y", "-ss", str(timestamp), "-i", video_path,
                 "-frames:v", "1", "-vf", f"scale={width}:-2", "-q:v", "3", output_path],
                capture_output=True, text=True, timeout=60
            )
            if result.returncode == 0:
                return {"success": True, "output": output_path, "timestamp": timestamp}
            return {"success": False, "error": result.stderr.strip() or "ffmpeg fallo"}
        except subprocess.TimeoutExpired:
            return {"success": False, "error": "timeout"}
        except Exception as ex:
            return {"success": False, "error": str(ex)}
