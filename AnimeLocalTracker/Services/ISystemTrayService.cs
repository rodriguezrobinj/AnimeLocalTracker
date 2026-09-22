using System;
using System.Windows;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Icono de bandeja del sistema: cuando AppSettings.MinimizarABandejaAlCerrar está activo,
/// cerrar o minimizar la ventana principal la oculta (sin ocupar taskbar) en vez de salir,
/// dejando el icono con un menú rápido. Reacciona en caliente a cambios de la configuración
/// (no hace falta reiniciar la app para activar/desactivar la opción).
/// </summary>
public interface ISystemTrayService
{
    /// <summary>El usuario pidió reanudar el último episodio reproducido desde el menú de la bandeja.</summary>
    event EventHandler? ReanudarUltimoAnimeSolicitado;

    /// <summary>El usuario pidió una búsqueda manual de episodios nuevos desde el menú de la bandeja.</summary>
    event EventHandler? BuscarNuevosEpisodiosSolicitado;

    /// <summary>Debe llamarse una única vez, con la ventana principal, tan pronto esté disponible.</summary>
    void Habilitar(Window ventanaPrincipal);

    /// <summary>
    /// True si la ventana principal no es visible o no está activa (minimizada, oculta en la bandeja, o el usuario
    /// está en otra aplicación): en ese caso un toast interno de WPF pasaría inadvertido.
    /// </summary>
    bool VentanaEnSegundoPlano { get; }

    /// <summary>El usuario pidió (Configuración → General) que los avisos usen siempre la notificación nativa de
    /// Windows, aunque la ventana esté visible y activa.</summary>
    bool NotificarSiempreConBandeja { get; }

    /// <summary>Muestra una notificación nativa de Windows (útil cuando la ventana está oculta).</summary>
    void MostrarNotificacion(string titulo, string mensaje);
}
