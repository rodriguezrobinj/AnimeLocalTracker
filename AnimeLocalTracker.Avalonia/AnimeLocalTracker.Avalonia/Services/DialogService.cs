using System.Threading.Tasks;
using AnimeLocalTracker.Core.Services;

namespace AnimeLocalTracker.Avalonia.Services;

public class DialogService : IDialogService
{
    public Task<bool> MostrarDialogoAsync(string titulo, string mensaje, bool esConfirmacion = false, string icono = "InformationOutline", string color = "#3F51B5")
    {
        return Task.FromResult(true); // Dummy implementation for now
    }

    public string? SeleccionarCarpeta(string titulo, string rutaInicial = "")
    {
        return null; // Dummy
    }
}
