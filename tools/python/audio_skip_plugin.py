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

def _extract_audio_envelope(video_path: str, duration: int, fps: int) -> np.ndarray:
    """Extrae una envolvente de volumen a `fps` muestras por segundo usando ffmpeg."""
    # Extraemos PCM mono a 8000 Hz
    cmd = [
        "ffmpeg", "-hide_banner", "-loglevel", "error",
        "-i", video_path,
        "-t", str(duration),
        "-ac", "1", "-ar", "8000",
        "-f", "f32le", "-"
    ]
    
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
