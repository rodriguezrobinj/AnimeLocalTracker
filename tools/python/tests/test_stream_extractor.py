import sys
import os
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


def test_extract_stream_info_url_no_byse_no_toca_el_extractor_de_navegador():
    with patch("resolvers.stream_extractor.BrowserStreamExtractor.extract_byse") as mock_extract, \
         patch("resolvers.stream_extractor.yt_dlp.YoutubeDL") as mock_ydl_cls:
        mock_ydl = mock_ydl_cls.return_value.__enter__.return_value
        mock_ydl.extract_info.return_value = {"formats": [], "subtitles": {}, "url": None}

        StreamExtractor.extract_stream_info("https://voe.sx/e/xyz")

    mock_extract.assert_not_called()
    mock_ydl.extract_info.assert_called_once()


def test_extract_stream_info_url_no_permitida_no_llega_a_ningun_extractor():
    with patch("resolvers.stream_extractor.BrowserStreamExtractor.extract_byse") as mock_extract:
        resultado = StreamExtractor.extract_stream_info("file:///etc/passwd")

    mock_extract.assert_not_called()
    assert resultado["success"] is False
