namespace AnimeLocalTracker.Services;

public class PlaybackWindowModeCoordinator : IPlaybackWindowModeCoordinator
{
    private readonly IVentanaPrincipal? _ventanaPrincipal;

    public PlaybackWindowModeCoordinator(IVentanaPrincipal? ventanaPrincipal)
    {
        _ventanaPrincipal = ventanaPrincipal;
    }

    public string? AlternarPantallaCompleta()
    {
        // ARC-04: sin cast a MainWindow — la ventana se consume vía contrato IVentanaPrincipal.
        if (_ventanaPrincipal == null) return null;

        _ventanaPrincipal.TogglePantallaCompleta();
        string icono = _ventanaPrincipal.IsFullScreen ? "FullscreenExit" : "Fullscreen";

        // Devolver foco a MainWindow para que las teclas sigan respondiendo
        System.Windows.Application.Current?.Dispatcher?.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Input,
            () => _ventanaPrincipal.Enfocar());

        return icono;
    }

    public string? IconoPantallaCompletaActual()
    {
        try
        {
            if (_ventanaPrincipal == null) return null;
            return _ventanaPrincipal.IsFullScreen ? "FullscreenExit" : "Fullscreen";
        }
        catch
        {
            // Entornos de pruebas sin ventana principal
            return null;
        }
    }

    public void EntrarModoMini() => _ventanaPrincipal?.EntrarModoPiP();

    public void SalirModoMini() => _ventanaPrincipal?.SalirModoPiP();
}
