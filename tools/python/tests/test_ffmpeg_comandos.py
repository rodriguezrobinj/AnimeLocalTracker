"""Comandos de ffmpeg/ffprobe del motor Python.

Regresión de dos errores que pasaban desapercibidos porque los llamadores tragaban el fallo:
  * ffprobe no admite ``-nostdin`` -> inspect-episode fallaba SIEMPRE (sin resolución/códec/fps/10-bit).
  * ``scdet=s=0.30:sc=1`` es un filtro inválido -> el plan B de detección de escenas nunca funcionaba
    y respondía "éxito" con cero escenas.
"""
import os
import shutil
import subprocess
import sys
from types import SimpleNamespace

import pytest

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), '..')))

from media import episode_metadata, scene_detector
from media.episode_metadata import EpisodeMetadata
from media.ffmpeg_guard import SCDET_UMBRAL, argumentos_ffprobe, filtro_scdet
from media.scene_detector import SceneDetector


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


def test_filtro_scdet_usa_umbral_y_paso_de_escenas_validos():
    filtro = filtro_scdet()
    assert filtro == f"scdet=t={SCDET_UMBRAL}:s=1,metadata=print:file=-"
    # s es un booleano: un valor decimal (como el antiguo s=0.30) hace que ffmpeg rechace el filtro
    assert "s=0." not in filtro
    assert "sc=" not in filtro  # esa opción no existe en scdet
    assert 0 < SCDET_UMBRAL <= 100


def test_inspect_episode_no_pasa_nostdin_a_ffprobe(monkeypatch, video_falso):
    comandos = _captura(monkeypatch, episode_metadata, stdout='{"streams": [], "format": {"duration": "10"}}')

    resultado = EpisodeMetadata.inspect_episode(video_falso)

    assert resultado["success"] is True
    assert comandos[0][0] == "ffprobe"
    assert "-nostdin" not in comandos[0]
    assert "-max_alloc" in comandos[0]


def test_probe_ffmpeg_no_pasa_nostdin_a_ffprobe(monkeypatch, video_falso):
    monkeypatch.setattr(scene_detector.shutil, "which", lambda nombre: nombre)
    comandos = _captura(monkeypatch, scene_detector, stdout='{"format": {"duration": "1425.06"}}')

    duracion = SceneDetector._probe_ffmpeg(video_falso)

    assert duracion == pytest.approx(1425.06)
    assert "-nostdin" not in comandos[0]


def test_detect_with_ffmpeg_arma_el_comando_con_el_filtro_valido(monkeypatch, video_falso):
    monkeypatch.setattr(scene_detector.shutil, "which", lambda nombre: nombre)
    monkeypatch.setattr(SceneDetector, "_probe_ffmpeg", staticmethod(lambda ruta: 1400.0))
    comandos = _captura(monkeypatch, scene_detector, stdout="frame:0 pts:1 pts_time:20.0\n")

    resultado = SceneDetector._detect_with_ffmpeg(video_falso, 300)

    assert resultado["success"] is True
    cmd = comandos[0]
    assert cmd[cmd.index("-vf") + 1] == filtro_scdet()
    assert "-nostdin" in cmd  # en ffmpeg SÍ es válido


def test_detect_with_ffmpeg_informa_el_fallo_de_ffmpeg(monkeypatch, video_falso):
    # Antes no se comprobaba el código de salida: un ffmpeg que rechazaba el comando daba "éxito" sin escenas.
    monkeypatch.setattr(scene_detector.shutil, "which", lambda nombre: nombre)
    monkeypatch.setattr(SceneDetector, "_probe_ffmpeg", staticmethod(lambda ruta: 1400.0))
    _captura(monkeypatch, scene_detector, stderr="Error applying option 's' to filter 'scdet'\n", codigo=234)

    resultado = SceneDetector._detect_with_ffmpeg(video_falso, 300)

    assert resultado["success"] is False
    assert "scdet" in resultado["error"]


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


@requiere_ffmpeg
def test_integracion_probe_ffmpeg_devuelve_la_duracion(video_real):
    # Antes devolvía None siempre (el -nostdin hacía fallar ffprobe y la excepción se tragaba).
    assert SceneDetector._probe_ffmpeg(video_real) == pytest.approx(6.0, abs=0.5)


@requiere_ffmpeg
def test_integracion_scdet_detecta_el_corte_de_escena(video_real):
    proc = subprocess.run(
        [ffmpeg_real, "-hide_banner", "-nostats", "-nostdin", "-i", video_real,
         "-vf", filtro_scdet(), "-f", "null", "-"],
        capture_output=True, text=True, timeout=60)

    assert proc.returncode == 0, proc.stderr
    cortes = SceneDetector._parse_scdet(proc.stdout + proc.stderr)
    assert any(2.5 <= t <= 3.5 for t in cortes), f"se esperaba un corte cerca de 3 s, salió {cortes}"


@requiere_ffmpeg
def test_integracion_detect_with_ffmpeg_ya_no_falla_por_el_filtro(video_real):
    resultado = SceneDetector._detect_with_ffmpeg(video_real, 60)

    assert resultado["success"] is True, resultado
    assert resultado["source"] == "scdet_local"
