import json
import os
import re
import sys

import pytest

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), '..')))

from resolvers import http_seguro
from resolvers.packed_extractor import PackedHlsExtractor, desempaquetar, es_dominio_packed

IP_PUBLICA = "93.184.216.34"
_CARACTERES = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ"


def _base(n: int, radix: int = 62) -> str:
    s = ""
    while True:
        s = _CARACTERES[n % radix] + s
        n //= radix
        if n == 0:
            return s


def empaquetar(js: str) -> str:
    """Empaqueta JavaScript como lo hace p.a.c.k.e.r (el formato de eval(function(p,a,c,k,e,d)…) que usan Vidhide y Streamwish)."""
    tokens = sorted(set(re.findall(r"\b\w+\b", js)))
    indice = {t: _base(i) for i, t in enumerate(tokens)}
    payload = re.sub(r"\b\w+\b", lambda m: indice[m.group(0)], js).replace("\\", "\\\\").replace("'", "\\'")
    return ("eval(function(p,a,c,k,e,d){while(c--)if(k[c])p=p.replace(new RegExp('\\\\b'+c.toString(a)+'\\\\b','g'),k[c]);return p}"
            f"('{payload}',62,{len(tokens)},'{'|'.join(tokens)}'.split('|'),0,{{}}))")


def pagina(links: dict, titulo: str = "Embed") -> str:
    js = ('var links=' + json.dumps(links) + ';var player=jwplayer("vplayer");'
          'player.setup({sources:[{file:links.hls4||links.hls3||links.hls2,type:"hls"}],image:"https://x.example/p.jpg"});')
    return (f"<html><head><title>{titulo}</title></head><body>\n<div id=\"vplayer\"></div>\n"
            f"<script type='text/javascript'>{empaquetar(js)}</script>\n</body></html>")


@pytest.fixture(autouse=True)
def dns_publico(monkeypatch):
    """Ningún test resuelve DNS de verdad: todo host de ejemplo resuelve a una IP pública."""
    monkeypatch.setattr(http_seguro, "ips_de", lambda host: [IP_PUBLICA])


def fake_descargar(paginas: dict):
    pedidas = []

    def descargar(url, referer=None):
        pedidas.append(url)
        return paginas.get(url, (404, None, ""))

    descargar.pedidas = pedidas
    return descargar


EMBED = "https://vidhide.example.org/e/ja75jxviemst"
LISTA_OK = (200, None, "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1,RESOLUTION=1920x1080\nlow/index.m3u8\n")


def listas(*urls):
    """Las listas HLS que responden (el extractor comprueba cada candidato antes de elegirlo)."""
    return {u: LISTA_OK for u in urls}


def test_desempaquetar_recupera_el_javascript_original():
    js = "var links={\"hls4\":\"/stream/a/b/master.m3u8?t=1&s=2\"};var x='no';"

    assert desempaquetar(empaquetar(js)) == js


def test_desempaquetar_con_texto_que_no_esta_empaquetado_devuelve_none():
    assert desempaquetar("<html>nada que desempaquetar</html>") is None


@pytest.mark.parametrize("host,esperado", [
    ("vidhidevip.com", True), ("callistanise.com", True), ("flaswish.com", True), ("sfastwish.com", True),
    ("www.streamwish.to", True), ("example.com", False), ("evilvidhidevip.com", False),
])
def test_es_dominio_packed(host, esperado):
    assert es_dominio_packed(host) is esperado


def test_elige_la_calidad_mas_alta_y_completa_la_ruta_relativa_con_el_host_de_la_pagina():
    links = {"hls4": "/stream/abc/master.m3u8?t=1&s=2", "hls3": "https://cdn.example.net/hls3/master.m3u8", "hls2": "https://cdn.example.net/hls2/master.m3u8"}
    descargar = fake_descargar({EMBED: (200, None, pagina(links, "toookyoooreeevengersssseasonsss4-01")), **listas("https://vidhide.example.org/stream/abc/master.m3u8?t=1&s=2", "https://cdn.example.net/hls3/master.m3u8", "https://cdn.example.net/hls2/master.m3u8")})

    r = PackedHlsExtractor.extract_packed(EMBED, descargar=descargar)

    assert r["success"] is True
    assert r["direct_url"] == "https://vidhide.example.org/stream/abc/master.m3u8?t=1&s=2"
    assert r["directUrl"] == r["direct_url"]
    assert r["title"] == "toookyoooreeevengersssseasonsss4-01"


def test_sin_hls4_usa_el_siguiente_como_hace_el_reproductor():
    links = {"hls2": "https://cdn.example.net/hls2/master.m3u8", "hls3": "https://cdn.example.net/hls3/master.m3u8"}
    descargar = fake_descargar({EMBED: (200, None, pagina(links)), **listas(*links.values())})

    assert PackedHlsExtractor.extract_packed(EMBED, descargar=descargar)["direct_url"] == "https://cdn.example.net/hls3/master.m3u8"


def test_salta_los_enlaces_que_no_son_https_publicos():
    links = {"hls4": "http://cdn.example.net/hls4/master.m3u8", "hls3": "https://127.0.0.1/hls3/master.m3u8", "hls2": "https://cdn.example.net/hls2/master.m3u8"}
    descargar = fake_descargar({EMBED: (200, None, pagina(links)), **listas("https://cdn.example.net/hls2/master.m3u8")})

    assert PackedHlsExtractor.extract_packed(EMBED, descargar=descargar)["direct_url"] == "https://cdn.example.net/hls2/master.m3u8"


def test_sigue_la_redireccion_a_otro_dominio_del_mismo_servicio():
    # Streamwish reparte sus dominios: sfastwish.com redirige a flaswish.com
    links = {"hls2": "https://cdn.example.net/hls2/master.m3u8"}
    descargar = fake_descargar({
        "https://sfastwish.example/e/abc": (302, "https://flaswish.example/e/abc", ""),
        "https://flaswish.example/e/abc": (200, None, pagina(links)),
        **listas("https://cdn.example.net/hls2/master.m3u8"),
    })

    r = PackedHlsExtractor.extract_packed("https://sfastwish.example/e/abc", descargar=descargar)

    assert r["success"] is True
    assert descargar.pedidas[:2] == ["https://sfastwish.example/e/abc", "https://flaswish.example/e/abc"]


def test_prefiere_el_espejo_que_responde_sin_referer():
    # Streamwish publica dos espejos: hls3 (otro dominio) exige el Referer de la página y hls2 no. El daemon descarga HLS sin
    # cabeceras, así que hay que quedarse con el que responde solo.
    links = {"hls2": "https://cdn-a.example.net/hls2/master.m3u8", "hls3": "https://cdn-b.example.net/hls3/master.m3u8"}

    def descargar(url, referer=None):
        if url == EMBED:
            return 200, None, pagina(links)
        if "cdn-b" in url:
            return (200, None, LISTA_OK[2]) if referer else (404, None, "")
        return LISTA_OK

    r = PackedHlsExtractor.extract_packed(EMBED, descargar=descargar)

    assert r["direct_url"] == "https://cdn-a.example.net/hls2/master.m3u8"


def test_si_ninguna_lista_responde_se_queda_con_la_de_mayor_calidad():
    links = {"hls2": "https://cdn.example.net/hls2/master.m3u8", "hls4": "https://cdn.example.net/hls4/master.m3u8"}
    descargar = fake_descargar({EMBED: (200, None, pagina(links))})  # las listas dan 404: puede ser un fallo pasajero, no se descarta el video

    assert PackedHlsExtractor.extract_packed(EMBED, descargar=descargar)["direct_url"] == "https://cdn.example.net/hls4/master.m3u8"


def test_rechaza_un_embed_a_un_destino_no_publico():
    descargar = fake_descargar({})

    r = PackedHlsExtractor.extract_packed("https://127.0.0.1/e/abc", descargar=descargar)

    assert r["success"] is False
    assert descargar.pedidas == []


def test_pagina_sin_javascript_empaquetado_da_un_error_claro():
    descargar = fake_descargar({EMBED: (200, None, "<html><body>This video can't be played</body></html>")})

    r = PackedHlsExtractor.extract_packed(EMBED, descargar=descargar)

    assert r["success"] is False
    assert r["error"]


def test_empaquetado_sin_lista_de_enlaces_da_error():
    js = "var x=1;function f(){return 2;}"
    descargar = fake_descargar({EMBED: (200, None, f"<html><script>{empaquetar(js)}</script></html>")})

    assert PackedHlsExtractor.extract_packed(EMBED, descargar=descargar)["success"] is False


def test_un_fallo_de_red_devuelve_error_sin_lanzar():
    def descargar(url, referer=None):
        raise OSError("sin conexión")

    r = PackedHlsExtractor.extract_packed(EMBED, descargar=descargar)

    assert r["success"] is False
    assert "sin conexión" in r["error"]
