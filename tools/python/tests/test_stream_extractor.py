import sys
import os
import pytest
from unittest.mock import patch

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), '..')))

from resolvers.stream_extractor import StreamExtractor


def test_extract_stream_info_url_byse_se_enruta_al_extractor_de_navegador():
    with patch(
        "resolvers.stream_extractor.BrowserStreamExtractor.extract_byse",
        return_value={"success": True, "direct_url": "https://cdn.example.com/master.m3u8"},
    ) as mock_extract:
        resultado = StreamExtractor.extract_stream_info("https://byselapuix.com/e/xyz")

    mock_extract.assert_called_once_with("https://byselapuix.com/e/xyz", None)
    assert resultado["success"] is True
    assert resultado["direct_url"] == "https://cdn.example.com/master.m3u8"


def test_extract_stream_info_url_voe_se_enruta_al_extractor_de_voe():
    with patch(
        "resolvers.stream_extractor.VoeExtractor.extract_voe",
        return_value={"success": True, "direct_url": "https://cdn.example.com/video.mp4"},
    ) as mock_extract, patch("resolvers.stream_extractor.yt_dlp.YoutubeDL") as mock_ydl_cls:
        resultado = StreamExtractor.extract_stream_info("https://voe.sx/e/xyz")

    mock_extract.assert_called_once_with("https://voe.sx/e/xyz", None)
    mock_ydl_cls.assert_not_called()
    assert resultado["direct_url"] == "https://cdn.example.com/video.mp4"


def test_extract_stream_info_url_mediafire_se_enruta_al_extractor_de_mediafire():
    with patch(
        "resolvers.stream_extractor.MediafireExtractor.extract_mediafire",
        return_value={"success": True, "direct_url": "https://download1.mediafire.com/x/video.mp4"},
    ) as mock_extract, patch("resolvers.stream_extractor.yt_dlp.YoutubeDL") as mock_ydl_cls:
        resultado = StreamExtractor.extract_stream_info("https://www.mediafire.com/file/abc/")

    mock_extract.assert_called_once_with("https://www.mediafire.com/file/abc/", None)
    mock_ydl_cls.assert_not_called()
    assert resultado["success"] is True


@pytest.mark.parametrize("servidor,url", [
    ("Vidhide", "https://dominio-que-rota.example/e/abc"),   # el nombre que da el sitio manda aunque el dominio sea nuevo
    ("Streamwish", "https://otro-dominio-nuevo.example/e/abc"),
    ("streamwish", "https://otro-dominio-nuevo.example/e/abc"),
    (None, "https://callistanise.com/e/abc"),                # sin nombre, por dominios conocidos
    (None, "https://flaswish.com/e/abc"),
])
def test_extract_stream_info_vidhide_y_streamwish_se_enrutan_al_extractor_hls(servidor, url):
    with patch(
        "resolvers.stream_extractor.PackedHlsExtractor.extract_packed",
        return_value={"success": True, "direct_url": "https://cdn.example.com/master.m3u8"},
    ) as mock_extract, patch("resolvers.stream_extractor.yt_dlp.YoutubeDL") as mock_ydl_cls:
        resultado = StreamExtractor.extract_stream_info(url, None, servidor)

    mock_extract.assert_called_once_with(url, None)
    mock_ydl_cls.assert_not_called()
    assert resultado["direct_url"] == "https://cdn.example.com/master.m3u8"


def test_extract_stream_info_otra_url_no_toca_los_extractores_propios():
    with patch("resolvers.stream_extractor.BrowserStreamExtractor.extract_byse") as mock_extract, \
         patch("resolvers.stream_extractor.VoeExtractor.extract_voe") as mock_voe, \
         patch("resolvers.stream_extractor.yt_dlp.YoutubeDL") as mock_ydl_cls:
        mock_ydl = mock_ydl_cls.return_value.__enter__.return_value
        mock_ydl.extract_info.return_value = {"formats": [], "subtitles": {}, "url": None}

        StreamExtractor.extract_stream_info("https://www.youtube.com/watch?v=xyz")

    mock_extract.assert_not_called()
    mock_voe.assert_not_called()
    mock_ydl.extract_info.assert_called_once()


def test_extract_stream_info_url_no_permitida_no_llega_a_ningun_extractor():
    with patch("resolvers.stream_extractor.BrowserStreamExtractor.extract_byse") as mock_extract:
        resultado = StreamExtractor.extract_stream_info("file:///etc/passwd")

    mock_extract.assert_not_called()
    assert resultado["success"] is False


def test_download_stream_no_imprime_el_progreso_de_yt_dlp_por_la_salida_estandar(tmp_path):
    # La salida estándar del daemon es el canal de la respuesta (una línea JSON): si yt-dlp escribe ahí su progreso
    # ("[download] 12.3 % …", miles de líneas en un HLS) el lector de C# no puede interpretar la respuesta.
    destino = str(tmp_path / "episodio.mp4")
    with patch("resolvers.stream_extractor.yt_dlp.YoutubeDL") as mock_ydl_cls:
        mock_ydl = mock_ydl_cls.return_value.__enter__.return_value
        mock_ydl.extract_info.return_value = {"title": "master"}
        mock_ydl.prepare_filename.return_value = destino

        StreamExtractor.download_stream("https://cdn.example.com/master.m3u8", destino)

    opciones = mock_ydl_cls.call_args.args[0]
    assert opciones.get("noprogress") is True
    assert opciones.get("quiet") is True


@pytest.mark.parametrize("url", [
    "http://cdn.example.com/video.mp4",          # http en claro
    "https://127.0.0.1/video.mp4",               # la propia máquina
    "https://192.168.1.10/video.mp4",            # la red local
    "https://169.254.169.254/latest/meta-data",  # metadatos de una nube
    "https://router/video.mp4",                  # nombre de intranet
    "https://nas.local/video.mp4",
    "https://usuario:clave@cdn.example.com/v.mp4",
    "file:///C:/Windows/win.ini",
    "",
])
def test_resolve_y_download_rechazan_lo_que_no_sea_https_hacia_internet(url, tmp_path):
    with patch("resolvers.stream_extractor.yt_dlp.YoutubeDL") as mock_ydl_cls:
        resuelto = StreamExtractor.extract_stream_info(url)
        descargado = StreamExtractor.download_stream(url, str(tmp_path / "episodio.mp4"))

    assert resuelto["success"] is False
    assert descargado["success"] is False
    mock_ydl_cls.assert_not_called()


# ── Avance de la descarga (HLS): un aviso por punto porcentual ──

def _avances(avisos):
    recibidos = []
    aviso = StreamExtractor.avisador_de_avance(lambda porcentaje, velocidad: recibidos.append((porcentaje, velocidad)))
    for d in avisos:
        aviso(d)
    return recibidos


def test_avance_por_fragmentos_avisa_una_vez_por_punto_porcentual():
    # 400 fragmentos: cada uno es un cuarto de punto, y yt-dlp avisa varias veces dentro del mismo fragmento.
    avisos = [{"status": "downloading", "fragment_index": i, "fragment_count": 400, "speed": 1000.0 + i} for i in range(0, 9) for _ in range(3)]

    assert _avances(avisos) == [(0.0, 1000.0), (1.0, 1004.0), (2.0, 1008.0)]


def test_avance_sin_fragmentos_usa_los_bytes_y_el_tamano_estimado():
    avisos = [
        {"status": "downloading", "downloaded_bytes": 50, "total_bytes_estimate": 200, "speed": None},
        {"status": "downloading", "downloaded_bytes": 300, "total_bytes": 200},  # la estimación se quedó corta: nunca más de 100
    ]

    assert _avances(avisos) == [(25.0, 0.0), (100.0, 0.0)]


def test_avance_ignora_lo_que_no_es_descarga_o_no_tiene_total():
    avisos = [
        {"status": "finished", "fragment_index": 4, "fragment_count": 4},
        {"status": "downloading", "downloaded_bytes": 50},
        {"status": "downloading"},
    ]

    assert _avances(avisos) == []


def test_download_stream_pasa_el_aviso_de_avance_a_yt_dlp(monkeypatch, tmp_path):
    opciones = {}

    class YdlFalso:
        def __init__(self, opts):
            opciones.update(opts)

        def __enter__(self):
            return self

        def __exit__(self, *_):
            return False

        def extract_info(self, url, download):
            for aviso in opciones.get("progress_hooks", []):
                aviso({"status": "downloading", "fragment_index": 1, "fragment_count": 2, "speed": 512})
            return None

    monkeypatch.setattr("resolvers.stream_extractor.yt_dlp.YoutubeDL", YdlFalso)
    monkeypatch.setattr(StreamExtractor, "_is_safe_http_url", staticmethod(lambda url: True))
    recibidos = []

    StreamExtractor.download_stream("https://cdn.example.com/master.m3u8", str(tmp_path / "ep.mkv"), None, lambda p, v: recibidos.append((p, v)))

    assert recibidos == [(50.0, 512.0)]
    assert opciones["noprogress"] is True, "el progreso en texto de yt-dlp sigue fuera de la salida"
