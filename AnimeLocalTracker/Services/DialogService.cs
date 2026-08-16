using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using AnimeLocalTracker.Messages;

namespace AnimeLocalTracker.Services;

public class DialogService : IDialogService
{
    public Task<bool> MostrarDialogoAsync(string titulo, string mensaje, bool esConfirmacion = false, string icono = "InformationOutline", string color = "#3F51B5")
    {
        var request = new MostrarDialogoRequestMessage(titulo, mensaje, esConfirmacion, icono, color);
        return WeakReferenceMessenger.Default.Send(request).Response;
    }
}
