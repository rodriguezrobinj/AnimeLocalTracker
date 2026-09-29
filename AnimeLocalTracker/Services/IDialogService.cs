using System.Threading.Tasks;
using System.ComponentModel;
using System.Windows.Input;

namespace AnimeLocalTracker.Services;

public interface IDialogService : INotifyPropertyChanged
{
    // DIÁLOGOS CUSTOM
    bool DialogoVisible { get; }
    string DialogoTitulo { get; }
    string DialogoMensaje { get; }
    bool DialogoEsConfirmacion { get; }
    string DialogoIcono { get; }
    string DialogoColor { get; }

    // TOAST NOTIFICATIONS
    bool ToastVisible { get; }
    string ToastTitulo { get; }
    string ToastMensaje { get; }
    string ToastIcono { get; }
    string ToastColor { get; }
    /// <summary>
    /// El aviso actual lo generó el propio reproductor (auto-tracking, reanudar, fin de episodio, logros por lo que acabas de ver…).
    /// El reproductor solo dibuja estos: los de fuera (descargas, emisiones, actualizaciones) no deben interrumpir el episodio.
    /// </summary>
    bool ToastDelReproductor { get; }

    // COMMANDS
    CommunityToolkit.Mvvm.Input.IRelayCommand AceptarDialogoCommand { get; }
    CommunityToolkit.Mvvm.Input.IRelayCommand CancelarDialogoCommand { get; }

    Task<bool> MostrarDialogoAsync(string titulo, string mensaje, bool esConfirmacion = false, string icono = "InformationOutline", string color = "#3F51B5");
    void MostrarToast(string titulo, string mensaje, string icono = "InformationOutline", string color = "#3F51B5");

    /// <summary>Aviso que sí se ve encima del video (ver <see cref="ToastDelReproductor"/>).</summary>
    void MostrarToastReproductor(string titulo, string mensaje, string icono = "InformationOutline", string color = "#3F51B5");

    /// <summary>
    /// Mientras dure (hasta Dispose), los avisos que se lancen desde este flujo —aunque sea dentro de otro servicio, como los
    /// logros al terminar un episodio— cuentan como del reproductor.
    /// </summary>
    System.IDisposable AvisosComoDelReproductor();
}
