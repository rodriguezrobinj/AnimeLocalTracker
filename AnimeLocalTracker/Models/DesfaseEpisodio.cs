using SQLite;

namespace AnimeLocalTracker.Models;

/// <summary>
/// Desfase de subtítulos y audio que el usuario ajustó en el reproductor para un capítulo concreto (teclas Z/X y J/K). Solo hay
/// fila para los capítulos ajustados: volver a 0 en los dos la borra. Tabla creada por la migración v26.
/// </summary>
public class DesfaseEpisodio
{
    /// <summary>"{AniListId}_{NumeroEpisodio}" (sqlite-net no admite claves primarias compuestas).</summary>
    [PrimaryKey]
    public string Clave { get; set; } = string.Empty;

    public int AniListId { get; set; }
    public int NumeroEpisodio { get; set; }

    /// <summary>Positivo = los subtítulos salen más tarde.</summary>
    public int SubtitulosMs { get; set; }

    /// <summary>Positivo = el audio suena más tarde.</summary>
    public int AudioMs { get; set; }

    public static string ClaveDe(int aniListId, int numeroEpisodio) => $"{aniListId}_{numeroEpisodio}";
}
