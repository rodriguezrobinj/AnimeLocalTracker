using AnimeLocalTracker.Core.Services;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Core.Services;

public interface IAuthService
{
    string? Token { get; }
    bool EstaAutenticado();
    string ObtenerTokenGuardado();
    Task<bool> IniciarSesionAsync();
    string? ObtenerToken();
    void CerrarSesion();
}
