using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Messages;

public record NavegarMensaje_Detalle(AnimeItem AnimeSeleccionado);
public record NavegarMensaje_Galeria();
public record NavegarMensaje_VolverDelReproductor();
public record EpisodioActualizadoMensaje(int AnimeId, int NumeroEpisodio, bool VistoLocal);
