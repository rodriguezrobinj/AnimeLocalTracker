import base64
import codecs
import json
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

# Voe se resuelve solo con HTTP (medido contra el sitio real, ver docs/investigacion-servidores-descarga.md):
#   1. https://voe.sx/e/<id> es una página mínima que redirige POR JAVASCRIPT a un dominio que rota.
#   2. Esa segunda página trae un <script type="application/json"> con una cadena ofuscada que, decodificada, es el
#      JSON del reproductor: "direct_access_url" (MP4, acepta rangos y no exige Referer) y "source" (HLS sin cifrar).
# Sin navegador, sin captcha y sin cargar sus anuncios. Si Voe cambia la ofuscación, decodificar_payload devuelve None y
# el proveedor sigue con el siguiente servidor (nunca rompe la descarga).
VOE_HOST_SUFFIXES = ("voe.sx",)

_MARCADORES = ("@$", "^^", "~@", "%?", "*~", "!!", "#&")
_SCRIPT_JSON = re.compile(r"""<script[^>]*type=["']application/json["'][^>]*>\s*(.*?)\s*</script>""", re.S | re.I)


def es_dominio_voe(host: str) -> bool:
    host = (host or "").lower()
    return any(host == h or host.endswith("." + h) for h in VOE_HOST_SUFFIXES)


def _relleno(texto: str) -> str:
    return texto + "=" * (-len(texto) % 4)


def decodificar_payload(texto: str) -> Optional[Dict[str, Any]]:
    """ROT13 → quitar marcadores → base64 → desplazar -3 → invertir → base64 → JSON. None si no encaja."""
    try:
        s = codecs.decode(texto, "rot13")
        for marcador in _MARCADORES:
            s = s.replace(marcador, "")
        s = base64.b64decode(_relleno(s)).decode("latin-1")
        s = "".join(chr(ord(c) - 3) for c in s)[::-1]
        datos = json.loads(base64.b64decode(_relleno(s)))
        return datos if isinstance(datos, dict) else None
    except Exception:
        return None


def _extraer_payload(html: str) -> Optional[Dict[str, Any]]:
    for m in _SCRIPT_JSON.finditer(html):
        try:
            contenido = json.loads(m.group(1))
        except ValueError:
            continue
        for candidato in contenido if isinstance(contenido, list) else [contenido]:
            datos = decodificar_payload(candidato) if isinstance(candidato, str) else None
            if datos and (datos.get("direct_access_url") or datos.get("source")):
                return datos
    return None


class VoeExtractor:
    @staticmethod
    def extract_voe(url: str, custom_headers: Optional[Dict[str, str]] = None,
                    descargar: Descargador = _descargar_http) -> Dict[str, Any]:
        def analizar(cuerpo: str, _actual: str) -> Optional[Dict[str, Any]]:
            datos = _extraer_payload(cuerpo)
            return VoeExtractor._resultado(datos) if datos else None

        return cargar_paginas(url, analizar, descargar, "Voe")

    @staticmethod
    def _resultado(datos: Dict[str, Any]) -> Dict[str, Any]:
        # MP4 directo primero (acepta rangos: reanudación y descarga por trozos); el HLS solo si no hay MP4.
        directo = next((u for u in (datos.get("direct_access_url"), datos.get("source"))
                        if isinstance(u, str) and destino_seguro(u)), "")
        if not directo:
            return error("Voe: el reproductor no trae un enlace https válido.")
        return resultado_directo(directo, str(datos.get("title") or ""))
