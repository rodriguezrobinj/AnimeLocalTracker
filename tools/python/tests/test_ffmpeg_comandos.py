"""Comandos de ffmpeg/ffprobe del motor Python.

Regresión de un error que pasaba desapercibido porque los llamadores tragaban el fallo:
  * ffprobe no admite ``-nostdin`` -> inspect-episode fallaba SIEMPRE (sin resolución/códec/fps/10-bit).

Y cobertura de la huella perceptual (dHash), que extrae el fotograma con ffmpeg en lugar de OpenCV.
"""
import os
import shutil
import subprocess
import sys
from types import SimpleNamespace

import pytest

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), '..')))

from media import episode_fingerprint, episode_metadata
from media.episode_fingerprint import EpisodeFingerprint
from media.episode_metadata import EpisodeMetadata
from media.ffmpeg_guard import argumentos_ffprobe


@pytest.fixture
def video_falso(tmp_path):
    """Archivo con extensión de video (es_ruta_media_segura solo mira que exista y la extensión)."""
    ruta = tmp_path / "episodio.mp4"
    ruta.write_bytes(b"\x00" * 16)
    return str(ruta)


def _captura(monkeypatch, modulo, stdout="", stderr="", codigo=0):
    """Sustituye subprocess.run del módulo y devuelve la lista donde se guardan los comandos ejecutados."""
    comandos = []

    def falso(cmd, *args, **kwargs):
        comandos.append(list(cmd))
        return SimpleNamespace(returncode=codigo, stdout=stdout, stderr=stderr)

    monkeypatch.setattr(modulo.subprocess, "run", falso)
    return comandos


# ───────────────────────── Unitarias (no necesitan ffmpeg) ─────────────────────────

def test_argumentos_ffprobe_no_incluyen_nostdin():
    args = argumentos_ffprobe()
    assert "-nostdin" not in args, "ffprobe no admite -nostdin (solo ffmpeg)"
    assert "-max_alloc" in args and "-v" in args


def test_inspect_episode_no_pasa_nostdin_a_ffprobe(monkeypatch, video_falso):
    comandos = _captura(monkeypatch, episode_metadata, stdout='{"streams": [], "format": {"duration": "10"}}')

    resultado = EpisodeMetadata.inspect_episode(video_falso)

    assert resultado["success"] is True
    assert comandos[0][0] == "ffprobe"
    assert "-nostdin" not in comandos[0]
    assert "-max_alloc" in comandos[0]


# ───────────────── Integración con ffmpeg/ffprobe reales (se omiten si no están en el PATH) ─────────────────

ffmpeg_real = shutil.which("ffmpeg")
ffprobe_real = shutil.which("ffprobe")
requiere_ffmpeg = pytest.mark.skipif(not (ffmpeg_real and ffprobe_real), reason="ffmpeg/ffprobe no están en el PATH")


@pytest.fixture(scope="module")
def video_real(tmp_path_factory):
    """Video sintético de 6 s con un corte de escena fuerte a los 3 s (negro -> blanco).

    scdet mide cambio de LUMINOSIDAD: un corte rojo -> azul apenas puntúa (~16 de 100) y no supera el umbral.
    """
    ruta = str(tmp_path_factory.mktemp("videos") / "sintetico.mp4")
    subprocess.run(
        [ffmpeg_real, "-y", "-loglevel", "error",
         "-f", "lavfi", "-i", "color=c=black:s=64x64:r=10:d=3",
         "-f", "lavfi", "-i", "color=c=white:s=64x64:r=10:d=3",
         "-filter_complex", "[0:v][1:v]concat=n=2:v=1:a=0", "-c:v", "mpeg4", ruta],
        check=True, timeout=60)
    return ruta


@requiere_ffmpeg
def test_integracion_inspect_episode_devuelve_metadatos(video_real):
    resultado = EpisodeMetadata.inspect_episode(video_real)

    assert resultado["success"] is True, resultado
    assert (resultado["ancho"], resultado["alto"]) == (64, 64)
    assert resultado["duracion_segundos"] == pytest.approx(6.0, abs=0.5)
    assert resultado["codec_video"] == "mpeg4"


# ───────────────────────── Huella perceptual (dHash con ffmpeg) ─────────────────────────

def test_dhash_de_una_imagen_plana_es_cero():
    assert EpisodeFingerprint.calcular_dhash(bytes([128] * 72)) == "0" * 16


def test_dhash_marca_las_columnas_que_aumentan():
    # Cada fila 0,255,0,255...: sube en las columnas 0->1, 2->3, 4->5, 6->7 (bits 1010...) y baja en el resto
    fila = bytes([0, 255] * 4 + [0])
    assert EpisodeFingerprint.calcular_dhash(fila * 8) == "aa" * 8


def test_fingerprint_rechaza_urls_y_rutas_inexistentes(monkeypatch):
    llamadas = _captura(monkeypatch, episode_fingerprint)

    assert EpisodeFingerprint.compute_fingerprint("http://ejemplo.com/video.mp4")["success"] is False
    assert EpisodeFingerprint.compute_fingerprint("C:/no/existe.mkv")["success"] is False
    assert llamadas == []  # ni siquiera se invoca ffmpeg


def test_fingerprint_arma_el_comando_con_las_protecciones(monkeypatch, video_falso):
    comandos = _captura(monkeypatch, episode_fingerprint, stdout=bytes([128] * 72))

    resultado = EpisodeFingerprint.compute_fingerprint(video_falso, 45.0)

    assert resultado["success"] is True
    cmd = comandos[0]
    assert cmd[0] == "ffmpeg"
    assert "-nostdin" in cmd and "-max_alloc" in cmd
    assert cmd[cmd.index("-ss") + 1] == "45.000"
    assert cmd[cmd.index("-i") + 1] == video_falso


def test_fingerprint_informa_el_fallo_de_ffmpeg(monkeypatch, video_falso):
    _captura(monkeypatch, episode_fingerprint, stderr=b"Invalid data found when processing input\n", codigo=1)

    resultado = EpisodeFingerprint.compute_fingerprint(video_falso)

    assert resultado["success"] is False
    assert "Invalid data" in resultado["error"]


def test_fingerprint_sin_fotograma_no_es_exito(monkeypatch, video_falso):
    # -ss más allá del final: ffmpeg sale con 0 pero sin datos
    _captura(monkeypatch, episode_fingerprint, stdout=b"")

    assert EpisodeFingerprint.compute_fingerprint(video_falso, 9999)["success"] is False


def _video_testsrc(ffmpeg, ruta, filtro_extra=None):
    cmd = [ffmpeg, "-y", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=s=160x90:r=10:d=3"]
    if filtro_extra:
        cmd += ["-vf", filtro_extra]
    subprocess.run(cmd + ["-c:v", "mpeg4", ruta], check=True, timeout=60)
    return ruta


@requiere_ffmpeg
def test_integracion_find_duplicates_agrupa_copias_y_separa_distintos(tmp_path):
    original = _video_testsrc(ffmpeg_real, str(tmp_path / "a.mp4"))
    copia = _video_testsrc(ffmpeg_real, str(tmp_path / "b.mp4"))
    distinto = _video_testsrc(ffmpeg_real, str(tmp_path / "c.mp4"), "hflip")

    resultado = EpisodeFingerprint.find_duplicates([original, copia, distinto], max_distance=8, timestamp=1.0)

    assert resultado["success"] is True
    assert resultado["total_analizados"] == 3
    assert [sorted(g["items"]) for g in resultado["duplicados"]] == [sorted([original, copia])]
    assert resultado["unicos"] == 1
