namespace AnimeLocalTracker.Messages;

/// <summary>Salieron episodios nuevos (de <paramref name="Desde"/> a <paramref name="Hasta"/>) de un anime con "Avisar" activado.</summary>
public sealed record EpisodioEmitidoMensaje(int AnimeId, string Titulo, string? UrlPortada, int Desde, int Hasta);
