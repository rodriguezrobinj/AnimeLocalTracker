using System;
using SQLite;

namespace AnimeLocalTracker.Models;

/// <summary>
/// Datos de AniList para las etiquetas de la ficha (nota media, formato, duración, estudio, fuente, tráiler), guardados
/// en local: cambian muy poco, así que se consultan como mucho una vez por semana. Tabla creada por la migración v9.
/// </summary>
public class DatosExtraAnime
{
    [PrimaryKey]
    public int AniListId { get; set; }

    /// <summary>TV, TV_SHORT, MOVIE, SPECIAL, OVA, ONA, MUSIC… (tal como lo da AniList).</summary>
    public string Formato { get; set; } = string.Empty;

    /// <summary>Minutos por episodio; 0 = desconocido.</summary>
    public int DuracionMin { get; set; }

    /// <summary>Nota media 0-100; 0 = sin nota todavía.</summary>
    public int NotaMedia { get; set; }

    public string Estudio { get; set; } = string.Empty;

    /// <summary>MANGA, LIGHT_NOVEL, ORIGINAL, VISUAL_NOVEL, GAME…</summary>
    public string Fuente { get; set; } = string.Empty;

    /// <summary>Identificador del tráiler en su sitio (youtube / dailymotion). Vacío si no hay.</summary>
    public string TrailerId { get; set; } = string.Empty;
    public string TrailerSitio { get; set; } = string.Empty;

    /// <summary>Cuándo se consultó AniList por última vez (UTC; sqlite-net lo devuelve con Kind Unspecified: tratarlo como UTC).</summary>
    public DateTime ConsultadoUtc { get; set; }
}
