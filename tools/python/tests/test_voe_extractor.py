import base64
import codecs
import json
import os
import sys

import pytest

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), '..')))

from resolvers import http_seguro
from resolvers.voe_extractor import VoeExtractor, decodificar_payload, es_dominio_voe

MARCADORES = ("@$", "^^", "~@", "%?", "*~", "!!", "#&")
IP_PUBLICA = "93.184.216.34"


def codificar(datos: dict) -> str:
    """Inverso de decodificar_payload: así Voe ofusca el JSON del reproductor (fixture sintético, sin red)."""
    paso1 = base64.b64encode(json.dumps(datos).encode()).decode()
    paso2 = "".join(chr(ord(c) + 3) for c in paso1[::-1])
    paso3 = base64.b64encode(paso2.encode("latin-1")).decode()
    con_marcadores = "".join(c + MARCADORES[i % len(MARCADORES)] if i % 6 == 5 else c for i, c in enumerate(paso3))
    return codecs.encode(con_marcadores, "rot13")


def pagina_con_payload(datos: dict) -> str:
    return f'<html><head><title>Watch</title></head><body><script type="application/json">["{codificar(datos)}"]</script></body></html>'


def pagina_con_salto(destino: str) -> str:
    return (f"<html><head><title>Redirecting...</title></head><body><script>"
            f"if (window.location.href.indexOf('x') === -1) {{ window.location.href = '{destino}'; }}"
            f"</script></body></html>")


@pytest.fixture(autouse=True)
def dns_publico(monkeypatch):
    """Ningún test resuelve DNS de verdad: todo host de ejemplo resuelve a una IP pública."""
    monkeypatch.setattr(http_seguro, "ips_de", lambda host: [IP_PUBLICA])


def fake_descargar(paginas: dict):
    """Descargador falso: url -> (estado, ubicación de redirección, texto). Anota qué URLs se pidieron."""
    pedidas = []

    def descargar(url, referer=None):
        pedidas.append(url)
        return paginas.get(url, (404, None, ""))

    descargar.pedidas = pedidas
    return descargar


DATOS = {
    "source": "https://cdn.example.net/hls/master.m3u8",
    "direct_access_url": "https://cdn.example.net/video.mp4",
    "title": "4446_1_SUB.mp4",
    "file_code": "abc123",
}


def test_decodificar_payload_ida_y_vuelta():
    assert decodificar_payload(codificar(DATOS)) == DATOS


def test_decodificar_payload_con_basura_devuelve_none():
    assert decodificar_payload("esto no es un payload de voe") is None
    assert decodificar_payload("") is None


def test_es_dominio_voe():
    assert es_dominio_voe("voe.sx")
    assert es_dominio_voe("www.voe.sx")
    assert not es_dominio_voe("notvoe.sx")
    assert not es_dominio_voe("voe.sx.evil.com")


def test_sigue_el_salto_por_javascript_y_devuelve_el_mp4_directo():
    descargar = fake_descargar({
        "https://voe.sx/e/abc123": (200, None, pagina_con_salto("https://rotativo.example.org/e/abc123")),
        "https://rotativo.example.org/e/abc123": (200, None, pagina_con_payload(DATOS)),
    })

    r = VoeExtractor.extract_voe("https://voe.sx/e/abc123", descargar=descargar)

    assert r["success"] is True
    assert r["direct_url"] == "https://cdn.example.net/video.mp4"
    assert r["directUrl"] == r["direct_url"]
    assert r["title"] == "4446_1_SUB.mp4"
    assert descargar.pedidas == ["https://voe.sx/e/abc123", "https://rotativo.example.org/e/abc123"]


def test_sigue_una_redireccion_http_3xx():
    descargar = fake_descargar({
        "https://voe.sx/e/abc123": (302, "https://rotativo.example.org/e/abc123", ""),
        "https://rotativo.example.org/e/abc123": (200, None, pagina_con_payload(DATOS)),
    })

    assert VoeExtractor.extract_voe("https://voe.sx/e/abc123", descargar=descargar)["success"] is True


def test_sin_mp4_directo_usa_el_hls():
    datos = {k: v for k, v in DATOS.items() if k != "direct_access_url"}
    descargar = fake_descargar({"https://voe.sx/e/abc123": (200, None, pagina_con_payload(datos))})

    r = VoeExtractor.extract_voe("https://voe.sx/e/abc123", descargar=descargar)

    assert r["success"] is True
    assert r["direct_url"] == "https://cdn.example.net/hls/master.m3u8"


def test_ignora_un_enlace_que_no_es_https():
    datos = dict(DATOS, direct_access_url="http://cdn.example.net/video.mp4")
    descargar = fake_descargar({"https://voe.sx/e/abc123": (200, None, pagina_con_payload(datos))})

    r = VoeExtractor.extract_voe("https://voe.sx/e/abc123", descargar=descargar)

    assert r["direct_url"] == "https://cdn.example.net/hls/master.m3u8"


@pytest.mark.parametrize("destino", [
    "https://127.0.0.1/e/abc123",
    "https://192.168.1.10/e/abc123",
    "https://localhost/e/abc123",
    "https://nas/e/abc123",
    "https://servidor.local/e/abc123",
    "http://rotativo.example.org/e/abc123",
    "file:///etc/passwd",
])
def test_rechaza_un_salto_a_destino_no_publico_o_no_https(destino):
    descargar = fake_descargar({"https://voe.sx/e/abc123": (200, None, pagina_con_salto(destino))})

    r = VoeExtractor.extract_voe("https://voe.sx/e/abc123", descargar=descargar)

    assert r["success"] is False
    assert descargar.pedidas == ["https://voe.sx/e/abc123"], "nunca debe pedirse el destino no permitido"


def test_rechaza_un_dominio_que_resuelve_a_una_ip_privada(monkeypatch):
    monkeypatch.setattr(http_seguro, "ips_de", lambda host: ["10.0.0.5"])
    descargar = fake_descargar({})

    r = VoeExtractor.extract_voe("https://voe.sx/e/abc123", descargar=descargar)

    assert r["success"] is False
    assert descargar.pedidas == []


def test_pagina_sin_payload_ni_salto_da_un_error_claro():
    descargar = fake_descargar({"https://voe.sx/e/abc123": (200, None, "<html><body>Video not found</body></html>")})

    r = VoeExtractor.extract_voe("https://voe.sx/e/abc123", descargar=descargar)

    assert r["success"] is False
    assert "voe" in r["error"].lower()


def test_payload_con_formato_desconocido_da_error_y_no_lanza():
    pagina = '<html><script type="application/json">["cadena que ya no se decodifica"]</script></html>'
    descargar = fake_descargar({"https://voe.sx/e/abc123": (200, None, pagina)})

    r = VoeExtractor.extract_voe("https://voe.sx/e/abc123", descargar=descargar)

    assert r["success"] is False
    assert r["error"]


def test_limita_los_saltos_para_no_dar_vueltas_sin_fin():
    descargar = fake_descargar({
        "https://voe.sx/e/abc123": (200, None, pagina_con_salto("https://a.example.org/e/abc123")),
        "https://a.example.org/e/abc123": (200, None, pagina_con_salto("https://b.example.org/e/abc123")),
        "https://b.example.org/e/abc123": (200, None, pagina_con_salto("https://a.example.org/e/abc123")),
    })

    r = VoeExtractor.extract_voe("https://voe.sx/e/abc123", descargar=descargar)

    assert r["success"] is False
    assert len(descargar.pedidas) <= 5


def test_un_fallo_de_red_devuelve_error_sin_lanzar():
    def descargar(url, referer=None):
        raise OSError("sin conexión")

    r = VoeExtractor.extract_voe("https://voe.sx/e/abc123", descargar=descargar)

    assert r["success"] is False
    assert "sin conexión" in r["error"]
