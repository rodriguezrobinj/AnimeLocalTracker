using System;
using System.Diagnostics;
using System.Threading.Tasks;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AnimeLocalTracker.ViewModels;

public partial class AcercaDeViewModel : ObservableObject
{
    private readonly IUpdateService _updateService;
    private readonly IDialogService _dialogService;

    // === INFORMACIÓN DE LA APLICACIÓN ===
    public string VersionAppTexto => _updateService?.ObtenerVersionActual() ?? "1.0.0";
    public string RepositorioUrl => "https://github.com/rodriguezrobinj/AnimeLocalTracker";
    public string AutorTexto => "Robin Rodriguez";
    public string LicenciaTexto => "Licencia MIT - Software de Código Abierto";

    [ObservableProperty] private string _tituloVersionTexto = "AnimeLocalTracker";
    [ObservableProperty] private string _fechaVersionTexto = string.Empty;
    [ObservableProperty] private string _novedadesTexto = "• Gestor y reproductor nativo multimedia para colecciones de anime locales.\n• Auto-tracking local e integración bidireccional con AniList.\n• Motor acelerado por hardware con Flyleaf y DirectX.\n• Actualizaciones automáticas con Velopack y GitHub Releases.";
    [ObservableProperty] private bool _isCargandoNovedades = false;

    public AcercaDeViewModel(
        IUpdateService updateService,
        IDialogService dialogService)
    {
        _updateService = updateService;
        _dialogService = dialogService;

        _ = CargarNovedadesAsync();
    }

    public async Task CargarNovedadesAsync(bool forzar = false)
    {
        try
        {
            IsCargandoNovedades = true;
            if (_updateService != null)
            {
                var release = await _updateService.ObtenerInfoUltimaVersionAsync(forzarActualizacion: forzar);
                if (release != null)
                {
                    TituloVersionTexto = !string.IsNullOrWhiteSpace(release.Titulo) ? release.Titulo : "AnimeLocalTracker";
                    NovedadesTexto = !string.IsNullOrWhiteSpace(release.NotasVersion) ? release.NotasVersion : NovedadesTexto;
                    FechaVersionTexto = release.FechaPublicacion.HasValue
                        ? $"Publicado: {release.FechaPublicacion.Value:dd/MM/yyyy}"
                        : string.Empty;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("AcercaDeViewModel", $"Error cargando novedades: {ex.Message}");
        }
        finally
        {
            IsCargandoNovedades = false;
        }
    }

    [RelayCommand]
    public async Task BuscarActualizacionesAsync()
    {
        try
        {
            if (_updateService == null) return;

            var update = await _updateService.ComprobarActualizacionesAsync(esManual: true);
            if (update != null)
            {
                string nuevaVersion = update.TargetFullRelease?.Version.ToNormalizedString() ?? "nueva versión";
                bool confirmar = await _dialogService.MostrarDialogoAsync(
                    "¡Actualización Disponible!",
                    $"Se encontró la versión {nuevaVersion}.\n\n¿Deseas descargarla e instalarla ahora automáticamente?",
                    true,
                    "DownloadCircle",
                    "#4CAF50");

                if (confirmar)
                {
                    bool descargado = await _updateService.DescargarActualizacionAsync(update);
                    if (descargado)
                    {
                        _updateService.AplicarActualizacionYReiniciar(update);
                    }
                }
            }
            else
            {
                await CargarNovedadesAsync(forzar: true);
                await _dialogService.MostrarDialogoAsync(
                    "Aplicación Actualizada",
                    $"Ya tienes instalada la versión más reciente ({VersionAppTexto}).",
                    false,
                    "CheckCircleOutline",
                    "#4CAF50");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("AcercaDeViewModel", "Error buscando actualizaciones", ex);
            await _dialogService.MostrarDialogoAsync(
                "Error de Actualización",
                $"No se pudo comprobar la actualización: {ex.Message}",
                false,
                "AlertCircleOutline",
                "#F44336");
        }
    }

    [RelayCommand]
    public void AbrirRepositorioGitHub()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = RepositorioUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLogger.Error("AcercaDeViewModel", "Error abriendo repositorio en navegador", ex);
        }
    }
}
