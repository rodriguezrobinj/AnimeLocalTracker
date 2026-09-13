using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Services;

public interface IPluginService
{
    List<string> ObtenerPluginsInstalados();
    Task<TResponse?> EjecutarPluginAsync<TRequest, TResponse>(string pluginFileName, string functionName, TRequest args, CancellationToken ct = default);
}
