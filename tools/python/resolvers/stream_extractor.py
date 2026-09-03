from typing import Dict, Any, List, Optional
from urllib.parse import urlparse
import os
import yt_dlp

# Impersonación (opcional): el player de zilla-networks (HLS de animeav1) está tras
# Cloudflare anti-bot; yt-dlp puede pasar el challenge si curl_cffi está instalado.
try:
    import curl_cffi  # noqa: F401
    HAVE_CURL_CFFI = True
except ImportError:
    HAVE_CURL_CFFI = False

class StreamExtractor:
    @staticmethod
    def _error_msg(ex: Exception) -> str:
        """Mensaje de error nunca vacío: str(ex) puede ser '' (p. ej. DownloadError
        del flujo de impersonación) y dejaba al C# sin causa para el log."""
        msg = str(ex).strip()
        if msg:
            return msg
        # Sin mensaje: incluir el tipo de excepción (p. ej. 'yt_dlp.utils.DownloadError')
        import traceback
        detalle = " | ".join(l.strip() for l in traceback.format_exception_only(type(ex), ex) if l.strip())
        return detalle if detalle else f"{type(ex).__name__} (sin mensaje de detalle)"

    @staticmethod
    def _is_safe_http_url(url: str) -> bool:
        """Solo http/https absolutas. Nunca esquemas locales (file://, ftp://, rutas)."""
        try:
            parsed = urlparse(url)
            return parsed.scheme in ("http", "https") and bool(parsed.netloc)
        except Exception:
            return False

    @staticmethod
    def extract_stream_info(url: str, custom_headers: Optional[Dict[str, str]] = None) -> Dict[str, Any]:
        """
        Extrae streams directos, calidades y subtítulos usando yt-dlp.
        Hardening INT-01: la URL de entrada se valida antes de tocar yt-dlp y
        los formatos/URLs devueltos se filtran a https.
        """
        if not StreamExtractor._is_safe_http_url(url):
            return {"success": False, "error": "URL no permitida (solo http/https)."}

        ydl_opts: Dict[str, Any] = {
            'quiet': True,
            'no_warnings': True,
            'extract_flat': False,
            'skip_download': True,
            'format': 'bestvideo+bestaudio/best',
            # Hardening: nunca expandir playlists (amortiguaría la descarga a cientos
            # de URLs de un proveedor comprometido) y acotar el tiempo de red.
            'noplaylist': True,
            'playlist_items': '1',
            'socket_timeout': 20,
        }
        # Impersonación contra Cloudflare (player zilla de animeav1) — solo si curl_cffi
        # (vía extractor_args: la opción 'impersonate' como string rompe en yt-dlp 2026.08)
        if HAVE_CURL_CFFI:
            ydl_opts['extractor_args'] = {'generic': ['impersonate=chrome']}
        
        if custom_headers:
            ydl_opts['http_headers'] = custom_headers

        try:
            with yt_dlp.YoutubeDL(ydl_opts) as ydl:
                info = ydl.extract_info(url, download=False)
                if not info:
                    return {"success": False, "error": "No se pudo extraer información del enlace."}

                formats_list: List[Dict[str, Any]] = []
                for f in info.get("formats", []):
                    # Filtrar formatos con URL válida y SOLO https (la app nunca
                    # descarga http en claro ni esquemas locales)
                    stream_url = f.get("url")
                    if not stream_url or not stream_url.startswith("https://"):
                        continue
                    
                    formats_list.append({
                        "format_id": str(f.get("format_id", "")),
                        "format_note": str(f.get("format_note") or f.get("resolution") or ""),
                        "url": stream_url,
                        "ext": f.get("ext", "mp4"),
                        "resolution": f"{f.get('width', 0)}x{f.get('height', 0)}" if f.get('width') else "",
                        "fps": f.get("fps"),
                        "vcodec": f.get("vcodec", ""),
                        "acodec": f.get("acodec", ""),
                        "is_hls": bool(".m3u8" in stream_url or f.get("protocol") == "m3u8_native"),
                        "is_dash": bool(".mpd" in stream_url or f.get("protocol") == "http_dash_segments")
                    })

                # Extraer subtítulos disponibles (solo https)
                subtitles: List[Dict[str, Any]] = []
                for lang, sub_entries in (info.get("subtitles") or {}).items():
                    for sub in sub_entries:
                        sub_url = sub.get("url", "")
                        if not sub_url.startswith("https://"):
                            continue
                        subtitles.append({
                            "language": lang,
                            "url": sub_url,
                            "ext": sub.get("ext", "vtt"),
                            "name": sub.get("name") or lang
                        })

                # Stream preferido directo (solo https)
                direct_stream_url = info.get("url")
                if direct_stream_url and not direct_stream_url.startswith("https://"):
                    direct_stream_url = ""
                if not direct_stream_url and formats_list:
                    # Elegir el último formato (generalmente la mejor calidad disponible)
                    direct_stream_url = formats_list[-1]["url"]

                return {
                    "success": True,
                    "title": info.get("title", ""),
                    "duration": info.get("duration", 0),
                    "direct_url": direct_stream_url or "",
                    "directUrl": direct_stream_url or "",
                    "thumbnail": info.get("thumbnail", ""),
                    "http_headers": info.get("http_headers", {}),
                    "httpHeaders": info.get("http_headers", {}),
                    "formats": formats_list,
                    "subtitles": subtitles
                }
        except Exception as ex:
            return {
                "success": False,
                "error": StreamExtractor._error_msg(ex)
            }

    @staticmethod
    def download_stream(url: str, output_path: str, custom_headers: Optional[Dict[str, str]] = None) -> Dict[str, Any]:
        """
        Descarga un stream (HLS/DASH segmentado o archivo directo) con yt-dlp a un
        archivo local. Bloqueante. Hardening INT-01: URL validada (solo http/https)
        y ruta de salida absoluta (nunca rutas relativas/relativas al cwd).
        """
        if not StreamExtractor._is_safe_http_url(url):
            return {"success": False, "error": "URL no permitida (solo http/https)."}
        if not os.path.isabs(output_path):
            return {"success": False, "error": "ruta de salida no permitida (debe ser absoluta)."}

        try:
            out_dir = os.path.dirname(output_path)
            if out_dir:
                os.makedirs(out_dir, exist_ok=True)
        except Exception as ex:
            return {"success": False, "error": f"no se pudo crear el directorio de salida: {ex}"}

        ydl_opts: Dict[str, Any] = {
            'quiet': True,
            'no_warnings': True,
            # Hardening: nunca expandir playlists y acotar red/archivo
            'noplaylist': True,
            'playlist_items': '1',
            'socket_timeout': 20,
            'format': 'best',
            'outtmpl': output_path,
            'retries': 3,
            'fragment_retries': 3,
            'concurrent_fragment_downloads': 4,
            'max_filesize': 50 * 1024 * 1024 * 1024,  # 50 GB tope por episodio
        }
        # Impersonación contra Cloudflare (player zilla de animeav1) — solo si curl_cffi
        # (vía extractor_args: la opción 'impersonate' como string rompe en yt-dlp 2026.08)
        if HAVE_CURL_CFFI:
            ydl_opts['extractor_args'] = {'generic': ['impersonate=chrome']}

        if custom_headers:
            ydl_opts['http_headers'] = custom_headers

        try:
            with yt_dlp.YoutubeDL(ydl_opts) as ydl:
                info = ydl.extract_info(url, download=True)
                if not info:
                    return {"success": False, "error": "No se pudo descargar el stream."}

                # yt-dlp añade la extensión real al outtmpl cuando no la tiene
                final = ydl.prepare_filename(info)
                if os.path.isfile(final):
                    return {"success": True, "file": final, "title": info.get("title", "")}
                if os.path.isfile(output_path):
                    return {"success": True, "file": output_path, "title": info.get("title", "")}
                return {"success": False, "error": "No se encontró el archivo descargado."}
        except Exception as ex:
            return {"success": False, "error": StreamExtractor._error_msg(ex)}
