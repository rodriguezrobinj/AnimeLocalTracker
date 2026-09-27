using System;
using SQLite;

namespace AnimeLocalTracker.Models;

/// <summary>
/// Página de animeav1.com ya verificada como la de un anime (por MAL ID o por nombre), para que la
/// próxima descarga de ese anime vaya directo a ella en vez de repetir la búsqueda completa en el
/// catálogo — también tras reiniciar la app. Tabla creada por la migración v14; fechas SIEMPRE en UTC.
/// </summary>
public class MediaAnimeAv1Verificado
{
    [PrimaryKey]
    public int AniListId { get; set; }

    /// <summary>Slug de la página del anime en el sitio (animeav1.com/media/{Slug}).</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>MAL ID con el que se verificó (null si se aceptó por nombre): sigue usándose para verificar cada episodio.</summary>
    public int? MalId { get; set; }

    /// <summary>Última vez que se usó con éxito (UTC). sqlite-net lo devuelve con Kind Unspecified: tratarlo como UTC.</summary>
    public DateTime VerificadoUtc { get; set; }
}
