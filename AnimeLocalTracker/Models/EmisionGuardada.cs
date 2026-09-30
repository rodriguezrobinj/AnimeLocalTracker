using SQLite;

namespace AnimeLocalTracker.Models;

/// <summary>
/// Episodio de la programación de emisión tal como lo dio AniList la última vez que hubo conexión: Calendario y Actualizaciones lo
/// muestran sin internet (antes se quedaban vacíos al abrir la app sin conexión). Tabla creada por la migración v17, con índice
/// único (AniListId, Episodio); se guardan las ventanas consultadas y se descarta lo de hace más de 60 días.
/// </summary>
public class EmisionGuardada
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    public int AniListId { get; set; }

    public int Episodio { get; set; }

    /// <summary>Momento de emisión en segundos Unix UTC (tal como lo da AniList).</summary>
    public long EmisionUnixUtc { get; set; }

    public string Titulo { get; set; } = string.Empty;

    public string UrlPortada { get; set; } = string.Empty;
}
