using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Core;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services.Python;

namespace AnimeLocalTracker.Services;

public class PluginService : IPluginService
{
    private readonly IPythonBridgeService _pythonBridge;
    private readonly ISettingsService? _settings;
    private readonly string _carpetaPlugins;

    public PluginService(IPythonBridgeService pythonBridge, ISettingsService? settings = null)
        : this(pythonBridge, settings, null)
    {
    }

    /// <summary>Permite apuntar a otra carpeta (pruebas): nunca se debe escribir en AppDataPaths desde los tests.</summary>
    internal PluginService(IPythonBridgeService pythonBridge, ISettingsService? settings, string? carpetaPlugins)
    {
        _pythonBridge = pythonBridge;
        _settings = settings;
        _carpetaPlugins = carpetaPlugins ?? AppDataPaths.PluginsFolder;
    }

    public List<string> ObtenerPluginsInstalados() => ListarArchivos().Select(Path.GetFileName).Where(f => f != null).Select(f => f!).ToList();

    public IReadOnlyList<PluginInfo> ObtenerDetallesPlugins()
    {
        var configuracion = _settings?.ObtenerConfiguracion() ?? new AppSettings();
        var resultado = new List<PluginInfo>();

        foreach (var ruta in ListarArchivos())
        {
            string nombre = Path.GetFileName(ruta);
            string huella = "";
            try { huella = ConfianzaPlugins.CalcularSha256(ruta); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLogger.Warn("PluginService", $"No se pudo leer '{nombre}' para calcular su huella: {ex.Message}");
            }

            var estado = huella.Length == 0
                ? EstadoConfianzaPlugin.SinConfiar
                : ConfianzaPlugins.Evaluar(configuracion, nombre, huella);
            resultado.Add(new PluginInfo(nombre, Path.GetExtension(nombre).ToLowerInvariant(), huella, estado));
        }

        return resultado;
    }

    public async Task<bool> EstablecerConfianzaAsync(string nombreArchivo, bool confiar)
    {
        if (_settings == null || !ConfianzaPlugins.EsNombreDePluginValido(nombreArchivo)) return false;

        var configuracion = _settings.ObtenerConfiguracion();
        var confiables = new Dictionary<string, string>(configuracion.PluginsConfiables ?? new(), StringComparer.OrdinalIgnoreCase);

        if (confiar)
        {
            string ruta = Path.Combine(_carpetaPlugins, nombreArchivo);
            if (!File.Exists(ruta)) return false;

            // La huella se calcula AHORA, sobre el contenido que el usuario está viendo, no una guardada antes.
            confiables[nombreArchivo] = ConfianzaPlugins.CalcularSha256(ruta);
        }
        else if (!confiables.Remove(nombreArchivo))
        {
            return true; // ya no estaba: nada que cambiar
        }

        configuracion.PluginsConfiables = confiables;
        await _settings.GuardarConfiguracionAsync(configuracion);
        AppLogger.Info("PluginService", confiar ? $"Plugin '{nombreArchivo}' marcado como confiable." : $"Confianza retirada al plugin '{nombreArchivo}'.");
        return true;
    }

    private IEnumerable<string> ListarArchivos()
    {
        if (!Directory.Exists(_carpetaPlugins)) return Enumerable.Empty<string>();

        // .py (script ejecutado por el daemon) y .dll (IProveedorVideo cargado en el arranque,
        // ver CSharpPluginLoader) — ambos comparten la misma carpeta de "drop-in" plugins.
        return Directory.GetFiles(_carpetaPlugins, "*.py")
            .Concat(Directory.GetFiles(_carpetaPlugins, "*.dll"))
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<TResponse?> EjecutarPluginAsync<TRequest, TResponse>(string pluginFileName, string functionName, TRequest args, CancellationToken ct = default)
    {
        // SEC-01: solo un nombre de archivo simple (sin "..\" ni carpetas) dentro de la carpeta de plugins...
        if (!ConfianzaPlugins.EsNombreDePluginValido(pluginFileName))
        {
            AppLogger.Warn("PluginService", $"Nombre de plugin no válido rechazado: '{pluginFileName}'.");
            return default;
        }

        string pluginPath = Path.Combine(_carpetaPlugins, pluginFileName);

        // ...y solo si los plugins están activados y el usuario confió en ESTE contenido exacto (huella SHA-256).
        var configuracion = _settings?.ObtenerConfiguracion() ?? new AppSettings();
        if (!ConfianzaPlugins.PuedeEjecutarse(configuracion, pluginPath))
        {
            AppLogger.Warn("PluginService", $"Plugin '{pluginFileName}' no ejecutado: plugins desactivados o archivo sin confianza/modificado.");
            return default;
        }

        if (!await _pythonBridge.IsAvailableAsync())
        {
            AppLogger.Warn("PluginService", "Demonio de Python no disponible. No se puede ejecutar el plugin.");
            return default;
        }

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
