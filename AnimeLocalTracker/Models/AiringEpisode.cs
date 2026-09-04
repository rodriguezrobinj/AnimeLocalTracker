using System;

namespace AnimeLocalTracker.Models;

public class AiringEpisode
{
    public int AniListId { get; set; }
    public string Titulo { get; set; } = string.Empty;
    public string UrlPortada { get; set; } = string.Empty;
    public int NumeroEpisodio { get; set; }
    public DateTime FechaEmision { get; set; }
    
    public DayOfWeek DiaSemana => FechaEmision.ToLocalTime().DayOfWeek;
    public string HoraEmisionFormateada => FechaEmision.ToLocalTime().ToString("HH:mm");

    /// <summary>True si la hora de emisión (hora local) ya pasó: el episodio está disponible/emitido.</summary>
    public bool EstaEmitido => FechaEmision.ToLocalTime() <= DateTime.Now;
}
