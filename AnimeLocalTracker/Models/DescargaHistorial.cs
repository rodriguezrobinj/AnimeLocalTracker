using System;
using SQLite;

namespace AnimeLocalTracker.Models;

/// <summary>
/// Resultado FINAL de una descarga (completada o fallida), para que la pestaña Descargas conserve un historial
/// entre sesiones: antes las completadas desaparecían a los 1,5 s y las fallidas se esfumaban sin dejar rastro.
/// Guarda lo necesario para reintentar (carpeta + títulos con los que se buscó el episodio) y para abrir el archivo.
/// Tabla creada por la migración v7 de DatabaseService; la fecha va SIEMPRE en UTC.
/// </summary>
public class DescargaHistorial
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    public int AniListId { get; set; }
    public string AnimeTitulo { get; set; } = string.Empty;
    public int NumeroEpisodio { get; set; }

    /// <summary>Carpeta del anime donde se guarda el episodio (necesaria para reintentar y para reproducir).</summary>
    public string CarpetaDestino { get; set; } = string.Empty;

    /// <summary>Ruta final del archivo (vacía si la descarga falló).</summary>
    public string RutaArchivo { get; set; } = string.Empty;

    public long TamanoBytes { get; set; }

    /// <summary>Momento en que terminó (UTC). sqlite-net lo devuelve con Kind Unspecified: tratarlo como UTC al mostrarlo.</summary>
    [Indexed]
    public DateTime FechaUtc { get; set; }

    public bool Completada { get; set; }

    /// <summary>Motivo del fallo (solo si <see cref="Completada"/> es false).</summary>
    public string? Error { get; set; }

    /// <summary>Títulos alternativos (separados por " | ") con los que se buscó el episodio en la fuente.</summary>
    public string TitulosAlternativos { get; set; } = string.Empty;
}
