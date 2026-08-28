import re
from typing import Optional, Dict, Any
import anitopy
from rapidfuzz import fuzz, process

class AnimeFileParser:
    @staticmethod
    def parse_filename(filename: str, clean_directory_context: Optional[str] = None) -> Dict[str, Any]:
        """
        Analiza el nombre de un archivo de video de anime usando anitopy y normalizaciones avanzadas.
        """
        # Extraer extensión
        ext_match = re.search(r'\.([a-zA-Z0-9]+)$', filename)
        extension = f".{ext_match.group(1).lower()}" if ext_match else ""
        
        # Parseo con Anitopy
        parsed = anitopy.parse(filename) or {}
        
        # Extraer número de episodio
        episode_raw = parsed.get("episode_number")
        episode_number: Optional[int] = None
        if episode_raw:
            if isinstance(episode_raw, list):
                episode_raw = episode_raw[0]
            try:
                # Manejar "01", "12v2", "12.5"
                num_clean = re.sub(r'[^\d.]', '', str(episode_raw))
                if num_clean:
                    episode_number = int(float(num_clean))
            except Exception:
                episode_number = None

        # Fallback de episodio si anitopy no lo detectó
        if episode_number is None:
            regex_patterns = [
                r'(?:ep|cap|episodio|capitulo)[\s._-]*(\d+)',
                r'[\s._-](\d{1,4})(?:v\d)?[\s._-]',
                r' - (\d{1,4})(?:v\d)?'
            ]
            for pat in regex_patterns:
                m = re.search(pat, filename, re.IGNORECASE)
                if m:
                    try:
                        episode_number = int(m.group(1))
                        break
                    except Exception:
                        pass

        # Extraer temporada
        season_raw = parsed.get("anime_season")
        season_number: Optional[int] = None
        if season_raw:
            try:
                season_number = int(re.sub(r'[^\d]', '', str(season_raw)))
            except Exception:
                pass
        if season_number is None:
            s_match = re.search(r'[Ss](?:eason)?[\s._-]*(\d+)', filename)
            if s_match:
                try:
                    season_number = int(s_match.group(1))
                except Exception:
                    pass

        # Título limpio del anime
        anime_title = parsed.get("anime_title")
        if not anime_title and clean_directory_context:
            anime_title = clean_directory_context
        elif not anime_title:
            anime_title = filename
            # Quitar extensiones y grupos
            anime_title = re.sub(r'\.[a-zA-Z0-9]+$', '', anime_title)
            anime_title = re.sub(r'\[.*?\]|\(.*?\)', '', anime_title).strip()

        return {
            "success": True,
            "filename": filename,
            "anime_title": str(anime_title).strip() if anime_title else "",
            "animeTitle": str(anime_title).strip() if anime_title else "",
            "episode_number": episode_number,
            "episodeNumber": episode_number,
            "season_number": season_number or 1,
            "seasonNumber": season_number or 1,
            "release_group": str(parsed.get("release_group") or "").strip(),
            "releaseGroup": str(parsed.get("release_group") or "").strip(),
            "video_resolution": str(parsed.get("video_resolution") or "").strip(),
            "videoResolution": str(parsed.get("video_resolution") or "").strip(),
            "video_codec": str(parsed.get("video_term") or "").strip(),
            "videoCodec": str(parsed.get("video_term") or "").strip(),
            "audio_codec": str(parsed.get("audio_term") or "").strip(),
            "audioCodec": str(parsed.get("audio_term") or "").strip(),
            "extension": extension
        }

    @staticmethod
    def parse_batch(filenames: list[str], clean_directory_context: Optional[str] = None) -> list[Dict[str, Any]]:
        """
        Analiza una lista completa de nombres de archivos en una sola llamada ultrarrápida.
        """
        return [AnimeFileParser.parse_filename(f, clean_directory_context) for f in filenames]

    @staticmethod
    def match_title_fuzzy(query: str, candidates: list[str], threshold: float = 75.0) -> Optional[Dict[str, Any]]:
        """
        Encuentra la mejor coincidencia difusa de título usando RapidFuzz con ponderación de ratios.
        """
        if not query or not candidates:
            return None
            
        result = process.extractOne(
            query, 
            candidates, 
            scorer=fuzz.token_sort_ratio
        )
        
        if result and result[1] >= threshold:
            return {
                "matched_title": result[0],
                "matchedTitle": result[0],
                "score": float(result[1]),
                "index": int(result[2])
            }
        return None
