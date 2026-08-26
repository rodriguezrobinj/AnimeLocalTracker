using AnimeLocalTracker.Core.Services;
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using AnimeLocalTracker.Core.Models;

namespace AnimeLocalTracker.Core.Services;

public class SettingsService : ISettingsService
{
    private readonly string _settingsFilePath;
    private AppSettings _configuracion;
    private readonly object _lockObj = new();

    public event Action<AppSettings>? ConfiguracionModificada;

    public SettingsService(string? customSettingsPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customSettingsPath))
        {
            _settingsFilePath = customSettingsPath;
        }
        else
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var folder = Path.Combine(appData, "AnimeLocalTracker");
            Directory.CreateDirectory(folder);
            _settingsFilePath = Path.Combine(folder, "settings.json");
        }

        _configuracion = CargarConfiguracionInicial();
    }

    private AppSettings CargarConfiguracionInicial()
    {
        lock (_lockObj)
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    var json = File.ReadAllText(_settingsFilePath);
                    var config = JsonSerializer.Deserialize<AppSettings>(json);
                    if (config != null)
                    {
                        if (string.IsNullOrWhiteSpace(config.RutaBaseAnimes))
                        {
                            config.RutaBaseAnimes = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Anime");
                        }
                        return config;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("SettingsService", $"No se pudo leer archivo de configuración: {ex.Message}");
            }

            var defaultSettings = new AppSettings();
            GuardarEnDiscoInterno(defaultSettings);
            return defaultSettings;
        }
    }

    public AppSettings ObtenerConfiguracion()
    {
        lock (_lockObj)
        {
            return _configuracion;
        }
    }

    public Task GuardarConfiguracionAsync(AppSettings configuracion)
    {
        if (configuracion == null) return Task.CompletedTask;

        lock (_lockObj)
        {
            _configuracion = configuracion;
            GuardarEnDiscoInterno(_configuracion);
        }

        AppLogger.Info("SettingsService", "Configuración guardada exitosamente.");
        ConfiguracionModificada?.Invoke(configuracion);
        return Task.CompletedTask;
    }

    private void GuardarEnDiscoInterno(AppSettings configuracion)
    {
        try
        {
            var directory = Path.GetDirectoryName(_settingsFilePath);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(configuracion, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsFilePath, json);
        }
        catch (Exception ex)
        {
            AppLogger.Error("SettingsService", "Error escribiendo archivo de configuración", ex);
        }
    }

    public string ObtenerRutaBaseAnimes()
    {
        lock (_lockObj)
        {
            return _configuracion.RutaBaseAnimes;
        }
    }

    public async Task EstablecerRutaBaseAnimesAsync(string nuevaRuta)
    {
        if (string.IsNullOrWhiteSpace(nuevaRuta)) return;

        try
        {
            if (!Directory.Exists(nuevaRuta))
            {
                Directory.CreateDirectory(nuevaRuta);
            }

            lock (_lockObj)
            {
                _configuracion.RutaBaseAnimes = nuevaRuta;
            }

            await GuardarConfiguracionAsync(_configuracion);
            AppLogger.Info("SettingsService", $"Ruta base de animes actualizada a: {nuevaRuta}");
        }
        catch (Exception ex)
        {
            AppLogger.Error("SettingsService", $"Error al establecer ruta base de animes: {nuevaRuta}", ex);
            throw;
        }
    }
}
