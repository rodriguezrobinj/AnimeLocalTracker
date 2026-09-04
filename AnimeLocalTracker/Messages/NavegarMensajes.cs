using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Messages;

// === Mensajes de Navegación ===
public record NavegarMensaje_Galeria();
public record NavegarMensaje_AgregarAnime();
public record NavegarMensaje_Detalle(AnimeItem AnimeSeleccionado);
public record NavegarMensaje_Calendario();
public record NavegarMensaje_Descargas();
public record NavegarMensaje_Configuracion();
public record NavegarMensaje_AcercaDe();
public record NavegarMensaje_Estadisticas();
public record NavegarMensaje_Historial();
public record NavegarMensaje_Reproductor(string RutaVideo, int AnimeId, string TituloAnime, int Episodio, System.Collections.Generic.List<EpisodioItem>? EpisodiosDisponibles = null);
public record NavegarMensaje_VolverDelReproductor();

// === Mensajes de Estado / Notificaciones ===
public record EpisodioActualizadoMensaje(int AnimeId, int NumeroEpisodio, bool VistoLocal, double ProgresoSegundos = 0, double TotalSegundos = 0);
public record AnimeAñadidoMensaje(AnimeItem NuevoAnime);
public record UsuarioLogeadoMensaje();
public record UsuarioDesconectadoMensaje();
public record AbrirBuscadorMensaje();
