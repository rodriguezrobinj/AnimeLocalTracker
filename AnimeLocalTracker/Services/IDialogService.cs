using System.Threading.Tasks;

namespace AnimeLocalTracker.Services;

public interface IDialogService
{
    Task<bool> MostrarDialogoAsync(string titulo, string mensaje, bool esConfirmacion = false, string icono = "InformationOutline", string color = "#3F51B5");
}
