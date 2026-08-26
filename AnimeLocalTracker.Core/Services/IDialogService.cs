using AnimeLocalTracker.Core.Services;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Core.Services;

public interface IDialogService
{
    Task<bool> MostrarDialogoAsync(string titulo, string mensaje, bool esConfirmacion = false, string icono = "InformationOutline", string color = "#3F51B5");
    string? SeleccionarCarpeta(string titulo, string rutaInicial = "");
}
