import sqlite3
import random
import os
import sys
from typing import Dict, Any

class DbMockGenerator:
    @staticmethod
    def populate_sqlite(db_path: str, count: int = 1000) -> Dict[str, Any]:
        """
        Puebla una base de datos SQLite con miles de animes y episodios simulados
        para evaluar el rendimiento de la UI (VirtualizingWrapPanel) y consultas concurrentes.
        """
        genres = ["Action", "Adventure", "Comedy", "Drama", "Fantasy", "Sci-Fi", "Mystery", "Romance"]
        statuses = ["CURRENT", "PLANNING", "COMPLETED", "DROPPED", "PAUSED"]
        
        try:
            conn = sqlite3.connect(db_path)
            cur = conn.cursor()
            
            # Crear tablas si no existen según el esquema de SQLiteNetExtensions
            cur.execute("""
                CREATE TABLE IF NOT EXISTS Anime (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    AniListId INTEGER UNIQUE,
                    Titulo TEXT,
                    TituloRomaji TEXT,
                    TituloIngles TEXT,
                    Descripcion TEXT,
                    ImagenPortadaUrl TEXT,
                    ImagenBannerUrl TEXT,
                    Formato TEXT,
                    Estado TEXT,
                    EpisodiosTotales INTEGER,
                    Temporada TEXT,
                    Ano INTEGER,
                    Generos TEXT,
                    PuntuacionMedia REAL,
                    Popularidad INTEGER,
                    RutaLocal TEXT,
                    FechaModificacionLocal TEXT,
                    EpisodiosDescargadosCount INTEGER,
                    EstaEnBiblioteca INTEGER,
                    Favorito INTEGER,
                    TieneNuevosEpisodios INTEGER,
                    NuevosEpisodios INTEGER,
                    EstadoDescarga TEXT,
                    ProgresoDescarga REAL,
                    TotalBytesDescargados INTEGER,
                    TotalBytesEsperados INTEGER
                )
            """)
            
            cur.execute("""
                CREATE TABLE IF NOT EXISTS Episodio (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    AnimeId INTEGER,
                    NumeroEpisodio INTEGER,
                    Titulo TEXT,
                    RutaArchivo TEXT,
                    TamanoBytes INTEGER,
                    DuracionSegundos REAL,
                    FechaArchivo TEXT,
                    Visto INTEGER,
                    PosicionActualSegundos REAL,
                    FechaVisto TEXT,
                    FOREIGN KEY(AnimeId) REFERENCES Anime(Id)
                )
            """)
            
            animes_inserted = 0
            episodes_inserted = 0
            
            for i in range(1, count + 1):
                anilist_id = 100000 + i
                title = f"Mock Anime Series #{i:04d} - The Legendary Journey"
                ep_count = random.choice([12, 24, 26, 50, 100])
                status = random.choice(statuses)
                genre_str = ", ".join(random.sample(genres, k=min(3, len(genres))))
                
                cur.execute("""
                    INSERT OR REPLACE INTO Anime (
                        AniListId, Titulo, TituloRomaji, TituloIngles, Descripcion,
                        ImagenPortadaUrl, Formato, Estado, EpisodiosTotales, Ano,
                        Generos, PuntuacionMedia, Popularidad, EstaEnBiblioteca, Favorito,
                        TieneNuevosEpisodios, NuevosEpisodios
                    ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 1, ?, ?, ?)
                """, (
                    anilist_id, title, title, title, f"Descripcion sintetica de prueba para el anime #{i}",
                    f"https://placehold.co/300x400/png?text=Anime+{i}", "TV", status, ep_count, 2024,
                    genre_str, round(random.uniform(6.0, 9.8), 1), random.randint(1000, 500000),
                    1 if random.random() > 0.8 else 0,
                    1 if random.random() > 0.7 else 0,
                    random.randint(1, 5)
                ))
                anime_db_id = cur.lastrowid
                animes_inserted += 1
                
                # Generar episodios para este anime
                for ep in range(1, ep_count + 1):
                    is_watched = 1 if (ep <= int(ep_count * 0.5)) else 0
                    cur.execute("""
                        INSERT INTO Episodio (
                            AnimeId, NumeroEpisodio, Titulo, RutaArchivo,
                            TamanoBytes, DuracionSegundos, Visto, PosicionActualSegundos
                        ) VALUES (?, ?, ?, ?, ?, ?, ?, ?)
                    """, (
                        anime_db_id, ep, f"Episodio {ep}",
                        f"C:\\Animes\\MockAnime_{i}\\Episodio_{ep:02d}.mkv",
                        random.randint(300_000_000, 1_400_000_000),
                        1440.0, is_watched, 1440.0 if is_watched else 0.0
                    ))
                    episodes_inserted += 1
                    
            conn.commit()
            conn.close()
            
            return {
                "success": True,
                "db_path": db_path,
                "animes_inserted": animes_inserted,
                "episodes_inserted": episodes_inserted
            }
        except Exception as ex:
            return {
                "success": False,
                "error": str(ex)
            }

if __name__ == "__main__":
    import argparse
    parser = argparse.ArgumentParser()
    parser.add_argument("--db", default="mock_anime.db")
    parser.add_argument("--count", type=int, default=500)
    args = parser.parse_args()
    res = DbMockGenerator.populate_sqlite(args.db, args.count)
    print(res)
