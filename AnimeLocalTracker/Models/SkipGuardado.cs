using System;
using SQLite;

namespace AnimeLocalTracker.Models;

/// <summary>
/// Resultado guardado de analizar un episodio para ubicar su opening/ending/resumen. Con esto la segunda vez que se abre el
/// episodio los tramos salen al instante (y en la barra de progreso) sin volver a detectar ni consultar la nube. La
/// <see cref="Firma"/> (tamaño + fecha del archivo) invalida el resultado si el video cambia. El índice único
/// (AnimeId, Episodio) lo crea la migración v13 de DatabaseService.
/// </summary>
public class AnalisisSkipEpisodio
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    public int AnimeId { get; set; }

    public int Episodio { get; set; }

    /// <summary>"tamaño-ticksDeModificación" del archivo analizado.</summary>
    public string Firma { get; set; } = string.Empty;

    public DateTime FechaUtc { get; set; }

    /// <summary>
    /// False si algo no se pudo consultar del todo (sin red para bajar la referencia, anime sin datos en la nube…): ese análisis
    /// se repite pasado un tiempo. True = definitivo mientras el archivo no cambie.
    /// </summary>
    public bool Completo { get; set; }
}

/// <summary>Un tramo (opening, ending, resumen…) ubicado en un episodio analizado. Tipo usa los nombres de AniSkip: op, ed, mixed-op, mixed-ed, recap.</summary>
public class SegmentoSkipGuardado
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    public int AnimeId { get; set; }

    public int Episodio { get; set; }

    public string Tipo { get; set; } = string.Empty;

    public double Inicio { get; set; }

    public double Fin { get; set; }

    /// <summary>De dónde salió: audio (referencia de AnimeThemes), aniskip (comunidad) o escenas.</summary>
    public string Origen { get; set; } = string.Empty;

    /// <summary>Confianza de la detección por audio (0 a 1); 0 si no aplica.</summary>
    public double Confianza { get; set; }
}
