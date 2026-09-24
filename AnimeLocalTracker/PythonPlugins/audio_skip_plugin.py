import os
import subprocess
import json
import numpy as np
from typing import Dict, Any, List

def detect_opening(video_paths: List[str]) -> Dict[str, Any]:
    """
    Busca el Opening comparando el audio de los primeros 4 minutos de 2 episodios.
    Uso de librosa es ideal, pero para no requerir dependencias externas complejas,
    usaremos ffmpeg para extraer PCM y numpy para la correlación cruzada de envolventes.
    """
    if not video_paths or len(video_paths) < 2:
        return {"success": False, "error": "Se requieren al menos 2 episodios para comparar el audio."}
        
    v1 = video_paths[0]
    v2 = video_paths[1]
    
    if not os.path.exists(v1) or not os.path.exists(v2):
        return {"success": False, "error": "Uno o ambos archivos de video no existen."}

    try:
        # Extraer envolventes de audio (10 Hz = 10 muestras por segundo) de los primeros 240 segundos
        env1 = _extract_audio_envelope(v1, duration=240, fps=10)
        env2 = _extract_audio_envelope(v2, duration=240, fps=10)
        
        if len(env1) == 0 or len(env2) == 0:
            return {"success": False, "error": "No se pudo extraer el audio (posible archivo sin pista de audio)."}
            
        # Para encontrar la coincidencia, hacemos una correlación cruzada de las envolventes
        # Buscaremos una ventana de 85 segundos (850 muestras) en env1 y su mejor coincidencia en env2.
        window_size = 85 * 10
        if len(env1) < window_size or len(env2) < window_size:
            return {"success": False, "error": "Los videos son demasiado cortos para tener un OP normal."}
            
        # Precomputar rolling mean y rolling std de env2
        env2_sum = np.convolve(env2, np.ones(window_size), mode='valid')
        env2_sq_sum = np.convolve(env2**2, np.ones(window_size), mode='valid')
        env2_mean = env2_sum / window_size
        env2_var = (env2_sq_sum / window_size) - env2_mean**2
        env2_var[env2_var < 0] = 0
        env2_std = np.sqrt(env2_var)
        env2_std[env2_std < 1e-5] = 1e-5
        
        best_score = -1
        best_start1 = -1
        
        # Deslizamiento en pasos de 1 segundo (10 muestras)
        for i in range(0, len(env1) - window_size, 10):
            segment = env1[i:i+window_size]
            seg_mean = np.mean(segment)
            seg_std = np.std(segment)
            if seg_std < 1e-5:
                continue
            seg_norm = (segment - seg_mean) / seg_std
            
            # Correlación cruzada con env2 completo
            corr = np.correlate(env2, seg_norm, mode='valid')
            
            # Pearson correlation
            pearson = corr / (window_size * env2_std)
            
            max_idx = np.argmax(pearson)
            max_val = pearson[max_idx]
            
            if max_val > best_score:
                best_score = max_val
                best_start1 = i
                
        # Si el score es muy bajo, no encontramos un Opening común (tal vez distintos OP o no hay OP)
        # Umbral heurístico (Pearson > 0.4 es muy significativo para audio de 85s)
        if best_score < 0.4:
            return {"success": True, "found": False, "confidence": best_score}
            
        # Convertimos los índices de vuelta a segundos
        op_start_sec = best_start1 / 10.0
        op_end_sec = op_start_sec + 85.0
        
        return {
            "success": True,
            "found": True,
            "intro_estimated_start": op_start_sec,
            "intro_estimated_end": op_end_sec,
            "confidence": float(best_score),
            "source": "audio_cross_correlation_plugin"
        }
        
    except Exception as e:
        return {"success": False, "error": str(e)}

def detect_from_reference(episode_path: str, reference_path: str, search_duration: float = 300.0, search_from_end: bool = False) -> Dict[str, Any]:
    """
    Busca dónde aparece un audio de referencia (el OP/ED oficial descargado de AnimeThemes.moe)
    dentro de una ventana del episodio, mediante la misma correlación cruzada normalizada que
    detect_opening() usa para comparar dos episodios entre sí — pero aquí la "ventana" es fija
    y conocida (la referencia completa), así que no depende de tener un segundo episodio local.
    search_from_end=True busca cerca del final del episodio (para ED); False busca desde el inicio
    (para OP). La duración real del episodio se calcula aquí mismo con ffprobe: no depende de que
    el reproductor ya conozca la duración (al llamar esto recién abierto el video, aún no la sabe).
    """
    if not os.path.exists(episode_path) or not os.path.exists(reference_path):
        return {"success": False, "error": "El episodio o el archivo de referencia no existen."}

    search_offset = 0.0
    if search_from_end:
        duracion_total = _get_duration_seconds(episode_path)
        if duracion_total <= 0:
            return {"success": False, "error": "No se pudo obtener la duración del episodio (ffprobe)."}
        search_offset = max(0.0, duracion_total - search_duration)

    try:
        fps = 10
        ref_env = _extract_audio_envelope(reference_path, fps=fps)
        if len(ref_env) < fps * 5:  # menos de 5s de audio de referencia: algo salió mal
            return {"success": False, "error": "No se pudo extraer el audio de referencia (¿archivo corrupto?)."}

        ref_mean = np.mean(ref_env)
        ref_std = np.std(ref_env)
        if ref_std < 1e-5:
            return {"success": False, "error": "El audio de referencia no tiene variación (silencio)."}
        ref_norm = (ref_env - ref_mean) / ref_std
        window_size = len(ref_norm)

        ep_env = _extract_audio_envelope(episode_path, fps=fps, start=search_offset, duration=search_duration)
        if len(ep_env) < window_size:
            return {"success": True, "found": False, "confidence": 0.0}

        ep_sum = np.convolve(ep_env, np.ones(window_size), mode='valid')
        ep_sq_sum = np.convolve(ep_env**2, np.ones(window_size), mode='valid')
        ep_mean = ep_sum / window_size
        ep_var = (ep_sq_sum / window_size) - ep_mean**2
        ep_var[ep_var < 0] = 0
        ep_std = np.sqrt(ep_var)
        ep_std[ep_std < 1e-5] = 1e-5

        corr = np.correlate(ep_env, ref_norm, mode='valid')
        pearson = corr / (window_size * ep_std)

        max_idx = int(np.argmax(pearson))
        best_score = float(pearson[max_idx])

        # Umbral algo más permisivo que el de detect_opening (0.4): aquí se compara contra un
        # master oficial que puede tener pequeñas diferencias de loudness/masterización frente
        # al audio del episodio, no el mismo archivo bit a bit como al comparar dos episodios.
        if best_score < 0.35:
            return {"success": True, "found": False, "confidence": best_score}

        start_sec = search_offset + (max_idx / fps)
        end_sec = start_sec + (window_size / fps)

        return {
            "success": True,
            "found": True,
            "estimated_start": start_sec,
            "estimated_end": end_sec,
            "confidence": best_score,
            "source": "audio_reference_match"
        }

    except Exception as e:
        return {"success": False, "error": str(e)}


def _get_duration_seconds(video_path: str) -> float:
    """Duración total del archivo en segundos, vía ffprobe. 0.0 si falla."""
    cmd = [
        "ffprobe", "-v", "error",
        "-show_entries", "format=duration",
        "-of", "default=noprint_wrappers=1:nokey=1",
        video_path
    ]
    try:
        proc = subprocess.run(cmd, capture_output=True, text=True)
        if proc.returncode != 0:
            return 0.0
        return float(proc.stdout.strip())
    except (ValueError, OSError):
        return 0.0


def _extract_audio_envelope(video_path: str, fps: int, start: float = 0.0, duration: float = None) -> np.ndarray:
    """Extrae una envolvente de volumen a `fps` muestras por segundo usando ffmpeg, opcionalmente
    desde `start` segundos y limitada a `duration` segundos (None = hasta el final del archivo)."""
    # Extraemos PCM mono a 8000 Hz
    cmd = ["ffmpeg", "-hide_banner", "-loglevel", "error"]
    if start > 0:
        cmd += ["-ss", str(start)]
    cmd += ["-i", video_path]
    if duration is not None:
        cmd += ["-t", str(duration)]
    cmd += ["-ac", "1", "-ar", "8000", "-f", "f32le", "-"]

    proc = subprocess.run(cmd, capture_output=True)
    if proc.returncode != 0 or len(proc.stdout) == 0:
        return np.array([])
        
    raw_audio = np.frombuffer(proc.stdout, dtype=np.float32)
    
    # Reducir a envolvente: agrupamos muestras y tomamos el RMS
    samples_per_chunk = 8000 // fps
    num_chunks = len(raw_audio) // samples_per_chunk
    
    # Truncar para que sea múltiplo exacto
    raw_audio = raw_audio[:num_chunks * samples_per_chunk]
    reshaped = raw_audio.reshape((num_chunks, samples_per_chunk))
    
    # RMS de cada bloque
    envelope = np.sqrt(np.mean(reshaped**2, axis=1))
    return envelope
