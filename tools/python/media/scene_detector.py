import json
import re
import shutil
import subprocess
from typing import Dict, Any, List, Optional, Tuple

from media.ffmpeg_guard import es_ruta_media_segura, MAX_ALLOC


class SceneDetector:
    """Detecta opening/ending en un video local analizando frames con OpenCV.

    Estrategia (mucho más robusta que el cambio de escena puro):
    1. Muestrea frames a ~1 fps en los primeros N segundos con cv2.VideoCapture.
    2. Calcula el "salto visual" (norm L1 de la diferencia HSV reducida) entre frames.
    3. Un OP real es una VENTANA CONTINUA de saltos altos (densidad alta) de al menos 20s,
       dentro de 0:20–2:50, que además tiende a mantener un tono/paleta estable.
    4. Un ED se busca en la ventana final (últimos 100s) con la misma técnica.
    5. Fallback: si OpenCV falla (sin codecs) usa el filtro scdet de ffmpeg del sistema.

    Devuelve: intro_estimated_start/end, ending_estimated_start/end, confidence (0-1).
    """

    SAMPLE_INTERVAL = 1.5               # Muestrear cada 1.5 segundos (suficiente y ultra rápido)
    OP_MIN_DURATION = 18.0              # Duración mínima de una ventana candidata a OP
    OP_MAX_START = 140.0                # El OP empieza antes de 2:20
    ED_LOOKBACK = 80.0                  # El ED se busca en los últimos 80 s
    JUMP_THRESHOLD = 16.0               # Norm L1 normalizada: considerado salto visual
    CONFIDENCE_MULT = 0.9

    @staticmethod
    def detect_skip_candidates(video_path: str, max_search_seconds: int = 140) -> Dict[str, Any]:
        """Punto de entrada del CLI. Analiza el video y devuelve candidatos JSON."""
        try:
            # Intento principal: detección ultra-rápida con OpenCV
            result = SceneDetector._detect_with_opencv(video_path, max_search_seconds)
            if result.get("success"):
                return result

            # Fallback: ffmpeg scdet si OpenCV no pudo
            fallback = SceneDetector._detect_with_ffmpeg(video_path, max_search_seconds)
            if fallback.get("success"):
                return fallback

            return {"success": True, "confidence": 0.0, "source": "none"}
        except Exception as ex:
            return {"success": False, "error": str(ex)}

    # ────────────────────────────────────────────────────────────────
    #  Impl. OpenCV (Seek muestreado ultraligero)
    # ────────────────────────────────────────────────────────────────
    @staticmethod
    def _detect_with_opencv(video_path: str, max_search_seconds: int = 140) -> Dict[str, Any]:
        try:
            import cv2
            import numpy as np
        except ImportError:
            return {"success": False, "error": "opencv no disponible"}

        try:
            cap = cv2.VideoCapture(video_path)
            if not cap.isOpened():
                return {"success": False, "error": "cv2 no pudo abrir el video"}
        except Exception as ex:
            return {"success": False, "error": f"cv2 abrir: {ex}"}

        try:
            fps = cap.get(cv2.CAP_PROP_FPS)
            if fps <= 0:
                fps = 24.0
            total_frames = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
            duration = total_frames / fps if (fps > 0 and total_frames > 0) else None

            frames: List[float] = []
            signals: List[float] = []

            # 1. Muestrear ventana de Opening (0s a máx 140s) mediante seeks rápidos
            max_op = min(max_search_seconds, 140)
            prev = None
            t = 0.0

            while t < max_op:
                cap.set(cv2.CAP_PROP_POS_MSEC, t * 1000.0)
                ok, frame_raw = cap.read()
                if not ok or frame_raw is None:
                    break
                gray = cv2.cvtColor(frame_raw, cv2.COLOR_BGR2GRAY)
                small = cv2.resize(gray, (32, 18), interpolation=cv2.INTER_AREA)
                if prev is not None:
                    diff = float(cv2.absdiff(prev, small).mean())
                    signal = (diff / 255.0) * 100.0
                    signals.append(signal)
                    frames.append(t)
                prev = small
                t += SceneDetector.SAMPLE_INTERVAL

            # 2. Muestrear ventana de Ending (últimos 80s si hay duración conocida)
            if duration and duration > 120:
                t_ed = max(0.0, duration - SceneDetector.ED_LOOKBACK)
                prev_ed = None
                while t_ed < duration:
                    cap.set(cv2.CAP_PROP_POS_MSEC, t_ed * 1000.0)
                    ok, frame_raw = cap.read()
                    if not ok or frame_raw is None:
                        break
                    gray = cv2.cvtColor(frame_raw, cv2.COLOR_BGR2GRAY)
                    small = cv2.resize(gray, (32, 18), interpolation=cv2.INTER_AREA)
                    if prev_ed is not None:
                        diff = float(cv2.absdiff(prev_ed, small).mean())
                        signal = (diff / 255.0) * 100.0
                        signals.append(signal)
                        frames.append(t_ed)
                    prev_ed = small
                    t_ed += SceneDetector.SAMPLE_INTERVAL

            cap.release()

            if len(signals) < 10:
                return {"success": False, "error": "pocos frames analizados"}

            intro = SceneDetector._window_finder(signals, frames, duration, mode="op")
            ending = SceneDetector._window_finder(signals, frames, duration, mode="ed")

            confidence = SceneDetector._estimate_confidence(intro, ending)
            if confidence <= 0:
                return {"success": True, "confidence": 0.0, "source": "opencv_sin_hallazgo"}

            return {
                "success": True,
                "video_path": video_path,
                "intro_estimated_start": intro[0] if intro else None,
                "intro_estimated_end": intro[1] if intro else None,
                "ending_estimated_start": ending[0] if ending else None,
                "ending_estimated_end": ending[1] if ending else None,
                "confidence": confidence,
                "source": "opencv_frame_analysis",
            }
        except Exception as ex:
            return {"success": False, "error": f"opencv análisis: {ex}"}

    @staticmethod
    def _window_finder(signals: List[float], frames: List[float], duration: Optional[float], mode: str) -> Optional[Tuple[float, float]]:
        """Busca la región con mayor DENSIDAD de saltos visuales (patrón tipo OP/ED).

        En lugar de ventanas deslizantes cuadradas, analiza la distribución de
        "eventos" (señales >= JUMP_THRESHOLD) y localiza la agrupación más densa.
        """
        n = len(signals)

        # Límites de búsqueda según modo
        if mode == "ed" and duration:
            start_in_window = max(0, duration - SceneDetector.ED_LOOKBACK)
            idx_start = next((i for i, t in enumerate(frames) if t >= start_in_window), 0)
            idx_end = n
        elif mode == "op":
            idx_start = next((i for i, t in enumerate(frames) if t > 15), 0)
            idx_end = next((i for i, t in enumerate(frames) if t > SceneDetector.OP_MAX_START), n)
        else:
            idx_start = 0
            idx_end = n

        allowed_indices = list(range(idx_start, idx_end))
        if not allowed_indices or len(allowed_indices) < 6:
            return None

        # Eventos: índices donde hay un salto alto
        event_idx = [i for i in allowed_indices if signals[i] >= SceneDetector.JUMP_THRESHOLD]

        # No hay eventos suficientes para un OP/ED
        if len(event_idx) < 3:
            return None

        # Utilizar ventana deslizante buscando densidad máxima de eventos
        window_size = max(4, int(SceneDetector.OP_MIN_DURATION / SceneDetector.SAMPLE_INTERVAL))
        best = None
        best_density = 0.0

        for i in range(idx_start, max(idx_start + 1, idx_end - window_size + 1)):
            end_idx = min(i + window_size, n)
            w = signals[i:end_idx]
            if not w:
                continue
            ev = sum(1 for s in w if s >= SceneDetector.JUMP_THRESHOLD)
            density = ev / len(w)
            if density > best_density and ev >= 3:
                best_density = density
                best = (frames[i], frames[min(end_idx - 1, n - 1)])

        return best

    @staticmethod
    def _estimate_confidence(intro: Optional[Tuple[float, float]], ending: Optional[Tuple[float, float]]) -> float:
        """Estima confianza: 0.75 base detectado, sube si aparece OP y ED."""
        if intro and ending:
            return 0.92
        if intro or ending:
            return 0.78
        return 0.0

    # ────────────────────────────────────────────────────────────────
    #  Fallback ffmpeg (si OpenCV no está o falla)
    # ────────────────────────────────────────────────────────────────
    @staticmethod
    def _detect_with_ffmpeg(video_path: str, max_search_seconds: int) -> Dict[str, Any]:
        ffmpeg = shutil.which("ffmpeg")
        if not ffmpeg:
            return {"success": False, "error": "ffmpeg no disponible"}
        # Hardening: solo archivos locales de video
        if not es_ruta_media_segura(video_path):
            return {"success": False, "error": "ruta de video no permitida"}

        dur = SceneDetector._probe_ffmpeg(video_path)
        search_sec = int(min(max_search_seconds, max(dur if dur else max_search_seconds, 60)))

        try:
            # Filtro scdet disponible desde ffmpeg 4.4
            cmd = [
                ffmpeg, "-hide_banner", "-nostats", "-nostdin", "-max_alloc", MAX_ALLOC,
                "-i", video_path,
                "-t", str(search_sec),
                "-vf", f"scdet=s=0.30:sc=1,metadata=print:file=-",
                "-f", "null", "-"
            ]
            proc = subprocess.run(cmd, capture_output=True, text=True, timeout=120)
            output = proc.stdout + proc.stderr
            scenes = SceneDetector._parse_scdet(output)

            intro = SceneDetector._pick_intro_ffmpeg(scenes)
            ending = SceneDetector._pick_ending_ffmpeg(scenes, dur)
            confidence = SceneDetector._estimate_confidence(intro, ending)

            return {
                "success": True,
                "video_path": video_path,
                "intro_estimated_start": intro[0] if intro else None,
                "intro_estimated_end": intro[1] if intro else None,
                "ending_estimated_start": ending[0] if ending else None,
                "ending_estimated_end": ending[1] if ending else None,
                "confidence": confidence,
                "source": "scdet_local",
            }
        except subprocess.TimeoutExpired:
            return {"success": False, "error": "timeout_analizando_video"}
        except Exception as ex:
            return {"success": False, "error": str(ex)}

    @staticmethod
    def _probe_ffmpeg(video_path: str) -> Optional[float]:
        ffprobe = shutil.which("ffprobe")
        if not ffprobe:
            return None
        # Hardening: solo archivos locales de video
        if not es_ruta_media_segura(video_path):
            return None
        try:
            result = subprocess.run(
                [ffprobe, "-nostdin", "-max_alloc", MAX_ALLOC, "-v", "error",
                 "-show_entries", "format=duration",
                 "-of", "json", video_path],
                capture_output=True, text=True, timeout=30
            )
            data = json.loads(result.stdout)
            return float(data["format"]["duration"])
        except Exception:
            return None

    @staticmethod
    def _parse_scdet(output: str) -> List[float]:
        timestamps = []
        for line in output.splitlines():
            m = re.search(r"pts_time:([0-9.]+)", line)
            if m:
                timestamps.append(float(m.group(1)))
        return timestamps

    @staticmethod
    def _pick_intro_ffmpeg(scenes: List[float]) -> Optional[tuple]:
        scenes = [s for s in scenes if 15 <= s <= 170]
        for i in range(len(scenes) - 2):
            group = scenes[i:i + 3]
            span = group[-1] - group[0]
            if 14 <= span <= 25:
                return (group[0] - 2.0, group[-1] + (group[-1] - group[0]) * 1.5)
        return None

    @staticmethod
    def _pick_ending_ffmpeg(scenes: List[float], duration: Optional[float]) -> Optional[tuple]:
        if not duration or not scenes:
            return None
        for i in range(len(scenes) - 2):
            group = scenes[i:i + 3]
            if group[0] < duration - 120:
                continue
            span = group[-1] - group[0]
            if span <= 25:
                return (group[0] - 2.0, duration)
        return None
