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

    /// <summary>Muestra una notificación nativa de Windows (útil cuando la ventana está oculta).</summary>
    void MostrarNotificacion(string titulo, string mensaje);
}
