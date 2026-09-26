using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

public interface IPluginService
{
    List<string> ObtenerPluginsInstalados();

    /// <summary>SEC-01: cada archivo de la carpeta de plugins con su huella SHA-256 y si el usuario confía en él.</summary>
    IReadOnlyList<PluginInfo> ObtenerDetallesPlugins();

    /// <summary>SEC-01: marca (o retira) la confianza en un archivo, fijando la huella de su contenido actual.
    /// False si el nombre no es válido o el archivo ya no existe.</summary>
    Task<bool> EstablecerConfianzaAsync(string nombreArchivo, bool confiar);

    Task<TResponse?> EjecutarPluginAsync<TRequest, TResponse>(string pluginFileName, string functionName, TRequest args, CancellationToken ct = default);
}
