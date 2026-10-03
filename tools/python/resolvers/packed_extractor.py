import json
import re
from typing import Any, Dict, Optional
from urllib.parse import urljoin

from resolvers.http_seguro import (
    Descargador,
    cargar_paginas,
    descargar_http as _descargar_http,
    destino_seguro,
    error,
    resultado_directo,
)

# Vidhide y Streamwish usan el MISMO reproductor (medido contra los sitios reales, ver docs/investigacion-jkanime.md): la página
# trae un <script> con JavaScript empaquetado (p.a.c.k.e.r: eval(function(p,a,c,k,e,d)…), que solo comprime texto) y, al
# desempaquetarlo, un objeto `var links={"hls4":"/stream/…/master.m3u8?…","hls2":"https://…"}`; el reproductor usa
# links.hls4||links.hls3||links.hls2. Son listas HLS sin cifrar y sin Referer (Vidhide ofrece hasta 1080p; Streamwish, 720p).
# Sus dominios ROTAN (vidhidevip.com, callistanise.com, flaswish.com, sfastwish.com…): por eso el nombre del servidor que da el sitio
# manda sobre el dominio (ver StreamExtractor) y esta lista es solo un respaldo cuando no se sabe el nombre.
PACKED_HOST_SUFFIXES = (
    "vidhidevip.com", "vidhide.com", "vidhidepro.com", "callistanise.com",
    "flaswish.com", "sfastwish.com", "streamwish.com", "streamwish.to",
)

_CARACTERES = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ"
_BLOQUE = re.compile(r"eval\(function\(p,a,c,k,e,d\).*?\.split\('\|'\)[^)]*\)\)", re.S)
_PARTES = re.compile(r"\}\('(.*)',(\d+),(\d+),'(.*?)'\.split\('\|'\)", re.S)
_LINKS = re.compile(r"var\s+links\s*=\s*(\{.*?\})\s*;", re.S)
_HLS_CLAVE_VALOR = re.compile(r'"(hls\d+)"\s*:\s*"([^"]+)"')
_TITULO = re.compile(r"<title>(.*?)</title>", re.S | re.I)


def es_dominio_packed(host: str) -> bool:
    host = (host or "").lower()
    return any(host == h or host.endswith("." + h) for h in PACKED_HOST_SUFFIXES)


def _en_base(n: int, base: int) -> str:
    s = ""
    while True:
        s = _CARACTERES[n % base] + s
        n //= base
        if n == 0:
            return s


def desempaquetar(js: str) -> Optional[str]:
    """Deshace el empaquetado p.a.c.k.e.r del primer bloque que encuentre (cada palabra del texto es un índice en una lista). None si no hay."""
    bloque = _BLOQUE.search(js or "")
    partes = _PARTES.search(bloque.group(0)) if bloque else None
    if not partes:
        return None
    try:
        texto, base, cuenta, palabras = partes.group(1).replace("\\'", "'"), int(partes.group(2)), int(partes.group(3)), partes.group(4).split("|")
        diccionario = {_en_base(i, base): (palabras[i] if i < len(palabras) and palabras[i] else _en_base(i, base)) for i in range(cuenta)}
        return re.sub(r"\b\w+\b", lambda m: diccionario.get(m.group(0), m.group(0)), texto).replace("\\\\", "\\")
    except Exception:
        return None


def _leer_enlaces(js: str) -> Dict[str, str]:
    m = _LINKS.search(js)
    if m:
        try:
            datos = json.loads(m.group(1))
            if isinstance(datos, dict):
                return {k: v for k, v in datos.items() if isinstance(k, str) and isinstance(v, str)}
        except ValueError:
            pass
    return dict(_HLS_CLAVE_VALOR.findall(js))


def _por_calidad(enlaces: Dict[str, str]):
    """hls4 antes que hls3 antes que hls2, como el propio reproductor."""
    return sorted((k for k in enlaces if re.fullmatch(r"hls\d+", k)), key=lambda k: int(k[3:]), reverse=True)


def _responde_sin_referer(lista: str, descargar: Descargador) -> bool:
    """¿La lista HLS responde sin cabeceras? La descarga HLS del daemon no manda Referer, y algunos espejos (Streamwish publica dos, en
    dominios distintos) dan 404 sin el de la página."""
    try:
        estado, _, cuerpo = descargar(lista, None)
        return estado == 200 and "#EXTM3U" in cuerpo[:200]
    except Exception:
        return False


class PackedHlsExtractor:
    @staticmethod
    def extract_packed(url: str, custom_headers: Optional[Dict[str, str]] = None,
                       descargar: Descargador = _descargar_http) -> Dict[str, Any]:
        def analizar(cuerpo: str, actual: str) -> Optional[Dict[str, Any]]:
            for bloque in _BLOQUE.finditer(cuerpo):
                js = desempaquetar(bloque.group(0))
                enlaces = _leer_enlaces(js) if js else {}
                candidatas = [c for c in (urljoin(actual, enlaces[k]) for k in _por_calidad(enlaces)) if destino_seguro(c)]
                if candidatas:
                    # La de mayor calidad que responda sola; si ninguna contesta (¿fallo pasajero?) no se descarta el video: la mejor.
                    lista = next((c for c in candidatas if _responde_sin_referer(c, descargar)), candidatas[0])
                    titulo = _TITULO.search(cuerpo)
                    return resultado_directo(lista, titulo.group(1).strip() if titulo else "")
            return None

        return cargar_paginas(url, analizar, descargar, "Vidhide/Streamwish")
