namespace AnimeLocalTracker.Messages;

/// <summary>El usuario vio de verdad (reproducción, no marcado a mano) el último episodio de una serie ya finalizada.</summary>
public sealed record SerieTerminadaMensaje(int AnimeId, string Titulo, string? UrlPortada, int TotalEpisodios);
