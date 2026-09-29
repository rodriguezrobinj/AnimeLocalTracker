namespace AnimeLocalTracker.Services;

/// <summary>
/// ARC-04: contrato mínimo de la ventana principal para los ViewModels que
/// necesitan alternar pantalla completa o devolver el foco, sin acoplarse a la
/// clase concreta Views.MainWindow. Lo implementa la propia ventana principal.
/// </summary>
public interface IVentanaPrincipal
{
    bool IsFullScreen { get; }

    /// <summary>True mientras el episodio se ve en el mini reproductor (su propia ventana flotante).</summary>
    bool EsModoPiP { get; }

    void TogglePantallaCompleta();

    /// <summary>Pasa el episodio a una ventana flotante propia (siempre encima, sin dueño: sigue visible aunque la ventana
    /// principal se minimice) y deja la ventana principal libre para navegar.</summary>
    void EntrarModoPiP();

    /// <summary>Cierra la ventana del mini reproductor.</summary>
    void SalirModoPiP();

    /// <summary>Restaura la ventana principal (si está minimizada u oculta) y la pasa al frente.</summary>
    void MostrarYActivar();

    void Enfocar();
}
