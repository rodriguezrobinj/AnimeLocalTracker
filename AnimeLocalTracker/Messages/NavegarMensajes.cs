using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Messages;

// === Mensajes de Navegación ===
/// <summary>Abrir una pestaña de la barra lateral (ver ViewModels/Pestana.cs). Se envía con <c>Pestanas.X.Abrir()</c>.</summary>
public record NavegarMensaje_Pestana(AnimeLocalTracker.ViewModels.Pestana Pestana);
/// <param name="ReproducirTemaClave">Opcional: al abrir la ficha, abrir el panel de música y reproducir ese opening/ending
/// (tipo|slug|versión). Lo usa "Reproducir" en el historial de descargas.</param>
public record NavegarMensaje_Detalle(AnimeItem AnimeSeleccionado, string? ReproducirTemaClave = null);
/// <summary>Visor de registros (se abre desde Configuración → Registro de diagnóstico).</summary>
public record NavegarMensaje_VisorRegistros();
public record NavegarMensaje_Reproductor(string RutaVideo, int AnimeId, string TituloAnime, int Episodio, System.Collections.Generic.List<EpisodioItem>? EpisodiosDisponibles = null, string? RutaPortada = null);
public record NavegarMensaje_VolverDelReproductor();



// === Mensajes de Estado / Notificaciones ===
/// <param name="SoloProgreso">True en el guardado periódico del reproductor (cada 3 s): solo cambió la posición, nunca el "visto".</param>
public record EpisodioActualizadoMensaje(int AnimeId, int NumeroEpisodio, bool VistoLocal, double ProgresoSegundos = 0, double TotalSegundos = 0, bool SoloProgreso = false);
public record AnimeAñadidoMensaje(AnimeItem NuevoAnime);
public record UsuarioLogeadoMensaje();
public record UsuarioDesconectadoMensaje();
public record AbrirBuscadorMensaje();

// LOC-08: notifica un cambio de Idioma vía WeakReferenceMessenger (referencias débiles, se
// des-registran solas al recolectar el receptor) en vez de suscribirse directo al evento
// estático de LocalizationService.Instance — esto último acumulaba suscriptores para
// siempre (cada instancia creada, p. ej. en tests, quedaba viva indefinidamente) y producía
// interferencia entre pruebas al correr en paralelo.
public record IdiomaCambiadoMensaje();
