import ipaddress
import re
import socket
import urllib.error
import urllib.request
from typing import Any, Callable, Dict, List, Optional, Tuple
from urllib.parse import urljoin, urlparse

# Piezas comunes de los extractores que resuelven con HTTP puro (Voe, Mediafire, Vidhide/Streamwish): pedir páginas validando CADA
# destino (la redirección la dicta una página de terceros, así que no puede llevar a la propia máquina ni a la red local) y seguir
# las redirecciones a mano con un tope.

_SALTO_JS = re.compile(r"""window\.location(?:\.href)?\s*=\s*['"]([^'"]+)['"]""")
_SUFIJOS_LOCALES = (".localhost", ".local", ".internal", ".lan", ".home.arpa", ".localdomain")

_MAX_BYTES = 3_000_000
_TIMEOUT_S = 20
USER_AGENT = (
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) "
    "Chrome/120.0.0.0 Safari/537.36"
)

# (url, referer) -> (estado HTTP, Location si es 3xx, cuerpo como texto)
Descargador = Callable[[str, Optional[str]], Tuple[int, Optional[str], str]]


def ips_de(host: str) -> List[str]:
    return [info[4][0] for info in socket.getaddrinfo(host, 443, type=socket.SOCK_STREAM)]


def destino_seguro(url: str) -> bool:
    """Solo https hacia un servidor de Internet: IP literal privada, nombres de intranet o dominios que resuelvan a una IP privada se rechazan."""
    try:
        parsed = urlparse(url)
        host = (parsed.hostname or "").rstrip(".").lower()
        if parsed.scheme != "https" or not host or parsed.username or parsed.password:
            return False
        try:
            return ipaddress.ip_address(host).is_global
        except ValueError:
            pass
        if "." not in host or host.endswith(_SUFIJOS_LOCALES):
            return False
        ips = ips_de(host)
        return bool(ips) and all(ipaddress.ip_address(ip).is_global for ip in ips)
    except Exception:
        return False


class _SinRedirecciones(urllib.request.HTTPRedirectHandler):
    """Las redirecciones se siguen a mano para validar cada destino antes de pedirlo."""

    def redirect_request(self, *args, **kwargs):
        return None


def descargar_http(url: str, referer: Optional[str] = None) -> Tuple[int, Optional[str], str]:
    """(estado, Location si es 3xx, cuerpo como texto). Lanza solo por fallos de red."""
    cabeceras = {"User-Agent": USER_AGENT}
    if referer:
        cabeceras["Referer"] = referer
    opener = urllib.request.build_opener(_SinRedirecciones)
    try:
        with opener.open(urllib.request.Request(url, headers=cabeceras), timeout=_TIMEOUT_S) as r:
            return r.status, None, r.read(_MAX_BYTES).decode("utf-8", "replace")
    except urllib.error.HTTPError as e:
        return e.code, e.headers.get("Location"), ""


def error(mensaje: str) -> Dict[str, Any]:
    return {"success": False, "error": mensaje}


def resultado_directo(url_media: str, titulo: str = "") -> Dict[str, Any]:
    """Respuesta de resolve-stream para una URL de video ya resuelta (archivo directo o lista HLS)."""
    return {
        "success": True,
        "title": titulo,
        "duration": 0,
        "direct_url": url_media,
        "directUrl": url_media,
        "thumbnail": "",
        "http_headers": {},
        "httpHeaders": {},
        "formats": [],
        "subtitles": [],
    }


def cargar_paginas(
    url: str,
    analizar: Callable[[str, str], Optional[Dict[str, Any]]],
    descargar: Optional[Descargador] = None,
    nombre: str = "Sitio",
    max_pedidas: int = 4,
) -> Dict[str, Any]:
    """
    Pide la página y se la da a <analizar>(cuerpo, url_actual): si devuelve un resultado (de éxito o de error) se termina; si devuelve None
    se sigue la redirección (3xx o por JavaScript) hacia otra página, validando cada destino, con un tope de páginas pedidas.
    """
    descargar = descargar or descargar_http
    actual, referer = url, None
    for _ in range(max_pedidas):
        if not destino_seguro(actual):
            return error(f"{nombre}: destino no permitido (solo https hacia servidores públicos).")
        try:
            estado, ubicacion, cuerpo = descargar(actual, referer)
        except Exception as ex:
            return error(f"{nombre}: no se pudo cargar la página ({ex}).")

        siguiente = None
        if 300 <= estado < 400 and ubicacion:
            siguiente = urljoin(actual, ubicacion)
        elif estado == 200:
            resultado = analizar(cuerpo, actual)
            if resultado is not None:
                return resultado
            salto = _SALTO_JS.search(cuerpo)
            if salto:
                siguiente = urljoin(actual, salto.group(1))
        else:
            return error(f"{nombre} respondió HTTP {estado}.")

        if not siguiente:
            return error(f"{nombre}: la página no trae el video (video borrado o formato de {nombre} no reconocido).")
        referer, actual = actual, siguiente
    return error(f"{nombre}: demasiados saltos de redirección.")
