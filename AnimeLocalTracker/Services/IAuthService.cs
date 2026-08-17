using System.Threading.Tasks;

namespace AnimeLocalTracker.Services;

public interface IAuthService
{
    string? Token { get; }
    bool EstaAutenticado();
    string ObtenerTokenGuardado();
    Task<bool> IniciarSesionAsync();
    string? ObtenerToken();
    void CerrarSesion();
}
