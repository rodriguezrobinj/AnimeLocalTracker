namespace AnimeLocalTracker.Services.Discord;

/// <summary>Lo que el reproductor está mostrando, tal como se publica en el estado de Discord.</summary>
public sealed record EstadoPresencia(int AnimeId, string Titulo, int Episodio, double PosicionSeg, double DuracionSeg, bool Reproduciendo);
