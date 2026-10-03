import base64
import re
from typing import Any, Dict, Optional

from resolvers.http_seguro import (
    Descargador,
    cargar_paginas,
    descargar_http as _descargar_http,
    destino_seguro,
    error,
    resultado_directo,
)

# Mediafire (medido contra el sitio real, ver docs/investigacion-jkanime.md): la página del archivo trae un botón de descarga cuyo
# enlace es una DESCARGA DIRECTA (https://downloadNNNN.mediafire.com/…, acepta rangos y no exige Referer), así que entra por la
# descarga normal de la app (por trozos y reanudable). Algunas páginas lo guardan ofuscado en base64 en data-scrambled-url.
MEDIAFIRE_HOST_SUFFIXES = ("mediafire.com",)

_BOTON = re.compile(r"""<a\s[^>]*?(?:aria-label="Download file"|id="downloadButton")[^>]*>""", re.S | re.I)
_OFUSCADO = re.compile(r'data-scrambled-url="([^"]+)"')
_HREF = re.compile(r'href="([^"]*)"')
_TITULO = re.compile(r"<title>(.*?)</title>", re.S | re.I)


def es_dominio_mediafire(host: str) -> bool:
    host = (host or "").lower()
    return any(host == h or host.endswith("." + h) for h in MEDIAFIRE_HOST_SUFFIXES)


def _enlace_de_descarga(html: str) -> Optional[str]:
    for boton in _BOTON.finditer(html):
        etiqueta = boton.group(0)
        ofuscado = _OFUSCADO.search(etiqueta)
        if ofuscado:
            try:
                texto = ofuscado.group(1)
                return base64.b64decode(texto + "=" * (-len(texto) % 4)).decode("utf-8", "replace").strip()
            except Exception:
                continue
        href = _HREF.search(etiqueta)
        if href and href.group(1).startswith("http"):
            return href.group(1).strip()
    return None


class MediafireExtractor:
    @staticmethod
    def extract_mediafire(url: str, custom_headers: Optional[Dict[str, str]] = None,
                          descargar: Descargador = _descargar_http) -> Dict[str, Any]:
        def analizar(cuerpo: str, _actual: str) -> Optional[Dict[str, Any]]:
            enlace = _enlace_de_descarga(cuerpo)
            if enlace is None:
                return None
            if not destino_seguro(enlace):
                return error("Mediafire: el enlace de descarga no es seguro (solo https hacia servidores públicos).")
            titulo = _TITULO.search(cuerpo)
            return resultado_directo(enlace, titulo.group(1).strip() if titulo else "")

        return cargar_paginas(url, analizar, descargar, "Mediafire", max_pedidas=3)
