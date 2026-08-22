using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Models;
using CommunityToolkit.Mvvm.Messaging;
using Velopack;
using Velopack.Sources;

namespace AnimeLocalTracker.Services;

public class UpdateService : IUpdateService
{
    private const string RepoUrl = "https://github.com/rodriguezrobinj/AnimeLocalTracker";
    private const string ReleasesApiUrl = "https://api.github.com/repos/rodriguezrobinj/AnimeLocalTracker/releases/latest";
    
    private readonly IDialogService _dialogService;
    private readonly HttpClient _httpClient;
    private readonly string _releaseCachePath;
    private UpdateManager? _updateManager;
    private CancellationTokenSource? _backgroundCts;
    private bool _isUpdating = false;

    public UpdateService(IDialogService dialogService, HttpClient? httpClient = null)
    {
        _dialogService = dialogService;
        _httpClient = httpClient ?? new HttpClient();

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var folder = Path.Combine(appData, "AnimeLocalTracker");
        Directory.CreateDirectory(folder);
        _releaseCachePath = Path.Combine(folder, "release_info.json");

        InicializarManager();
    }

    private void InicializarManager()
    {
        try
        {
            var source = new GithubSource(RepoUrl, null, false);
            _updateManager = new UpdateManager(source);
        }
        catch (Exception)
        {
            // En modo desarrollo o entorno de pruebas no existe VelopackLocator; se desactiva silenciosamente
            _updateManager = null;
        }
    }

    public string ObtenerVersionActual()
    {
        try
        {
            if (_updateManager != null && _updateManager.IsInstalled && _updateManager.CurrentVersion != null)
            {
                return _updateManager.CurrentVersion.ToNormalizedString();
            }

            var asmVersion = Assembly.GetEntryAssembly()?.GetName().Version;
            if (asmVersion != null)
            {
                return $"v{asmVersion.Major}.{asmVersion.Minor}.{asmVersion.Build}";
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("UpdateService", $"Error obteniendo versión actual: {ex.Message}");
        }

        return "v1.0.0";
    }

    public bool EstaInstaladoPorVelopack()
    {
        try
        {
            return _updateManager != null && _updateManager.IsInstalled;
        }
        catch
        {
            return false;
        }
    }

    public async Task<UpdateInfo?> ComprobarActualizacionesAsync(bool esManual = false)
    {
        // Actualizar caché de información de release en segundo plano
        _ = ObtenerInfoUltimaVersionAsync(forzarActualizacion: true);

        if (_updateManager == null || !_updateManager.IsInstalled)
        {
            AppLogger.Info("UpdateService", "Comprobación omitida: la aplicación no está instalada vía Velopack (Modo Desarrollo).");
            if (esManual)
            {
                _ = _dialogService.MostrarDialogoAsync(
                    "Actualizaciones",
                    $"Estás en modo de desarrollo ({ObtenerVersionActual()}). Las actualizaciones automáticas se habilitan al compilar con el instalador de producción.",
                    false,
                    "CodeTags",
                    "#9C27B0");
            }
            return null;
        }

        try
        {
            AppLogger.Info("UpdateService", "Comprobando nuevas versiones en GitHub Releases...");
            var updateInfo = await _updateManager.CheckForUpdatesAsync();

            if (updateInfo == null)
            {
                AppLogger.Info("UpdateService", "La aplicación ya cuenta con la versión más reciente.");
                if (esManual)
                {
                    _ = _dialogService.MostrarDialogoAsync(
                        "Al día",
                        $"Ya tienes la última versión instalada ({ObtenerVersionActual()}).",
                        false,
                        "CheckCircle",
                        "#4CAF50");
                }
                return null;
            }

            AppLogger.Info("UpdateService", $"Nueva versión detectada: {updateInfo.TargetFullRelease?.Version}");
            return updateInfo;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("UpdateService", $"Error consultando actualizaciones en GitHub: {ex.Message}");
            if (esManual)
            {
                _ = _dialogService.MostrarDialogoAsync(
                    "Error de conexión",
                    "No se pudo consultar el servidor de actualizaciones en GitHub. Comprueba tu conexión a internet.",
                    false,
                    "AlertCircleOutline",
                    "#E53935");
            }
            return null;
        }
    }

    public async Task<bool> DescargarActualizacionAsync(UpdateInfo updateInfo, Action<int>? onProgreso = null)
    {
        if (_updateManager == null || updateInfo == null || _isUpdating) return false;

        try
        {
            _isUpdating = true;
            string targetVersion = updateInfo.TargetFullRelease?.Version.ToNormalizedString() ?? "nueva versión";
            AppLogger.Info("UpdateService", $"Iniciando descarga en segundo plano de la versión {targetVersion}...");

            await _updateManager.DownloadUpdatesAsync(updateInfo, p => onProgreso?.Invoke(p));

            AppLogger.Info("UpdateService", $"Descarga de la versión {targetVersion} completada con éxito.");

            // Actualizar info local de la versión
            _ = ObtenerInfoUltimaVersionAsync(forzarActualizacion: true);

            // Notificación al usuario
            _ = WeakReferenceMessenger.Default.Send(new MostrarDialogoRequestMessage(
                "Actualización lista",
                $"La versión {targetVersion} se ha descargado y está lista para aplicarse.",
                false,
                "Update",
                "#4CAF50"));

            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("UpdateService", $"Error descargando la actualización: {ex.Message}");
            return false;
        }
        finally
        {
            _isUpdating = false;
        }
    }

    public void AplicarActualizacionYReiniciar(UpdateInfo updateInfo)
    {
        if (_updateManager == null || updateInfo == null) return;

        try
        {
            AppLogger.Info("UpdateService", "Reiniciando aplicación para aplicar actualización...");
            _updateManager.ApplyUpdatesAndRestart(updateInfo);
        }
        catch (Exception ex)
        {
            AppLogger.Error("UpdateService", "Error aplicando actualización y reiniciando", ex);
        }
    }

    public void IniciarVerificacionSegundoPlano(TimeSpan intervalo)
    {
        if (!EstaInstaladoPorVelopack()) return;

        _backgroundCts?.Cancel();
        _backgroundCts?.Dispose();
        _backgroundCts = new CancellationTokenSource();
        var token = _backgroundCts.Token;

        _ = Task.Run(async () =>
        {
            // Esperar 15 segundos después de que inicie la app para no competir con el arranque
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var updateInfo = await ComprobarActualizacionesAsync(esManual: false);
                    if (updateInfo != null)
                    {
                        await DescargarActualizacionAsync(updateInfo);
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("UpdateService", $"Excepción en ciclo de actualización automática: {ex.Message}");
                }

                try
                {
                    await Task.Delay(intervalo, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, token);
    }

    public async Task<ReleaseInfo> ObtenerInfoUltimaVersionAsync(bool forzarActualizacion = false)
    {
        // 1. Si no se fuerza la actualización y existe la caché local, usarla de inmediato (sin llamadas a internet)
        if (!forzarActualizacion && File.Exists(_releaseCachePath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(_releaseCachePath);
                var cached = JsonSerializer.Deserialize<ReleaseInfo>(json);
                if (cached != null && !string.IsNullOrWhiteSpace(cached.NotasVersion))
                {
                    return cached;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug("UpdateService", $"No se pudo leer caché local de release: {ex.Message}");
            }
        }

        // 2. Si no hay caché o se forzó, consultar GitHub API
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApiUrl);
            request.Headers.UserAgent.ParseAdd("AnimeLocalTracker-App");

            var response = await _httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;

                var releaseInfo = new ReleaseInfo
                {
                    Version = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() ?? ObtenerVersionActual() : ObtenerVersionActual(),
                    Titulo = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "Actualización de Versión" : "Actualización de Versión",
                    NotasVersion = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? string.Empty : string.Empty,
                    UrlRelease = root.TryGetProperty("html_url", out var urlProp) ? urlProp.GetString() ?? RepoUrl : RepoUrl
                };

                if (root.TryGetProperty("published_at", out var pubProp) && pubProp.TryGetDateTime(out var dt))
                {
                    releaseInfo.FechaPublicacion = dt;
                }

                if (string.IsNullOrWhiteSpace(releaseInfo.NotasVersion))
                {
                    releaseInfo.NotasVersion = "• Mejoras generales de estabilidad y optimización de rendimiento.\n• Corrección de errores en la reproducción y sincronización.";
                }

                // Guardar en la caché local para arranques posteriores offline
                try
                {
                    var serialized = JsonSerializer.Serialize(releaseInfo, new JsonSerializerOptions { WriteIndented = true });
                    await File.WriteAllTextAsync(_releaseCachePath, serialized);
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("UpdateService", $"Error guardando caché de release local: {ex.Message}");
                }

                return releaseInfo;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("UpdateService", $"No se pudo consultar GitHub Releases API: {ex.Message}");
        }

        // 3. Fallback: Si hay caché previa aunque haya fallado la conexión actual
        if (File.Exists(_releaseCachePath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(_releaseCachePath);
                var cached = JsonSerializer.Deserialize<ReleaseInfo>(json);
                if (cached != null) return cached;
            }
            catch { }
        }

        // 4. Fallback por defecto de la versión instalada
        return new ReleaseInfo
        {
            Version = ObtenerVersionActual(),
            Titulo = "AnimeLocalTracker",
            NotasVersion = "• Gestor y reproductor nativo multimedia para colecciones de anime locales.\n• Auto-tracking local y sincronización bidireccional con AniList.\n• Motor multimedia acelerado por hardware con Flyleaf y DirectX.\n• Sistema de actualizaciones automáticas integrado con GitHub Releases."
        };
    }
}
