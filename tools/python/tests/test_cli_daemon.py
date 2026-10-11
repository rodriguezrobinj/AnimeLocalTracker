import json
import os
import subprocess
import sys

import pytest

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), '..')))

from media.episode_fingerprint import EpisodeFingerprint

CLI = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', 'cli.py'))

PLUGIN = "import time\ndef lento(valor, segundos):\n    time.sleep(segundos)\n    return valor\n"


@pytest.fixture
def daemon(tmp_path):
    """El daemon de verdad, hablando por sus tuberías como lo hace la app."""
    plugin = tmp_path / "plugin_lento.py"
    plugin.write_text(PLUGIN, encoding="utf-8")
    proceso = subprocess.Popen([sys.executable, CLI, "--daemon"], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                               encoding="utf-8", cwd=os.path.dirname(CLI))

    def enviar(mensaje):
        proceso.stdin.write(json.dumps(mensaje) + "\n")
        proceso.stdin.flush()

    def pedir(ident, valor, segundos):
        enviar({"id": ident, "command": "run-plugin",
                "payload": {"plugin_path": str(plugin), "func_name": "lento", "args": {"valor": valor, "segundos": segundos}}})

    def leer():
        return json.loads(proceso.stdout.readline())

    try:
        yield enviar, pedir, leer
    finally:
        proceso.stdin.close()
        proceso.wait(timeout=10)


def test_el_saludo_anuncia_el_protocolo_de_peticiones_a_la_vez(daemon):
    _, _, leer = daemon

    assert leer()["protocolVersion"] == 2


def test_una_peticion_lenta_no_hace_esperar_a_la_siguiente(daemon):
    _, pedir, leer = daemon
    leer()  # saludo

    pedir(1, "lenta", 1.5)
    pedir(2, "rapida", 0)

    assert leer() == {"success": True, "result": "rapida", "id": 2}
    assert leer() == {"success": True, "result": "lenta", "id": 1}


def test_una_peticion_cancelada_no_escribe_su_respuesta(daemon):
    enviar, pedir, leer = daemon
    leer()  # saludo

    pedir(1, "cancelada", 0.5)
    enviar({"cancel": 1})
    enviar({"cancel": 99})  # una que ya terminó o nunca existió: se ignora
    pedir(2, "siguiente", 1.5)  # termina después de que la cancelada hubiera respondido

    assert leer() == {"success": True, "result": "siguiente", "id": 2}


def test_buscar_duplicados_cancelado_no_abre_ningun_video():
    class Cancelada:
        @staticmethod
        def is_set():
            return True

    res = EpisodeFingerprint.find_duplicates(["C:\\no_existe_1.mkv", "C:\\no_existe_2.mkv"], cancelada=Cancelada())

    assert res == {"success": False, "error": "cancelado"}
