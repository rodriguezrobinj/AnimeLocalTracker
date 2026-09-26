import os
from typing import Set

# Hardening FFmpeg (sec): ffmpeg/ffprobe aceptan URLs y esquemas locales; la app
# solo procesa archivos locales de la biblioteca. Validar antes de invocar.
VIDEO_EXTENSIONS: Set[str] = {
    ".mkv", ".mp4", ".avi", ".webm", ".ts", ".mov", ".m4v", ".flv", ".wmv", ".m2ts"
}


def es_ruta_media_segura(ruta: str) -> bool:
    """Solo archivos locales existentes con extensión de video. Nunca URLs ni rutas relativas extrañas."""
    if not ruta or not isinstance(ruta, str):
        return False
    if "://" in ruta:  # file://, http://, https://, ftp:// ... nunca procesar URLs
        return False
    if not os.path.isfile(ruta):
        return False
    ext = os.path.splitext(ruta)[1].lower()
    return ext in VIDEO_EXTENSIONS


# Límite de memoria por asignación de av_malloc en ffmpeg/ffprobe (2 GB por bloque):
# acota bombas de asignación de headers/demuxers maliciosos sin afectar archivos legítimos.
MAX_ALLOC = "2147483648"  # 2 GB


def argumentos_ffprobe() -> list:
    """Opciones de seguridad y silencio comunes a TODAS las llamadas a ffprobe.

    OJO: ffprobe NO admite ``-nostdin`` (es una opción exclusiva de ffmpeg). Pasárselo hace que ffprobe
    salga con error ("Failed to set value ... for option 'nostdin'") y, como los llamadores tragaban
    el fallo, la app dejó de recibir resolución/códec/fps/10-bit de cada episodio sin ningún aviso.
    ffprobe además nunca lee stdin, así que no lo necesita.
    """
    return ["-max_alloc", MAX_ALLOC, "-v", "error"]

