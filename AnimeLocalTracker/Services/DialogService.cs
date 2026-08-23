using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using AnimeLocalTracker.Messages;

namespace AnimeLocalTracker.Services;

public class DialogService : IDialogService
{
    public async Task<bool> MostrarDialogoAsync(string titulo, string mensaje, bool esConfirmacion = false, string icono = "InformationOutline", string color = "#3F51B5")
    {
        try
        {
            var request = new MostrarDialogoRequestMessage(titulo, mensaje, esConfirmacion, icono, color);
            var message = WeakReferenceMessenger.Default.Send(request);
            if (message.HasReceivedResponse)
            {
                return await message.Response;
            }
            return false;
        }
        catch (Exception ex)
        {
            AppLogger.Error("DialogService", $"Error al mostrar diálogo '{titulo}': {ex.Message}", ex);
            return false;
        }
    }
}
