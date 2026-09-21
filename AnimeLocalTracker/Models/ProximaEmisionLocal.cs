using System;
using SQLite;

namespace AnimeLocalTracker.Models;

/// <summary>
/// Próximo episodio de un anime en emisión guardado en local, para mostrar la cuenta atrás sin consultar AniList
/// en cada visita. Solo se vuelve a preguntar cuando puede haber cambiado la programación (ver
/// <see cref="Services.ProximaEmisionService"/>). Tabla creada por la migración v8; fechas SIEMPRE en UTC.
/// </summary>
public class ProximaEmisionLocal
{
    [PrimaryKey]
    public int AniListId { get; set; }

    /// <summary>Número del próximo episodio; 0 = AniList no tiene ninguno programado (fin de temporada, pausa…).</summary>
    public int Episodio { get; set; }

    /// <summary>Momento de emisión (segundos Unix UTC, tal como lo da AniList). 0 si no hay episodio programado.</summary>
    public long EmisionUnixUtc { get; set; }

    /// <summary>Cuándo se consultó AniList por última vez (UTC). sqlite-net lo devuelve con Kind Unspecified: tratarlo como UTC.</summary>
    public DateTime ConsultadoUtc { get; set; }
}
