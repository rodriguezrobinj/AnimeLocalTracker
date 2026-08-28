from typing import Dict, Any, List, Optional
import yt_dlp

class StreamExtractor:
    @staticmethod
    def extract_stream_info(url: str, custom_headers: Optional[Dict[str, str]] = None) -> Dict[str, Any]:
        """
        Extrae streams directos, calidades y subtítulos usando yt-dlp.
        """
        ydl_opts: Dict[str, Any] = {
            'quiet': True,
            'no_warnings': True,
            'extract_flat': False,
            'skip_download': True,
            'format': 'bestvideo+bestaudio/best',
        }
        
        if custom_headers:
            ydl_opts['http_headers'] = custom_headers

        try:
            with yt_dlp.YoutubeDL(ydl_opts) as ydl:
                info = ydl.extract_info(url, download=False)
                if not info:
                    return {"success": False, "error": "No se pudo extraer información del enlace."}

                formats_list: List[Dict[str, Any]] = []
                for f in info.get("formats", []):
                    # Filtrar formatos con URL válida
                    stream_url = f.get("url")
                    if not stream_url:
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

                # Extraer subtítulos disponibles
                subtitles: List[Dict[str, Any]] = []
                for lang, sub_entries in (info.get("subtitles") or {}).items():
                    for sub in sub_entries:
                        subtitles.append({
                            "language": lang,
                            "url": sub.get("url", ""),
                            "ext": sub.get("ext", "vtt"),
                            "name": sub.get("name") or lang
                        })

                # Stream preferido directo
                direct_stream_url = info.get("url")
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
                "error": str(ex)
            }
