using System;
using SQLite;

namespace AnimeLocalTracker.Models;

/// <summary>
/// Una relación entre dos animes según AniList (precuela, secuela, historia paralela, spin-off…).
/// Sirve para agrupar franquicias: One Piece, sus películas y especiales cuentan como una sola.
/// El índice único (AnimeId, RelacionadoId) lo crea la migración v6 de DatabaseService.
/// </summary>
public class RelacionAnime
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    public int AnimeId { get; set; }

    public int RelacionadoId { get; set; }

    /// <summary>Tipo de relación visto desde <see cref="AnimeId"/> (PREQUEL, SEQUEL, PARENT, SIDE_STORY, SPIN_OFF…).</summary>
    public string Tipo { get; set; } = string.Empty;
}

/// <summary>
/// Marca que las relaciones de un anime ya se consultaron a AniList (aunque no tenga ninguna), para
/// no volver a preguntar cada vez y para saber cuándo refrescarlas (salen secuelas nuevas).
/// </summary>
public class RelacionAnimeSync
{
    [PrimaryKey]
    public int AnimeId { get; set; }

    public DateTime FechaUtc { get; set; }
}
