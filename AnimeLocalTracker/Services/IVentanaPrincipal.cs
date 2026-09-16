namespace AnimeLocalTracker.Services;

/// <summary>
/// ARC-04: contrato mínimo de la ventana principal para los ViewModels que
/// necesitan alternar pantalla completa o devolver el foco, sin acoplarse a la
/// clase concreta Views.MainWindow. Lo implementa la propia ventana principal.
/// </summary>
public interface IVentanaPrincipal
{
    bool IsFullScreen { get; }

    /// <summary>PIP-01: true mientras la ventana está en modo Picture-in-Picture (recuadro
    /// compacto, siempre encima, sin bordes).</summary>
    bool EsModoPiP { get; }

    void TogglePantallaCompleta();

    /// <summary>Encoge la ventana principal a un recuadro compacto anclado a una esquina de la
    /// pantalla, la fija por encima de las demás ventanas (Topmost) y quita el chrome de la app.</summary>
    void EntrarModoPiP();

    /// <summary>Restaura la ventana principal al tamaño/posición/estado que tenía antes de
    /// EntrarModoPiP().</summary>
    void SalirModoPiP();

    void Enfocar();
}
