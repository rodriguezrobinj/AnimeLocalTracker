import pytest
import sys
import os

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), '..')))

from parsers.anime_parser import AnimeFileParser

def test_parse_filename_standard():
    result = AnimeFileParser.parse_filename("[SubsPlease] Naruto - 12 (1080p).mkv")
    assert result["success"] is True
    assert result["anime_title"] == "Naruto"
    assert result["episode_number"] == 12
    assert result["release_group"] == "SubsPlease"
    assert result["video_resolution"] == "1080p"
    assert result["extension"] == ".mkv"

def test_parse_filename_v2():
    result = AnimeFileParser.parse_filename("[Erai-raws] Jujutsu Kaisen - 02v2 [1080p].mp4")
    assert result["anime_title"] == "Jujutsu Kaisen"
    assert result["episode_number"] == 2
    assert result["extension"] == ".mp4"

def test_parse_filename_with_season():
    result = AnimeFileParser.parse_filename("Bleach S2 - 14.mkv")
    assert result["season_number"] == 2
    assert result["episode_number"] == 14

def test_match_title_fuzzy():
    candidates = ["Naruto", "Naruto Shippuden", "Boruto"]
    result = AnimeFileParser.match_title_fuzzy("naruto shippuuden", candidates)
    assert result is not None
    assert result["matched_title"] == "Naruto Shippuden"
    assert result["score"] >= 75.0
