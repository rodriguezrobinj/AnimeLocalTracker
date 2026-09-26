import subprocess
from typing import Dict, Any, List

from media.ffmpeg_guard import es_ruta_media_segura, MAX_ALLOC

# dHash: se reduce el fotograma a 9x8 en gris y se compara cada columna con la siguiente (8x8 = 64 bits).
ANCHO_HASH = 9
ALTO_HASH = 8
TIMEOUT_SEGUNDOS = 60


class EpisodeFingerprint:
    """Perceptual hashing (dHash) de un frame clave de cada episodio.

    Permite:
    - find-duplicates: agrupar archivos que comparten el mismo contenido (hashing cercano)
    - fingerprint: generar la firma de un episodio para comparación posterior

    El fotograma lo extrae ffmpeg (ya va incluido con la app); antes se usaba OpenCV, que solo servía para
    esto y añadía ~110 MB al instalador.
    """

    @staticmethod
    def _comando_ffmpeg(video_path: str, timestamp: float) -> List[str]:
        return [
            "ffmpeg", "-hide_banner", "-nostats", "-loglevel", "error", "-nostdin",
            "-max_alloc", MAX_ALLOC,
            "-ss", f"{max(timestamp, 0.0):.3f}", "-i", video_path,
            "-frames:v", "1", "-an", "-sn",
            "-vf", f"scale={ANCHO_HASH}:{ALTO_HASH}:flags=area,format=gray",
            "-f", "rawvideo", "pipe:1",
        ]

    @staticmethod
    def calcular_dhash(pixeles: bytes) -> str:
        """dHash de 64 bits (16 hex) a partir de 9x8 píxeles en gris (fila a fila)."""
        bits = 0
        for fila in range(ALTO_HASH):
            base = fila * ANCHO_HASH
            for col in range(ANCHO_HASH - 1):
                bits = (bits << 1) | (1 if pixeles[base + col + 1] > pixeles[base + col] else 0)
        return f"{bits:016x}"

    @staticmethod
    def compute_fingerprint(video_path: str, timestamp: float = 30.0) -> Dict[str, Any]:
        """Extrae un frame en 'timestamp' y calcula su dHash de 8x8 (64 bits)."""
        try:
            # Hardening: solo archivos locales de video (nunca URLs ni esquemas de ffmpeg)
            if not es_ruta_media_segura(video_path):
                return {"success": False, "error": "ruta de video no permitida"}

            proc = subprocess.run(
                EpisodeFingerprint._comando_ffmpeg(video_path, timestamp),
                capture_output=True, timeout=TIMEOUT_SEGUNDOS,
            )
            if proc.returncode != 0:
                detalle = proc.stderr.decode("utf-8", "replace").strip().splitlines()
                return {"success": False, "error": detalle[-1] if detalle else "ffmpeg fallo"}

            pixeles = proc.stdout
            if len(pixeles) < ANCHO_HASH * ALTO_HASH:
                return {"success": False, "error": "no se pudo extraer el frame"}

            return {
                "success": True,
                "hash": EpisodeFingerprint.calcular_dhash(pixeles),
                "timestamp": timestamp,
            }
        except subprocess.TimeoutExpired:
            return {"success": False, "error": "timeout"}
        except Exception as ex:
            return {"success": False, "error": str(ex)}

    @staticmethod
    def hamming_distance(h1: str, h2: str) -> int:
        """Distancia de Hamming entre dos hash hexadecimales de 16 chars."""
        try:
            v1 = int(h1, 16)
            v2 = int(h2, 16)
            return bin(v1 ^ v2).count("1")
        except Exception:
            return 64

    @classmethod
    def find_duplicates(cls, video_paths: List[str], max_distance: int = 8, timestamp: float = 30.0) -> Dict[str, Any]:
        """Agrupa los archivos cuyo frame clave es perceptualmente equivalente (duplicados)."""
        fingerprints = []
        for path in video_paths:
            r = cls.compute_fingerprint(path, timestamp)
            if r.get("success"):
                fingerprints.append({"path": path, "hash": r["hash"]})

        groups: List[Group] = []
        for fp in fingerprints:
            placed = False
            for g in groups:
                # Comparar con el representante del grupo
                if cls.hamming_distance(g["hash"], fp["hash"]) <= max_distance:
                    g["items"].append(fp["path"])
                    placed = True
                    break
            if not placed:
                groups.append({"hash": fp["hash"], "items": [fp["path"]]})

        duplicates = [g for g in groups if len(g["items"]) > 1]

        return {
            "success": True,
            "total_analizados": len(fingerprints),
            "duplicados": duplicates,
            "unicos": len(groups) - len(duplicates),
        }


Group = Dict[str, Any]
