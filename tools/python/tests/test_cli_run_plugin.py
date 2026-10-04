import hashlib
import os
import sys

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), '..')))

from cli import process_command


def _plugin(tmp_path, codigo: str, nombre: str = "plugin_prueba.py") -> str:
    ruta = tmp_path / nombre
    ruta.write_text(codigo, encoding="utf-8")
    return str(ruta)


def _huella(ruta: str) -> str:
    with open(ruta, "rb") as f:
        return hashlib.sha256(f.read()).hexdigest().upper()  # la app la manda en mayúsculas


def test_run_plugin_con_la_huella_aprobada_ejecuta_la_funcion(tmp_path):
    ruta = _plugin(tmp_path, "def doble(valor):\n    return valor * 2\n")

    res = process_command("run-plugin", {"plugin_path": ruta, "func_name": "doble", "args": {"valor": 21}, "sha256": _huella(ruta)})

    assert res == {"success": True, "result": 42}


def test_run_plugin_si_el_archivo_cambio_tras_aprobarse_no_lo_ejecuta(tmp_path):
    ruta = _plugin(tmp_path, "def doble(valor):\n    return valor * 2\n")
    aprobada = _huella(ruta)
    testigo = tmp_path / "ejecutado.txt"
    with open(ruta, "w", encoding="utf-8") as f:
        f.write(f"open({str(testigo)!r}, 'w').close()\ndef doble(valor):\n    return 0\n")

    res = process_command("run-plugin", {"plugin_path": ruta, "func_name": "doble", "args": {"valor": 21}, "sha256": aprobada})

    assert res["success"] is False
    assert not testigo.exists(), "el código cambiado no debe llegar a ejecutarse"


def test_run_plugin_no_deja_la_carpeta_del_plugin_en_sys_path(tmp_path):
    ruta = _plugin(tmp_path, "def eco(valor):\n    return valor\n")

    res = process_command("run-plugin", {"plugin_path": ruta, "func_name": "eco", "args": {"valor": 1}, "sha256": _huella(ruta)})

    assert res["success"] is True
    assert str(tmp_path) not in sys.path, "un archivo dejado junto al plugin no debe poder suplantar un import del daemon"


def test_run_plugin_sin_huella_sigue_funcionando_para_el_plugin_propio_de_la_app(tmp_path):
    ruta = _plugin(tmp_path, "import os\ndef donde():\n    return os.path.basename(__file__)\n")

    res = process_command("run-plugin", {"plugin_path": ruta, "func_name": "donde", "args": {}})

    assert res == {"success": True, "result": "plugin_prueba.py"}
