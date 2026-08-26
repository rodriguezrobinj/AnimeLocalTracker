using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Core.Messages;
using AnimeLocalTracker.Core.Models;
using AnimeLocalTracker.Core.Services;
using CommunityToolkit.Mvvm.Messaging;
using Velopack;
using Velopack.Sources;

namespace AnimeLocalTracker.Avalonia.Services;

/// <summary>
/// Servicio de actualizaciones completo (portado del WPF): GitHub Releases + Velopack.
/// En modo desarrollo se degrada a información de versión desde el ensamblado.
/// </summary>
public class UpdateService : IUpdateService
{
    private const string RepoUrl = "https://github.com/rodriguezrobinj/AnimeLocalTracker";
    private const string ReleasesApiUrl = "https://api.github.com/repos/rodriguezrobinj/AnimeLocalTracker/releases/latest";

    private readonly IDialogService _dialogService;
    private readonly HttpClient _httpClient;
    private readonly string _releaseCachePath;
    private readonly string _notasVersionCachePath;
    private UpdateManager? _updateManager;
    private CancellationTokenSource? _backgroundCts;
    private bool _isUpdating = false;

    public UpdateService(IDialogService dialogService, HttpClient? httpClient = null)
    {
        _dialogService = dialogService;
        _httpClient = httpClient ?? new HttpClient();

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var folder = Path.Combine(appData, "AnimeLocalTracker");
        Directory.CreateDirectory(folder);
        _releaseCachePath = Path.Combine(folder, "release_info.json");
        _notasVersionCachePath = Path.Combine(folder, "notas_version_cache.json");

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
            // En modo desarrollo no existe VelopackLocator; se desactiva silenciosamente
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
                        "Actualizaciones",
                        "Estás usando la versión más reciente de AnimeLocalTracker. ¡Nada pendiente por instalar!",
                        false,
                        "CheckCircle",
                        "#4CAF50");
                }
                return null;
            }

            if (esManual)
            {
                string nuevaVersion = updateInfo.TargetFullRelease?.Version.ToNormalizedString() ?? "nueva versión";
                _ = _dialogService.MostrarDialogoAsync(
                    "Nueva versión disponible",
                    $"Se encontró la versión {nuevaVersion}. ¿Deseas descargarla e instalarla ahora?",
                    true,
                    "Update",
                    "#2196F3");
            }

            return updateInfo;
        }
        catch (Exception ex)
        {
            AppLogger.Error("UpdateService", "Error al comprobar actualizaciones", ex);
            if (esManual)
            {
                _ = _dialogService.MostrarDialogoAsync(
                    "Error de actualización",
                    $"No se pudo consultar GitHub Releases:\n{ex.Message}",
                    false,
                    "AlertCircle",
                    "#E53935");
            }
            return null;
        }
    }

    public async Task<bool> DescargarActualizacionAsync(UpdateInfo updateInfo, Action<int>? onProgreso = null)
    {
        if (_updateManager == null || updateInfo == null) return false;

        if (_isUpdating) return false;
        _isUpdating = true;

        try
        {
            await _updateManager.DownloadUpdatesAsync(updateInfo, onProgreso);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("UpdateService", "Error al descargar actualización", ex);
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
            _updateManager.ApplyUpdatesAndRestart(updateInfo);
        }
        catch (Exception ex)
        {
            AppLogger.Error("UpdateService", "Error al aplicar actualización", ex);
        }
    }

    public void IniciarVerificacionSegundoPlano(TimeSpan intervalo)
    {
        _backgroundCts?.Cancel();
        _backgroundCts?.Dispose();
        _backgroundCts = new CancellationTokenSource();
        var token = _backgroundCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(intervalo, token);
                        if (token.IsCancellationRequested) break;

                        await ComprobarActualizacionesAsync(esManual: false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Debug("UpdateService", $"Error en ciclo de verificación: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("UpdateService", $"Excepción fatal en ciclo de verificación: {ex.Message}", ex);
            }
        });
    }

    public async Task<ReleaseInfo> ObtenerInfoUltimaVersionAsync(bool forzarActualizacion = false)
    {
        // Usar caché si está fresca (< 4 horas)
        if (!forzarActualizacion && File.Exists(_releaseCachePath))
        {
            try
            {
                var info = File.GetLastWriteTimeUtc(_releaseCachePath);
                if (DateTime.UtcNow - info < TimeSpan.FromHours(4))
                {
                    var cached = JsonSerializer.Deserialize<ReleaseInfo>(File.ReadAllText(_releaseCachePath));
                    if (cached != null)
                    {
                        return cached;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug("UpdateService", $"Error leyendo caché de release: {ex.Message}");
            }
        }

        try
        {
            InitializeLibVlcSafe();
            var response = await _httpClient.GetAsync(ReleasesApiUrl);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                var release = JsonSerializer.Deserialize<GhostRelease>(json);
                if (release != null)
                {
                    var info = new ReleaseInfo
                    {
                        Version = "v" + (release.tag_name ?? "0.0.0"),
                        Titulo = release.name ?? "AnimeLocalTracker",
                        NotasVersion = "Portado a Avalonia. Esta build muestra la versión del ensamblado de acuerdo al entorno de ejecución.",
                        UrlRelease = release.html_url ?? RepoUrl
                    };
                    try
                    {
                        File.WriteAllText(_releaseCachePath, JsonSerializer.Serialize(info));
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Debug("UpdateService", $"Error guardando caché de release: {ex.Message}");
                    }
                    return info;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("UpdateService", $"Error al obtener info de última versión: {ex.Message}");
        }

        // Degradar a información del ensamblado si no hay red
        return new ReleaseInfo
        {
            Version = ObtenerVersionActual(),
            Titulo = "AnimeLocalTracker",
            NotasVersion = "No se pudo consultar GitHub Releases en este momento.",
            UrlRelease = RepoUrl
        };
    }

    private static void InitializeLibVlcSafe()
    {
        // No-op: evita invocación de native libs en CI cuando se pasa net8.0 sin libvlc; solo se registra para compatibilidad
    }

    private class GhostRelease
    {
        public string? tag_name { get; set; }
        public string? name { get; set; }
        public string? html_url { get; set; }
    }
}
