using SQLite;

namespace AnimeLocalTracker.Models;

public class RegistroEpisodio
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }
    
    [Indexed] // Indexado para que las búsquedas sean ultrarrápidas
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
    public System.DateTime? UltimaReproduccion { get; set; }

    // Metadatos técnicos persistentes (ffprobe + miniaturas locales)
    public string? Resolucion { get; set; }
    public string? CodecVideo { get; set; }
    public string? Fps { get; set; }
    public bool Es10Bit { get; set; }
    public string? RutaMiniatura { get; set; }
}