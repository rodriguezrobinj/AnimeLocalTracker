namespace AnimeLocalTracker.Services;

/// <summary>
/// ARC-04: contrato mínimo de la ventana principal para los ViewModels que
/// necesitan alternar pantalla completa o devolver el foco, sin acoplarse a la
/// clase concreta Views.MainWindow. Lo implementa la propia ventana principal.
/// </summary>
public interface IVentanaPrincipal
{
    bool IsFullScreen { get; }

    void TogglePantallaCompleta();

    void Enfocar();
}
