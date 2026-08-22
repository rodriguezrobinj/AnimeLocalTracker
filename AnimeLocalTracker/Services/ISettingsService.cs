using System;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

public interface ISettingsService
{
    /// <summary>
    /// Obtiene la configuración actual de la aplicación.
    /// </summary>
    AppSettings ObtenerConfiguracion();

    /// <summary>
    /// Guarda la configuración actualizada en el disco.
    /// </summary>
    Task GuardarConfiguracionAsync(AppSettings configuracion);

    /// <summary>
    /// Obtiene la ruta base actual configurada para la biblioteca de animes.
    /// </summary>
    string ObtenerRutaBaseAnimes();

    /// <summary>
    /// Actualiza la ruta base de la biblioteca de animes y crea el directorio si no existe.
    /// </summary>
    Task EstablecerRutaBaseAnimesAsync(string nuevaRuta);

    /// <summary>
    /// Evento disparado cuando la configuración ha sido modificada.
    /// </summary>
    event Action<AppSettings>? ConfiguracionModificada;
}
