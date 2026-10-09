import hashlib
import os
import subprocess
import threading
import time
from concurrent.futures import ThreadPoolExecutor
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

# ─────────────────────────────────────────────────────────────────────────────────────────────
#  Opening/ending con el audio oficial de AnimeThemes (todas las referencias en UNA llamada)
# ─────────────────────────────────────────────────────────────────────────────────────────────
#
# Antes se hacía una llamada por tema, y cada una volvía a decodificar 300 s del episodio (+ ffprobe para los endings) y el
# tema entero: ~1 s por tema. Con 29 temas descargados (One Piece) un episodio sin coincidencia en su rango tardaba ~30 s y
# tenía ocupado el motor Python todo ese rato. Ahora el episodio se decodifica una vez (inicio y final a la vez, en paralelo),
# la huella de cada tema se guarda en disco (la segunda vez no se decodifica) y la comparación usa FFT: ~1-2 ms por tema.
#
# Huella = 10 fotogramas/s de [volumen RMS + energía (log) en 7 bandas de frecuencia]. Solo con el volumen, dos cortes de la
# misma canción (OP1 y OP1 v2) o un tema equivocado daban 0,55-0,68 frente a 0,86-1,0 del correcto; con las bandas los
# equivocados bajan a ~0,3-0,6 (medido con 25 episodios reales de 9 animes).

_FPS = 10
_SR = 8000
_BANDAS_HZ = (0, 150, 300, 600, 1000, 1600, 2400, 4000)
_PESOS = np.array([0.5] + [0.5 / (len(_BANDAS_HZ) - 1)] * (len(_BANDAS_HZ) - 1))
_FORMATO_HUELLA = 1
_MAX_HUELLAS_EN_DISCO = 800

# Coincidencia "completa" con esta confianza o más: no hace falta mirar por trozos.
_CONFIANZA_SEGURA = 0.8
# Por trozos (el episodio usa solo una parte del tema, o hay diálogo encima de un tramo): trozos de 10 s cada 5 s; un trozo
# cuenta si pasa de 0,65 y el tramo encontrado debe durar al menos 30 s (o la mitad del tema) para no penalizarlo.
_TROZO = 10 * _FPS
_PASO_TROZO = 5 * _FPS
_UMBRAL_TROZO = 0.65
_TRAMO_MINIMO = 30 * _FPS
# Solo se analizan por trozos los candidatos con cierta similitud completa (los demás no son la canción).
_MINIMO_PARA_TROZOS = 0.45
_MAXIMO_CANDIDATOS_TROZOS = 4
# El opening se busca como mucho hasta esta fracción del episodio. Más allá el mismo tema suele sonar como canción de fondo del
# clímax (episodios finales): marcarlo haría que "saltar opening" se llevara la escena.
_LIMITE_OPENING = 0.5


def detect_themes(episode_path: str, references: List[Dict[str, Any]], min_confidence: float = 0.7,
                  head_seconds: float = 480.0, tail_seconds: float = 360.0, cache_dir: str = None) -> Dict[str, Any]:
    """
    Ubica el opening (al inicio) y el ending (al final) de un episodio comparándolo con los temas oficiales.

    references: [{"path", "kind": "OP"|"ED", "priority": 0 (aplica al episodio según AnimeThemes) | 1 (resto)}].
    Por cada tramo se prueban grupos en orden y se para en el primero que acierta (así los temas que no aplican ni se
    decodifican si el que aplica ya coincide):
      opening → OP que aplican, OP restantes; si no aparece en head_seconds, otra vez hasta la mitad del episodio.
      ending  → ED que aplican, ED restantes y, por último, los OP (primero los que aplican): el episodio 1 y los finales
                suelen cerrar con el opening; antes ese ending no se detectaba nunca.
    Devuelve matches con segment "op"/"ed", reference_path, start, end, confidence y mode ("full"/"partial").
    """
    t0 = time.monotonic()
    if not _es_ruta_local(episode_path) or not os.path.isfile(episode_path):
        return {"success": False, "error": "El episodio no existe."}

    refs = [r for r in (references or []) if isinstance(r, dict) and _es_ruta_local(r.get("path")) and os.path.isfile(r.get("path"))]
    if not refs:
        return {"success": True, "matches": [], "evaluated": 0, "seconds": 0.0}

    duracion = _get_duration_seconds(episode_path)
    if duracion <= 0:
        return {"success": False, "error": "No se pudo obtener la duración del episodio (ffprobe)."}

    try:
        inicio, final = _ventanas_episodio(episode_path, duracion, head_seconds, tail_seconds)
        if inicio is None or final is None:
            return {"success": False, "error": "No se pudo extraer el audio del episodio."}

        huellas: Dict[str, Any] = {}
        evaluados = set()

        def grupo(kind, priority=None):
            return [r["path"] for r in refs
                    if str(r.get("kind", "")).upper() == kind and (priority is None or int(r.get("priority", 1)) == priority)]

        matches = []
        grupos_op = [grupo("OP", 0), grupo("OP", 1)]
        op = _buscar_en_grupos(inicio, grupos_op, huellas, evaluados, cache_dir, min_confidence, None)
        if not op:
            # No está en los primeros minutos: un episodio doble o con una apertura en frío larga lo trae más tarde (Sasaki to
            # Pii-chan 2, episodio 1 de 47 min: suena a los 8:21). Se mira hasta la mitad del episodio, y solo ahora, para no
            # decodificar de más en el caso normal. El tramo empieza un tema antes del límite: cubre el que cae a caballo.
            largo_tema = max((len(h) for r in grupos_op[0] + grupos_op[1] if (h := huellas.get(r)) is not None), default=0) / _FPS
            hasta = duracion * _LIMITE_OPENING
            if largo_tema > 0 and hasta > head_seconds:
                tardio = _ventana_tramo(episode_path, max(0.0, head_seconds - largo_tema), hasta)
                if tardio:
                    op = _buscar_en_grupos(tardio, grupos_op, huellas, evaluados, cache_dir, min_confidence, None)
        if op:
            matches.append(dict(op, segment="op"))

        excluir = (op["start"], op["end"]) if op else None
        ed = _buscar_en_grupos(final, [grupo("ED", 0), grupo("ED", 1), grupo("OP", 0), grupo("OP", 1)], huellas, evaluados, cache_dir, min_confidence, excluir)
        if ed:
            matches.append(dict(ed, segment="ed"))

        return {"success": True, "matches": matches, "evaluated": len(evaluados), "duration": duracion,
                "seconds": round(time.monotonic() - t0, 3)}
    except Exception as e:
        return {"success": False, "error": str(e)}


def _es_ruta_local(ruta) -> bool:
    """Solo archivos locales: ffmpeg también abre URLs y esquemas raros, y aquí nunca hacen falta."""
    return isinstance(ruta, str) and bool(ruta) and "://" not in ruta and not ruta.startswith("-")


def _ventanas_episodio(ruta: str, duracion: float, segundos_inicio: float, segundos_final: float):
    """(inicio, final) como _Ventana. Las dos extracciones van en paralelo (ffmpeg no depende del GIL); en episodios cortos
    las ventanas se solapan y se decodifica el archivo entero una sola vez."""
    largo_inicio = min(segundos_inicio, duracion)
    comienzo_final = max(0.0, duracion - segundos_final)

    if comienzo_final <= largo_inicio:
        huella = _huella(_pcm(ruta))
        if len(huella) == 0:
            return None, None
        corte = int(comienzo_final * _FPS)
        return _Ventana(0.0, huella[:int(largo_inicio * _FPS)]), _Ventana(corte / _FPS, huella[corte:])

    with ThreadPoolExecutor(max_workers=2) as pool:
        f_inicio = pool.submit(_pcm, ruta, 0.0, largo_inicio)
        f_final = pool.submit(_pcm, ruta, comienzo_final, segundos_final)
        h_inicio, h_final = _huella(f_inicio.result()), _huella(f_final.result())
    if len(h_inicio) == 0 or len(h_final) == 0:
        return None, None
    return _Ventana(0.0, h_inicio), _Ventana(comienzo_final, h_final)


def _ventana_tramo(ruta: str, desde: float, hasta: float):
    """_Ventana de un tramo suelto del episodio (None si no se pudo leer)."""
    huella = _huella(_pcm(ruta, desde, hasta - desde))
    return _Ventana(desde, huella) if len(huella) else None


def _buscar_en_grupos(ventana, grupos, huellas, evaluados, cache_dir, min_confidence, excluir):
    for rutas in grupos:
        pendientes = [r for r in rutas if r not in huellas]
        if pendientes:
            with ThreadPoolExecutor(max_workers=min(4, len(pendientes))) as pool:
                for ruta, h in zip(pendientes, pool.map(lambda r: _huella_referencia(r, cache_dir), pendientes)):
                    huellas[ruta] = h

        candidatos = [(r, huellas[r]) for r in rutas if huellas.get(r) is not None]
        evaluados.update(r for r, _ in candidatos)
        mejor = _mejor_coincidencia(ventana, candidatos, excluir)
        if mejor and mejor["confidence"] >= min_confidence:
            return mejor
    return None


def _mejor_coincidencia(ventana, candidatos, excluir):
    completas = []
    for ruta, huella in candidatos:
        c = _coincidencia_completa(ventana, huella)
        completas.append((c["confidence"] if c else -1.0, ruta, huella, c))

    validas = [c for _, ruta, _, c in completas if c and not _solapa(c, excluir)]
    mejor = max(validas, key=lambda c: c["confidence"], default=None)
    if mejor and mejor["confidence"] >= _CONFIANZA_SEGURA:
        return dict(mejor, reference_path=_ruta_de(completas, mejor))

    # Nadie coincide entero: probar por trozos solo los que se parecen algo (o los temas más largos que la ventana).
    completas.sort(key=lambda x: x[0], reverse=True)
    for conf, ruta, huella, c in completas[:_MAXIMO_CANDIDATOS_TROZOS]:
        if c is not None and conf < _MINIMO_PARA_TROZOS:
            continue
        p = _coincidencia_por_trozos(ventana, huella)
        if p and not _solapa(p, excluir) and (mejor is None or p["confidence"] > mejor["confidence"]):
            mejor = dict(p, reference_path=ruta)

    if mejor and "reference_path" not in mejor:
        mejor = dict(mejor, reference_path=_ruta_de(completas, mejor))
    return mejor


def _ruta_de(completas, coincidencia):
    return next(ruta for _, ruta, _, c in completas if c is coincidencia)


def _solapa(c, excluir) -> bool:
    """Más de la mitad del tramo cae dentro del otro (el ending no puede ser el mismo opening ya encontrado)."""
    if not excluir:
        return False
    comun = min(c["end"], excluir[1]) - max(c["start"], excluir[0])
    return comun > 0.5 * min(c["end"] - c["start"], excluir[1] - excluir[0])


def _coincidencia_completa(ventana, huella):
    largo = len(huella)
    if largo > ventana.n:
        return None
    curva = ventana.curva(huella)
    if len(curva) == 0:
        return None
    i = int(np.argmax(curva))
    return {"start": ventana.inicio + i / _FPS, "end": ventana.inicio + (i + largo) / _FPS,
            "confidence": float(curva[i]), "mode": "full"}


def _coincidencia_por_trozos(ventana, huella):
    """
    Busca un desfase en el que varios trozos seguidos del tema coinciden a la vez. Sirve cuando el episodio usa solo una parte
    (ending recortado, opening que arranca a mitad) o hay diálogo encima de un tramo. Un trozo suelto de 10 s puede parecerse
    por azar (~0,4-0,6 en cualquier sitio); varios seguidos con el mismo desfase, no.
    """
    largo = len(huella)
    if largo < _TROZO or ventana.n < _TROZO:
        return None

    inicios = list(range(0, largo - _TROZO + 1, _PASO_TROZO))
    desfase_min = -(largo - _TROZO)
    tamano = (ventana.n - _TROZO) - desfase_min + 1
    curvas = np.full((len(inicios), tamano), -1.0)
    for k, c in enumerate(inicios):
        curva = ventana.curva(huella[c:c + _TROZO])
        o0 = -c - desfase_min
        curvas[k, o0:o0 + len(curva)] = curva

    puntos = np.where(curvas >= _UMBRAL_TROZO, curvas, 0.0).sum(axis=0)
    o = int(np.argmax(puntos))
    if puntos[o] <= 0:
        return None
    valores = curvas[:, o]
    ok = valores >= _UMBRAL_TROZO

    # Tramos de trozos buenos seguidos (se tolera un trozo malo en medio: un grito o un efecto tapando la música).
    tramos, actual, ultimo = [], None, None
    for k in range(len(inicios)):
        if ok[k]:
            if actual is not None and k - ultimo <= 2:
                actual.append(k)
            else:
                actual = [k]
                tramos.append(actual)
            ultimo = k
    tramo = max(tramos, key=lambda t: inicios[t[-1]] - inicios[t[0]])

    a, b = inicios[tramo[0]], inicios[tramo[-1]] + _TROZO
    confianza = float(np.mean(valores[tramo]))
    necesario = min(_TRAMO_MINIMO, 0.5 * largo)
    if b - a < necesario:
        confianza *= (b - a) / necesario
    desfase = o + desfase_min
    return {"start": ventana.inicio + max(0, desfase + a) / _FPS, "end": ventana.inicio + min(ventana.n, desfase + b) / _FPS,
            "confidence": confianza, "mode": "partial"}


class _Ventana:
    """Huella de un tramo del episodio con lo que se reutiliza entre temas (sumas acumuladas y espectros por tamaño de FFT)."""

    def __init__(self, inicio: float, huella: np.ndarray):
        self.inicio = inicio
        self.h = huella.astype(np.float64)
        self.n = len(huella)
        ceros = np.zeros((1, self.h.shape[1]))
        self._suma = np.vstack([ceros, np.cumsum(self.h, axis=0)])
        self._suma2 = np.vstack([ceros, np.cumsum(self.h * self.h, axis=0)])
        self._espectros = {}

    def curva(self, segmento: np.ndarray) -> np.ndarray:
        """Correlación de Pearson del segmento en cada posición de la ventana (media ponderada por columna, -1..1)."""
        largo = len(segmento)
        if largo < 2 or largo > self.n:
            return np.array([])

        seg = segmento.astype(np.float64)
        desv = seg.std(axis=0)
        validas = desv > 1e-6
        if not validas.any():
            return np.array([])
        norm = (seg - seg.mean(axis=0)) / np.where(validas, desv, 1.0)

        nfft = 1 << int(np.ceil(np.log2(self.n + largo)))
        if nfft not in self._espectros:
            self._espectros[nfft] = np.fft.rfft(self.h, nfft, axis=0)
        corr = np.fft.irfft(self._espectros[nfft] * np.conj(np.fft.rfft(norm, nfft, axis=0)), nfft, axis=0)[:self.n - largo + 1]

        media = (self._suma[largo:] - self._suma[:-largo]) / largo
        var = (self._suma2[largo:] - self._suma2[:-largo]) / largo - media * media
        pearson = corr / (largo * np.sqrt(np.maximum(var, 1e-12)))
        pesos = np.where(validas, _PESOS, 0.0)
        return np.clip(pearson @ (pesos / pesos.sum()), -1.0, 1.0)


def _pcm(ruta: str, inicio: float = 0.0, duracion: float = None) -> np.ndarray:
    """Audio mono a 8 kHz en float32 (vacío si ffmpeg falla)."""
    cmd = ["ffmpeg", "-hide_banner", "-loglevel", "error", "-nostdin", "-max_alloc", "2147483648"]
    if inicio > 0:
        cmd += ["-ss", f"{inicio:.3f}"]
    cmd += ["-i", ruta, "-vn", "-sn", "-dn"]
    if duracion is not None:
        cmd += ["-t", f"{duracion:.3f}"]
    cmd += ["-ac", "1", "-ar", str(_SR), "-f", "f32le", "-"]
    try:
        proc = subprocess.run(cmd, capture_output=True)
    except OSError:
        return np.array([], dtype=np.float32)
    if proc.returncode != 0 or len(proc.stdout) == 0:
        return np.array([], dtype=np.float32)
    return np.frombuffer(proc.stdout, dtype=np.float32)


def _huella(pcm: np.ndarray) -> np.ndarray:
    """(fotogramas, 8): RMS + log10 de la energía por banda, 10 fotogramas por segundo. Por bloques para no disparar la memoria."""
    n = _SR // _FPS
    total = len(pcm) // n
    if total == 0:
        return np.zeros((0, len(_BANDAS_HZ)), dtype=np.float32)

    frecuencias = np.fft.rfftfreq(n, 1.0 / _SR)
    mascaras = [(frecuencias >= a) & (frecuencias < b) for a, b in zip(_BANDAS_HZ[:-1], _BANDAS_HZ[1:])]
    ventana = np.hanning(n)
    partes = []
    for i in range(0, total, 2000):
        bloque = pcm[i * n:min(total, i + 2000) * n].reshape(-1, n).astype(np.float64)
        rms = np.sqrt(np.mean(bloque ** 2, axis=1))
        espectro = np.abs(np.fft.rfft(bloque * ventana, axis=1)) ** 2
        bandas = np.stack([espectro[:, m].sum(axis=1) for m in mascaras], axis=1)
        partes.append(np.column_stack([rms, np.log10(bandas + 1e-9)]))
    return np.vstack(partes).astype(np.float32)


def _recortar_silencio(huella: np.ndarray) -> np.ndarray:
    """Quita el silencio del principio y del final del tema: el tramo detectado empieza y acaba donde suena la música."""
    if len(huella) == 0:
        return huella
    rms = huella[:, 0]
    sonando = np.where(rms > max(float(rms.max()) * 0.02, 1e-4))[0]
    return huella[sonando[0]:sonando[-1] + 1] if len(sonando) else huella[:0]


def _huella_referencia(ruta: str, cache_dir: str = None):
    """Huella del tema (sin silencios en los extremos), guardada en disco: la segunda vez no se decodifica. None si no sirve."""
    try:
        clave = _clave_huella(ruta)
    except OSError:
        return None

    archivo = os.path.join(cache_dir, clave + ".npy") if cache_dir else None
    if archivo and os.path.isfile(archivo):
        try:
            huella = np.load(archivo, allow_pickle=False)
            if huella.ndim == 2 and huella.shape[1] == len(_BANDAS_HZ):
                try:
                    os.utime(archivo)  # orden de uso para la poda
                except OSError:
                    pass
                return huella if len(huella) >= 5 * _FPS else None
        except (OSError, ValueError):
            pass

    huella = _recortar_silencio(_huella(_pcm(ruta)))
    if archivo and len(huella) > 0:
        _guardar_huella(archivo, huella, cache_dir)
    return huella if len(huella) >= 5 * _FPS else None  # menos de 5 s: archivo roto o vacío


def _clave_huella(ruta: str) -> str:
    """Por contenido (tamaño + primeros y últimos 64 KB), no por nombre: renombrar el mp3 no obliga a recalcular."""
    tamano = os.path.getsize(ruta)
    h = hashlib.sha1(f"{_FORMATO_HUELLA}|{tamano}|".encode("ascii"))
    with open(ruta, "rb") as f:
        h.update(f.read(65536))
        if tamano > 131072:
            f.seek(-65536, os.SEEK_END)
            h.update(f.read(65536))
    return h.hexdigest()


def _guardar_huella(archivo: str, huella: np.ndarray, cache_dir: str):
    try:
        os.makedirs(cache_dir, exist_ok=True)
        temporal = f"{archivo}.{os.getpid()}.{threading.get_ident()}.tmp"
        with open(temporal, "wb") as f:
            np.save(f, huella, allow_pickle=False)
        os.replace(temporal, archivo)
        _podar_huellas(cache_dir)
    except OSError:
        pass  # sin caché solo se pierde velocidad


def _podar_huellas(cache_dir: str):
    """Tope de archivos (~30 KB cada uno): al pasarlo se borran los menos usados."""
    try:
        entradas = [e for e in os.scandir(cache_dir) if e.is_file() and e.name.endswith(".npy")]
        if len(entradas) <= _MAX_HUELLAS_EN_DISCO:
            return
        entradas.sort(key=lambda e: e.stat().st_mtime)
        for e in entradas[:len(entradas) - int(_MAX_HUELLAS_EN_DISCO * 0.75)]:
            try:
                os.remove(e.path)
            except OSError:
                pass
    except OSError:
        pass


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
