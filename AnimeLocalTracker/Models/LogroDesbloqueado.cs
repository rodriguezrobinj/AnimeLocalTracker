using System;
using SQLite;

namespace AnimeLocalTracker.Models;

/// <summary>
/// Nivel de un logro ya conseguido. Se guarda para (1) avisar una sola vez al desbloquearlo y
/// (2) no perder el logro si después cambian los datos que lo originaron (p. ej. borras una serie).
/// El índice único (LogroId, Nivel) lo crea la migración v5 de DatabaseService.
/// </summary>
public class LogroDesbloqueado
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>Id de la familia (p. ej. "horas"); "__base__" marca la evaluación inicial silenciosa.</summary>
    public string LogroId { get; set; } = string.Empty;

    /// <summary>Nivel conseguido, 1 (Bronce) a 5 (Diamante). 0 solo para el marcador base.</summary>
    public int Nivel { get; set; }

    /// <summary>Momento del desbloqueo (UTC). Null en logros que ya cumplías antes de existir esta
    /// función: no se conoce la fecha real y no se inventa (mismo criterio que el historial).</summary>
    public DateTime? FechaUtc { get; set; }
}
