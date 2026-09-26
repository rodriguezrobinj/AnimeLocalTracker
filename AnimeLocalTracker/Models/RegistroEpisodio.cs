using SQLite;

namespace AnimeLocalTracker.Models;

public class RegistroEpisodio
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }
    
    // DB-01: SIN [Indexed]. Las búsquedas por anime usan el índice compuesto IX_RegistroEpisodio_AnimeEp
    // (AniListId, NumeroEpisodio), que ya cubre AniListId como prefijo; el índice automático de [Indexed] era redundante
    // y solo encarecía cada escritura. Los índices se crean con migraciones explícitas (ver DatabaseService.Migraciones).
    public int AniListId { get; set; } // Para saber a qué anime pertenece este capítulo
    
    public int NumeroEpisodio { get; set; }
    
    // Para identificar exactamente qué archivo vio el usuario
    public string RutaArchivo { get; set; } = string.Empty; 
    
    // El núcleo de nuestro sistema híbrido:
    public bool VistoLocal { get; set; }
    public bool FavoritoLocal { get; set; } // Añadido para guardar si es favorito
    public bool SincronizadoEnNube { get; set; } // Preparando el terreno para la Fase 2

    // Reanudación de reproducción (Resume Playback):
    public double ProgresoSegundos { get; set; }
    public double TotalSegundos { get; set; }
    // DB-01: sin [Indexed]; el índice IX_RegistroEpisodio_UltimaReproduccion lo crea la migración v3.
    public System.DateTime? UltimaReproduccion { get; set; }

    // Metadatos técnicos persistentes (ffprobe + miniaturas locales)
    public string? Resolucion { get; set; }
    public string? CodecVideo { get; set; }
    public string? Fps { get; set; }
    public bool Es10Bit { get; set; }
    public string? RutaMiniatura { get; set; }
}