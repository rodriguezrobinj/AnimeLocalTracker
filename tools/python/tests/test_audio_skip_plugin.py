"""Ubicación de openings/endings con el audio de AnimeThemes (detect_themes).

Se prueba sobre huellas sintéticas (sin ffmpeg): el tema se "incrusta" en la huella del episodio y se comprueba dónde lo
encuentra, qué grupo de temas se llega a decodificar y cómo se guarda la huella en disco.
"""
import os
import shutil
import sys

import numpy as np
import pytest

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), '..')))

import audio_skip_plugin as plugin

FPS = plugin._FPS
COLUMNAS = len(plugin._BANDAS_HZ)


def _ruido(segundos, semilla):
    return np.random.default_rng(semilla).normal(size=(int(segundos * FPS), COLUMNAS)).astype(np.float32)


def _incrustar(episodio, tema, segundo, desde=0):
    ep = episodio.copy()
    parte = tema[desde:]
    i = int(segundo * FPS)
    ep[i:i + len(parte)] = parte + 0.1 * np.random.default_rng(99).normal(size=parte.shape)
    return ep


@pytest.fixture
def entorno(monkeypatch, tmp_path):
    """Episodio de 1400 s y temas sintéticos; devuelve (crear_referencia, decodificados)."""
    temas = {}
    decodificados = []

    def crear_referencia(nombre, huella):
        ruta = tmp_path / nombre
        ruta.write_bytes(b"x")
        temas[str(ruta)] = huella
        return str(ruta)

    def huella_referencia(ruta, cache_dir=None):
        decodificados.append(os.path.basename(ruta))
        return temas[ruta]

    monkeypatch.setattr(plugin, "_huella_referencia", huella_referencia)
    monkeypatch.setattr(plugin, "_get_duration_seconds", lambda _: 1400.0)
    episodio = tmp_path / "Episodio 01.mkv"
    episodio.write_bytes(b"x")
    return crear_referencia, decodificados, str(episodio)


def _con_episodio(monkeypatch, huella_completa):
    """El episodio "suena" como huella_completa (1400 s): inicio = primeros 480 s, final = últimos 360 s."""
    def ventanas(_ruta, duracion, seg_inicio, seg_final):
        comienzo = duracion - seg_final
        return (plugin._Ventana(0.0, huella_completa[:int(seg_inicio * FPS)]),
                plugin._Ventana(comienzo, huella_completa[int(comienzo * FPS):]))
    monkeypatch.setattr(plugin, "_ventanas_episodio", ventanas)


def test_ubica_opening_y_ending_con_sus_temas(monkeypatch, entorno):
    crear, _, episodio = entorno
    op, ed = _ruido(90, 1), _ruido(90, 2)
    huella = _incrustar(_incrustar(_ruido(1400, 3), op, 120), ed, 1300)
    _con_episodio(monkeypatch, huella)

    r = plugin.detect_themes(episodio, [
        {"path": crear("OP1.mp3", op), "kind": "OP", "priority": 0},
        {"path": crear("ED1.mp3", ed), "kind": "ED", "priority": 0},
    ])

    assert r["success"]
    por_tramo = {m["segment"]: m for m in r["matches"]}
    assert por_tramo["op"]["start"] == pytest.approx(120, abs=0.2)
    assert por_tramo["op"]["end"] == pytest.approx(210, abs=0.2)
    assert por_tramo["ed"]["start"] == pytest.approx(1300, abs=0.2)
    assert por_tramo["op"]["confidence"] > 0.9
    assert os.path.basename(por_tramo["ed"]["reference_path"]) == "ED1.mp3"


def test_un_tema_que_no_suena_en_el_episodio_no_se_acepta(monkeypatch, entorno):
    crear, _, episodio = entorno
    _con_episodio(monkeypatch, _ruido(1400, 3))

    r = plugin.detect_themes(episodio, [{"path": crear("OP9.mp3", _ruido(90, 7)), "kind": "OP", "priority": 0}])

    assert r["success"] and r["matches"] == []


def test_los_temas_que_no_aplican_ni_se_decodifican_si_acierta_uno_que_aplica(monkeypatch, entorno):
    crear, decodificados, episodio = entorno
    op, ed = _ruido(90, 1), _ruido(90, 2)
    _con_episodio(monkeypatch, _incrustar(_incrustar(_ruido(1400, 3), op, 60), ed, 1300))

    plugin.detect_themes(episodio, [
        {"path": crear("OP2.mp3", op), "kind": "OP", "priority": 0},
        {"path": crear("ED1.mp3", ed), "kind": "ED", "priority": 0},
        {"path": crear("OP1.mp3", _ruido(90, 5)), "kind": "OP", "priority": 1},
        {"path": crear("OP3.mp3", _ruido(90, 6)), "kind": "OP", "priority": 1},
    ])

    assert "OP1.mp3" not in decodificados and "OP3.mp3" not in decodificados


def test_si_ninguno_que_aplica_acierta_prueba_los_demas(monkeypatch, entorno):
    crear, _, episodio = entorno
    op = _ruido(90, 1)
    _con_episodio(monkeypatch, _incrustar(_ruido(1400, 3), op, 60))

    r = plugin.detect_themes(episodio, [
        {"path": crear("OP2.mp3", _ruido(90, 5)), "kind": "OP", "priority": 0},
        {"path": crear("OP1.mp3", op), "kind": "OP", "priority": 1},
    ])

    assert os.path.basename(r["matches"][0]["reference_path"]) == "OP1.mp3"


def test_un_opening_que_suena_al_final_es_el_ending_del_episodio(monkeypatch, entorno):
    # Episodio 1 / final de temporada: no hay ending propio y el opening cierra el episodio.
    crear, _, episodio = entorno
    op = _ruido(90, 1)
    _con_episodio(monkeypatch, _incrustar(_ruido(1400, 3), op, 1310))

    r = plugin.detect_themes(episodio, [
        {"path": crear("OP1.mp3", op), "kind": "OP", "priority": 0},
        {"path": crear("ED1.mp3", _ruido(90, 2)), "kind": "ED", "priority": 0},
    ])

    assert [m["segment"] for m in r["matches"]] == ["ed"]
    assert r["matches"][0]["start"] == pytest.approx(1310, abs=0.2)


def test_en_un_episodio_corto_el_ending_no_puede_ser_el_mismo_opening(monkeypatch, entorno):
    crear, _, episodio = entorno
    monkeypatch.setattr(plugin, "_get_duration_seconds", lambda _: 256.0)
    op = _ruido(60, 1)
    huella = _incrustar(_ruido(256, 3), op, 24)
    # Episodio de 4 minutos: las dos ventanas cubren el episodio entero.
    monkeypatch.setattr(plugin, "_ventanas_episodio",
                        lambda *_: (plugin._Ventana(0.0, huella), plugin._Ventana(0.0, huella)))

    r = plugin.detect_themes(episodio, [{"path": crear("OP1.mp3", op), "kind": "OP", "priority": 0}])

    assert [m["segment"] for m in r["matches"]] == ["op"]


def test_un_ending_recortado_se_ubica_por_trozos(monkeypatch, entorno):
    # El episodio solo usa los últimos 60 s del tema (los primeros 30 s suenan sobre otra escena distinta).
    crear, _, episodio = entorno
    ed = _ruido(90, 2)
    _con_episodio(monkeypatch, _incrustar(_ruido(1400, 3), ed, 1330, desde=30 * FPS))

    r = plugin.detect_themes(episodio, [{"path": crear("ED1.mp3", ed), "kind": "ED", "priority": 0}])

    m = r["matches"][0]
    assert m["segment"] == "ed" and m["mode"] == "partial"
    assert m["start"] == pytest.approx(1330, abs=5)
    assert m["end"] == pytest.approx(1390, abs=5)
    assert m["confidence"] >= 0.7


def test_la_curva_coincide_con_pearson_calculado_a_mano():
    rng = np.random.default_rng(4)
    ventana = plugin._Ventana(0.0, rng.normal(size=(300, COLUMNAS)).astype(np.float32))
    segmento = rng.normal(size=(40, COLUMNAS)).astype(np.float32)

    curva = ventana.curva(segmento)

    esperado = []
    for i in range(300 - 40 + 1):
        trozo = ventana.h[i:i + 40]
        por_columna = [np.corrcoef(trozo[:, j], segmento[:, j])[0, 1] for j in range(COLUMNAS)]
        esperado.append(float(np.dot(por_columna, plugin._PESOS)))
    np.testing.assert_allclose(curva, esperado, atol=1e-6)


def test_rechaza_urls_y_rutas_raras():
    assert not plugin._es_ruta_local("https://animethemes.moe/a.ogg")
    assert not plugin._es_ruta_local("-i")
    assert not plugin._es_ruta_local(None)
    r = plugin.detect_themes("file://c:/x.mkv", [])
    assert not r["success"]


def test_la_huella_del_tema_se_guarda_y_no_depende_del_nombre(monkeypatch, tmp_path):
    llamadas = []
    rng = np.random.default_rng(8)
    pcm = (rng.normal(size=8000 * 20) * 0.3).astype(np.float32)
    monkeypatch.setattr(plugin, "_pcm", lambda ruta, *a, **k: llamadas.append(ruta) or pcm)
    cache = tmp_path / "huellas"
    original = tmp_path / "OP_OP1_v1_ep1-.mp3"
    original.write_bytes(os.urandom(200_000))
    renombrado = tmp_path / "OP1 - Canción.mp3"
    shutil.copy(original, renombrado)

    primera = plugin._huella_referencia(str(original), str(cache))
    segunda = plugin._huella_referencia(str(renombrado), str(cache))

    assert len(llamadas) == 1, "la segunda vez (aunque el archivo se haya renombrado) sale del disco"
    np.testing.assert_array_equal(primera, segunda)
    assert len(list(cache.glob("*.npy"))) == 1


def test_recorta_el_silencio_de_los_extremos_del_tema():
    huella = np.zeros((100, COLUMNAS), dtype=np.float32)
    huella[20:70, 0] = 0.5

    recortada = plugin._recortar_silencio(huella)

    assert len(recortada) == 50
