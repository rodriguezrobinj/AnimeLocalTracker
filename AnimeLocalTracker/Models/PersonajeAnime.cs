using System;
using SQLite;

namespace AnimeLocalTracker.Models;

/// <summary>
/// Un personaje de un anime según AniList, guardado para jugar "Adivina el personaje" sin volver a preguntar.
/// El mismo personaje puede salir en varios animes (serie y película), así que la clave es propia y el índice único
/// (AnimeId, PersonajeId) lo crea la migración v12 de DatabaseService.
/// </summary>
public class PersonajeAnime
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>Id de AniList del anime en el que sale.</summary>
    public int AnimeId { get; set; }

    /// <summary>Id de AniList del personaje.</summary>
    public int PersonajeId { get; set; }

    /// <summary>Nombre completo tal como lo escribe AniList ("Light Yagami").</summary>
    public string Nombre { get; set; } = string.Empty;

    public string NombreNativo { get; set; } = string.Empty;

    /// <summary>Apodos y otras grafías separados por " | " (sirven de pista y para tapar el nombre).</summary>
    public string Alternativos { get; set; } = string.Empty;

    public string ImagenUrl { get; set; } = string.Empty;

    /// <summary>Male / Female / otro / vacío si AniList no lo indica.</summary>
    public string Genero { get; set; } = string.Empty;

    /// <summary>Texto libre de AniList ("17-", "13-15 (Pre-timeskip)"); vacío si no se sabe.</summary>
    public string Edad { get; set; } = string.Empty;

    /// <summary>MAIN / SUPPORTING / BACKGROUND.</summary>
    public string Rol { get; set; } = string.Empty;

    public int Favoritos { get; set; }
}

/// <summary>
/// Marca que los personajes de un anime ya se consultaron a AniList (aunque no tenga ninguno), para no volver a
/// preguntar en cada partida y saber cuándo refrescarlos.
/// </summary>
public class PersonajesAnimeSync
{
    [PrimaryKey]
    public int AnimeId { get; set; }

    public DateTime FechaUtc { get; set; }
}
