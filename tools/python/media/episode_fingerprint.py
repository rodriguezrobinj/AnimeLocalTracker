import cv2
import numpy as np
from typing import Dict, Any, List, Tuple


class EpisodeFingerprint:
    """Perceptual hashing (dHash) de un frame clave de cada episodio.

    Permite:
    - find-duplicates: agrupar archivos que comparten el mismo contenido (hashing cercano)
    - fingerprint: generar la firma de un episodio para comparación posterior
    """

    HASH_SPACES = {
        "video": (0.0, 0.0, 0.0),        # t=30s frame clave (default)
    }

    @staticmethod
    def compute_fingerprint(video_path: str, timestamp: float = 30.0) -> Dict[str, Any]:
        """Extrae un frame en 'timestamp' y calcula su dHash de 8x8 (64 bits)."""
        try:
            cap = cv2.VideoCapture(video_path)
            if not cap.isOpened():
                return {"success": False, "error": "no se pudo abrir el video"}
            fps = cap.get(cv2.CAP_PROP_FPS)
            if fps <= 0:
                fps = 24.0
            frame_number = int(timestamp * fps)
            cap.set(cv2.CAP_PROP_POS_FRAMES, frame_number)
            ok, frame = cap.read()
            width = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH)) if cap.isOpened() else 0
            cap.release()
            if not ok or frame is None:
                return {"success": False, "error": "no se pudo extraer el frame"}

            # dHash: diferencia entre columnas contiguas, reducido a 8x8
            gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
            small = cv2.resize(gray, (9, 8), interpolation=cv2.INTER_AREA)
            diffs = small[:, 1:] > small[:, :-1]  # 8x8 booleano
            bits = ''.join('1' if b else '0' for row in diffs for b in row)
            hash_val = int(bits, 2)

            return {
                "success": True,
                "hash": f"{hash_val:016x}",
                "timestamp": timestamp,
                "width": width,
            }
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
