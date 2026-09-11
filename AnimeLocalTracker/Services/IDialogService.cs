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

    // COMMANDS
    CommunityToolkit.Mvvm.Input.IRelayCommand AceptarDialogoCommand { get; }
    CommunityToolkit.Mvvm.Input.IRelayCommand CancelarDialogoCommand { get; }

    Task<bool> MostrarDialogoAsync(string titulo, string mensaje, bool esConfirmacion = false, string icono = "InformationOutline", string color = "#3F51B5");
    void MostrarToast(string titulo, string mensaje, string icono = "InformationOutline", string color = "#3F51B5");
}
