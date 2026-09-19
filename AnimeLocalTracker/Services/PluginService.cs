using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Services.Python;

namespace AnimeLocalTracker.Services;

public class PluginService : IPluginService
{
    private readonly IPythonBridgeService _pythonBridge;

    public PluginService(IPythonBridgeService pythonBridge)
    {
        _pythonBridge = pythonBridge;
    }

    public List<string> ObtenerPluginsInstalados()
    {
        var pluginsDir = AppDataPaths.PluginsFolder;
        if (!Directory.Exists(pluginsDir))
        {
            return new List<string>();
        }

        // .py (script ejecutado por el daemon) y .dll (IProveedorVideo cargado en el arranque,
        // ver CSharpPluginLoader) — ambos comparten la misma carpeta de "drop-in" plugins.
        return Directory.GetFiles(pluginsDir, "*.py")
            .Concat(Directory.GetFiles(pluginsDir, "*.dll"))
            .Select(Path.GetFileName)
            .Where(f => f != null)
            .Select(f => f!)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<TResponse?> EjecutarPluginAsync<TRequest, TResponse>(string pluginFileName, string functionName, TRequest args, CancellationToken ct = default)
    {
        if (!await _pythonBridge.IsAvailableAsync())
        {
            AppLogger.Warn("PluginService", "Demonio de Python no disponible. No se puede ejecutar el plugin.");
            return default;
        }

        string pluginPath = Path.Combine(AppDataPaths.PluginsFolder, pluginFileName);
        
        var payload = new
        {
            plugin_path = pluginPath,
            func_name = functionName,
            args = args
        };

        var daemonResponse = await _pythonBridge.ExecuteCommandAsync<object, PluginDaemonResponse<TResponse>>("run-plugin", payload, ct);

        if (daemonResponse == null)
        {
            return default;
        }

        if (!daemonResponse.Success)
        {
            AppLogger.Error("PluginService", $"Error en plugin {pluginFileName}: {daemonResponse.Error}");
            return default;
        }

        return daemonResponse.Result;
    }
}

public class PluginDaemonResponse<T>
{
    public bool Success { get; set; }
    public T? Result { get; set; }
    public string? Error { get; set; }
}
