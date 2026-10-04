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

public static class SettingsServiceExtensions
{
    /// <summary>
    /// Cambia una o varias preferencias y las guarda: lee los ajustes, aplica <paramref name="cambio"/> y los escribe en disco.
    /// No hace nada si todavía no hay ajustes cargados. Es el gesto que antes se repetía a mano en cada ViewModel.
    /// </summary>
    public static Task ActualizarAsync(this ISettingsService ajustes, Action<AppSettings> cambio)
    {
        var config = ajustes.ObtenerConfiguracion();
        if (config == null) return Task.CompletedTask;

        cambio(config);
        return ajustes.GuardarConfiguracionAsync(config);
    }
}
