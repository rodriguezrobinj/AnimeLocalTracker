using System;
using SQLite;

namespace AnimeLocalTracker.Models;

/// <summary>
/// Una partida de minijuego terminada (solo las que llegan al resumen: las abandonadas no cuentan). De estas filas salen
/// los récords y las métricas de los logros de la categoría Minijuegos. El índice (JuegoId, Puntos) lo crea la migración
/// v11 de DatabaseService (los [Indexed] del modelo solo aplican a bases nuevas: persistence.md #6).
/// </summary>
public class PartidaMinijuego
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>Identificador estable del juego (<see cref="AnimeLocalTracker.Services.Minijuegos.JuegosMinijuego"/>).</summary>
    public string JuegoId { get; set; } = string.Empty;

    /// <summary>Cuándo terminó la partida (UTC; sqlite-net la devuelve con Kind Unspecified: tratarla como UTC).</summary>
    public DateTime FechaUtc { get; set; }

    public int Puntos { get; set; }

    /// <summary>Rondas realmente jugadas (pueden ser menos de 10 si la biblioteca o las descargas no dieron para más).</summary>
    public int Rondas { get; set; }

    public int Aciertos { get; set; }

    /// <summary>Mayor número de aciertos seguidos dentro de la partida.</summary>
    public int RachaMaxima { get; set; }
}
