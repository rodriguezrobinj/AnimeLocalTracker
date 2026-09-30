using System;
using SQLite;

namespace AnimeLocalTracker.Models;

/// <summary>
/// Seguimiento del usuario de un anime (estado, progreso, puntuación, fechas) guardado en local: el editor de la Ficha abre con
/// estos datos al instante y funciona sin conexión. <see cref="Pendiente"/> = cambio hecho sin poder enviarlo a AniList; lo sube
/// <see cref="Services.SyncService"/> cuando vuelve la conexión. Tabla creada por la migración v18; fechas del seguimiento sin
/// hora (día elegido por el usuario), <see cref="ModificadoUtc"/> en UTC.
/// </summary>
public class SeguimientoLocal
{
    [PrimaryKey]
    public int AniListId { get; set; }

    /// <summary>Estado de AniList en inglés (CURRENT, COMPLETED, PAUSED, DROPPED, PLANNING).</summary>
    public string Estado { get; set; } = "CURRENT";

    public int Progreso { get; set; }

    public float Puntaje { get; set; }

    public DateTime? FechaInicio { get; set; }

    public DateTime? FechaFin { get; set; }

    public bool Pendiente { get; set; }

    public DateTime ModificadoUtc { get; set; }
}
