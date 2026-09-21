using SQLite;

namespace AnimeLocalTracker.Models;

/// <summary>
/// Preferencias de un anime en emisión: avisar cuando sale un episodio nuevo y/o descargarlo automáticamente.
/// Tabla creada por la migración v9. Los contadores permiten que solo cuenten los episodios que salgan DESPUÉS de
/// activar la opción (al activarla se fijan al último episodio ya emitido) y que un episodio no se avise ni se
/// descargue dos veces.
/// </summary>
public class PreferenciaEmision
{
    [PrimaryKey]
    public int AniListId { get; set; }

    public bool Avisar { get; set; }
    public bool AutoDescargar { get; set; }

    /// <summary>Último episodio emitido del que ya se avisó.</summary>
    public int UltimoAvisado { get; set; }

    /// <summary>Último episodio emitido ya resuelto para la descarga automática (descargado, ya existente u omitido).</summary>
    public int UltimoDescargado { get; set; }
}
