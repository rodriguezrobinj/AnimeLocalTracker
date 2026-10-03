import base64
import os
import sys

import pytest

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), '..')))

from resolvers import http_seguro
from resolvers.mediafire_extractor import MediafireExtractor, es_dominio_mediafire

IP_PUBLICA = "93.184.216.34"
ENLACE = "https://download2390.mediafire.com/abc123xyz/ga8o61lzee8ryz/Episodio_01.mp4"


@pytest.fixture(autouse=True)
def dns_publico(monkeypatch):
    """Ningún test resuelve DNS de verdad: todo host de ejemplo resuelve a una IP pública."""
    monkeypatch.setattr(http_seguro, "ips_de", lambda host: [IP_PUBLICA])


def pagina_con_boton(href: str, extra: str = "") -> str:
    # con el espacio en blanco del sitio real: atributos en líneas distintas
    return (f'<html><head><title>toookyoooreeevengersssseasonsss4-01</title></head><body>\n'
            f'<a class="input popsok"\n   aria-label="Download file"\n   href="{href}"\n   id="downloadButton" {extra} rel="nofollow">\n'
            f'   Download (418MB)\n</a></body></html>')


def fake_descargar(paginas: dict):
    pedidas = []

    def descargar(url, referer=None):
        pedidas.append(url)
        return paginas.get(url, (404, None, ""))

    descargar.pedidas = pedidas
    return descargar


def test_es_dominio_mediafire():
    assert es_dominio_mediafire("www.mediafire.com")
    assert es_dominio_mediafire("mediafire.com")
    assert es_dominio_mediafire("download2390.mediafire.com")
    assert not es_dominio_mediafire("notmediafire.com")
    assert not es_dominio_mediafire("mediafire.com.evil.com")


def test_devuelve_el_enlace_de_descarga_directa_del_boton():
    descargar = fake_descargar({"https://www.mediafire.com/file/4f7fyuv9kcrn0em/": (200, None, pagina_con_boton(ENLACE))})

    r = MediafireExtractor.extract_mediafire("https://www.mediafire.com/file/4f7fyuv9kcrn0em/", descargar=descargar)

    assert r["success"] is True
    assert r["direct_url"] == ENLACE
    assert r["directUrl"] == ENLACE
    assert r["title"] == "toookyoooreeevengersssseasonsss4-01"


def test_entiende_el_enlace_ofuscado_en_base64():
    cifrado = base64.b64encode(ENLACE.encode()).decode()
    pagina = pagina_con_boton("#", extra=f'data-scrambled-url="{cifrado}"')
    descargar = fake_descargar({"https://www.mediafire.com/file/x/": (200, None, pagina)})

    r = MediafireExtractor.extract_mediafire("https://www.mediafire.com/file/x/", descargar=descargar)

    assert r["success"] is True
    assert r["direct_url"] == ENLACE


def test_sigue_una_redireccion_de_la_pagina():
    descargar = fake_descargar({
        "https://mediafire.com/file/x": (302, "https://www.mediafire.com/file/x/file", ""),
        "https://www.mediafire.com/file/x/file": (200, None, pagina_con_boton(ENLACE)),
    })

    assert MediafireExtractor.extract_mediafire("https://mediafire.com/file/x", descargar=descargar)["success"] is True


@pytest.mark.parametrize("href", [
    "http://download2390.mediafire.com/x/video.mp4",   # sin https
    "https://127.0.0.1/video.mp4",
    "https://192.168.1.10/video.mp4",
    "https://intranet/video.mp4",
])
def test_rechaza_un_enlace_que_no_es_https_publico(href):
    descargar = fake_descargar({"https://www.mediafire.com/file/x/": (200, None, pagina_con_boton(href))})

    r = MediafireExtractor.extract_mediafire("https://www.mediafire.com/file/x/", descargar=descargar)

    assert r["success"] is False
    assert "mediafire" in r["error"].lower()


def test_rechaza_un_enlace_cuyo_dominio_resuelve_a_una_ip_privada(monkeypatch):
    monkeypatch.setattr(http_seguro, "ips_de", lambda host: ["10.0.0.5"] if host.startswith("download") else [IP_PUBLICA])
    descargar = fake_descargar({"https://www.mediafire.com/file/x/": (200, None, pagina_con_boton(ENLACE))})

    assert MediafireExtractor.extract_mediafire("https://www.mediafire.com/file/x/", descargar=descargar)["success"] is False


def test_archivo_borrado_o_sin_boton_da_un_error_claro():
    descargar = fake_descargar({"https://www.mediafire.com/file/x/": (200, None, "<html><title>Dangerous File Blocked</title></html>")})

    r = MediafireExtractor.extract_mediafire("https://www.mediafire.com/file/x/", descargar=descargar)

    assert r["success"] is False
    assert "mediafire" in r["error"].lower()


def test_un_fallo_de_red_devuelve_error_sin_lanzar():
    def descargar(url, referer=None):
        raise OSError("sin conexión")

    r = MediafireExtractor.extract_mediafire("https://www.mediafire.com/file/x/", descargar=descargar)

    assert r["success"] is False
    assert "sin conexión" in r["error"]
