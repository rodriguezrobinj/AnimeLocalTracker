using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

public class SettingsService : ISettingsService
{
    private readonly string _settingsFilePath;
    private AppSettings _configuracion;
    private readonly object _lockObj = new();

    // CA1869: opciones de serialización reutilizadas (persistencia de settings)
    private static readonly JsonSerializerOptions JsonOpcionesIndentadas = new() { WriteIndented = true };

    public event Action<AppSettings>? ConfiguracionModificada;

    public SettingsService(string? customSettingsPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customSettingsPath))
        {
            _settingsFilePath = customSettingsPath;
        }
        else
        {
            // ARQ-02: los datos del usuario SIEMPRE viven en %LocalAppData%\AnimeLocalTrackerData.
            _settingsFilePath = AppDataPaths.SettingsPath;
            AppDataPaths.MigrarArchivoDesdeRoaming("settings.json", _settingsFilePath);
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
                        // PIP-01: settings.json guardados antes de que existiera "ModoMini" tienen
                        // "AnteriorEpisodio": "P" persistido (el default viejo) — migrar ANTES de
                        // sanear, o SanitizarAtajos mantendría ese "P" y además llenaría "ModoMini"
                        // con su nuevo default ("P" también), recreando el mismo choque de teclas.
                        bool migrado = MigrarAtajoAnteriorEpisodioLegado(config);
                        // ATA-01: el settings.json puede estar editado a mano — sanear los atajos
                        SanitizarAtajos(config);
                        // El estilo de subtítulos también puede venir editado a mano (o nulo)
                        config.EstiloSubtitulos = (config.EstiloSubtitulos ?? new EstiloSubtitulos()).Normalizar();
                        // SEC-01: la lista de plugins de confianza también puede venir editada a mano (o nula)
                        config.PluginsConfiables = new Dictionary<string, string>(
                            config.PluginsConfiables ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
                        if (migrado) GuardarEnDiscoInterno(config);
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

    /// <summary>
    /// PIP-01: migra el atajo legado de "AnteriorEpisodio" ("P") a su nuevo default ("B") cuando
    /// detecta un settings.json de antes de que existiera "ModoMini" — así "P" queda libre para
    /// el atajo de Modo Mini/PiP sin pisar la personalización de un usuario que sí eligió "P" él mismo
    /// para Anterior (ese caso ya no tendría "P" en el archivo sin también tener "ModoMini").
    /// </summary>
    private static bool MigrarAtajoAnteriorEpisodioLegado(AppSettings config)
    {
        if (config.Atajos != null
            && config.Atajos.TryGetValue("AnteriorEpisodio", out var anterior)
            && string.Equals(anterior, "P", StringComparison.OrdinalIgnoreCase)
            && !config.Atajos.ContainsKey("ModoMini"))
        {
            config.Atajos["AnteriorEpisodio"] = "B";
            return true;
        }
        return false;
    }

    /// <summary>
    /// ATA-01: sanea el diccionario de atajos de un settings.json editado a mano.
    /// - Solo acciones conocidas; primera ocurrencia gana (descartar duplicados).
    /// - Se rechazan teclas de sistema (Win) y valores que no corresponden a una tecla real.
    /// - Cada tecla se asigna una sola vez: si dos acciones piden la misma, la segunda
    ///   vuelve a su valor por defecto.
    /// - Las acciones ausentes se completan con los defaults (fallback por acción).
    /// </summary>
    private static void SanitizarAtajos(AppSettings config)
    {
        var defaults = new AppSettings().Atajos;
        var sanitizado = new Dictionary<string, string>(StringComparer.Ordinal);
        var teclasUsadas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (config.Atajos != null)
        {
            foreach (var kv in config.Atajos)
            {
                if (!defaults.ContainsKey(kv.Key) || sanitizado.ContainsKey(kv.Key)) continue;
                if (!EsTeclaValida(kv.Value) || !teclasUsadas.Add(kv.Value)) continue;
                sanitizado[kv.Key] = kv.Value;
            }
        }

        foreach (var kv in defaults)
        {
            if (!sanitizado.ContainsKey(kv.Key)) sanitizado[kv.Key] = kv.Value;
        }

        config.Atajos = sanitizado;
    }

    private static bool EsTeclaValida(string? tecla)
    {
        if (string.IsNullOrWhiteSpace(tecla)) return false;
        if (Enum.TryParse<System.Windows.Input.Key>(tecla, out var key))
        {
            if (key == System.Windows.Input.Key.None) return false;
            if (key == System.Windows.Input.Key.LWin || key == System.Windows.Input.Key.RWin) return false;
            return Enum.IsDefined(key);
        }
        return false;
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

            var json = JsonSerializer.Serialize(configuracion, JsonOpcionesIndentadas);
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
