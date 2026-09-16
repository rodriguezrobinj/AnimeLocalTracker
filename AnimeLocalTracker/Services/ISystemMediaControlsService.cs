using System;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Integra los Controles Multimedia del Sistema de Windows (SMTC): permite que las teclas
/// Play/Pausa/Siguiente/Anterior (auriculares Bluetooth, teclado multimedia) controlen la
/// reproducción aunque la ventana de la app no tenga el foco, y muestra el overlay nativo de
/// Windows (junto al control de volumen) con la portada, el título del anime y el episodio.
/// </summary>
public interface ISystemMediaControlsService
{
    event EventHandler? PlayRequested;
    event EventHandler? PauseRequested;
    event EventHandler? NextRequested;
    event EventHandler? PreviousRequested;

    /// <summary>Debe llamarse una única vez, con el HWND de la ventana principal, tan pronto esté disponible.</summary>
    void Inicializar(IntPtr hwndVentanaPrincipal);

    /// <summary>Actualiza el título/episodio/portada mostrados en el overlay y habilita los controles.</summary>
    void ActualizarMetadatos(string tituloAnime, int numeroEpisodio, string? rutaPortada);

    void ActualizarEstadoReproduccion(bool reproduciendo);

    void ActualizarNavegacionDisponible(bool haySiguiente, bool hayAnterior);

    /// <summary>Oculta los controles del sistema (se llama al cerrar el reproductor).</summary>
    void Deshabilitar();
}
