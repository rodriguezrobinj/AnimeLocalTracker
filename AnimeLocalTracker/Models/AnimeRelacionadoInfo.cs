using System;
using SQLite;

namespace AnimeLocalTracker.Models;

/// <summary>
/// Datos de AniList de una parte de una saga (orden de visionado de la ficha): título, formato, fecha de estreno y portada. Guarda
/// todas las partes, también las de la biblioteca (de esas solo se usan formato y fecha: <see cref="AnimeItem"/> no los tiene).
/// Se refresca cada 30 días. Tabla creada por la migración v27.
/// </summary>
public class AnimeRelacionadoInfo
{
    [PrimaryKey]
    public int AniListId { get; set; }

    public string Titulo { get; set; } = string.Empty;

    /// <summary>Formato de AniList: TV, TV_SHORT, MOVIE, SPECIAL, OVA, ONA, MUSIC (vacío si no se sabe).</summary>
    public string Formato { get; set; } = string.Empty;

    public int? Anio { get; set; }
    public int? Mes { get; set; }
    public int? Dia { get; set; }
    public int? Episodios { get; set; }
    public string UrlPortada { get; set; } = string.Empty;

    /// <summary>Cuándo se consultó (UTC; sqlite-net la devuelve con Kind Unspecified: tratarla como UTC).</summary>
    public DateTime FechaUtc { get; set; }
}
