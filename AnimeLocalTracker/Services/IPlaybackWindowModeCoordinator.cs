namespace AnimeLocalTracker.Services;

/// <summary>
/// Orquesta el modo de ventana del reproductor (pantalla completa y mini/PiP) contra
/// <see cref="IVentanaPrincipal"/>. El ViewModel sigue dueño de las propiedades observables
/// (EsModoMini/FullscreenIcon, que NavigationService también pisa directamente) — este
/// colaborador solo ejecuta la operación y devuelve el dato para que el ViewModel lo asigne.
/// </summary>
public interface IPlaybackWindowModeCoordinator
{
    /// <summary>
    /// Alterna pantalla completa y devuelve el foco a la ventana. Devuelve null si no hay
    /// ventana principal disponible (p. ej. en pruebas) — el llamador no debe tocar el ícono.
    /// </summary>
    string? AlternarPantallaCompleta();

    /// <summary>Ícono según el estado actual de pantalla completa, sin alternar nada — para
    /// sincronizar al cargar un episodio. Null si no hay ventana principal disponible.</summary>
    string? IconoPantallaCompletaActual();

    void EntrarModoMini();
    void SalirModoMini();

    /// <summary>Trae la ventana principal al frente (al volver del mini reproductor al formato habitual).</summary>
    void MostrarVentanaPrincipal();

    /// <summary>True si la ventana principal está a pantalla completa (false sin ventana, p. ej. en pruebas).</summary>
    bool EstaEnPantallaCompleta { get; }

    /// <summary>Vuelve al tamaño normal si estaba a pantalla completa (no hace nada si no lo estaba).</summary>
    void SalirDePantallaCompleta();
}
