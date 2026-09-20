using System;
using System.Diagnostics;
using System.Threading.Tasks;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AnimeLocalTracker.ViewModels;

public partial class AcercaDeViewModel : ObservableObject, IRecipient<IdiomaCambiadoMensaje>
{
    private readonly IUpdateService _updateService;
    private readonly IDialogService _dialogService;

    // === INFORMACIÓN DE LA APLICACIÓN ===
    /// <summary>Siempre con prefijo "v": el servicio devuelve "1.0.5" instalado por Velopack pero
    /// "v1.0.5" en modo desarrollo, y la vista mostraba "vv1.0.5" en este último caso.</summary>
    public string VersionAppTexto
    {
        get
        {
            var version = _updateService?.ObtenerVersionActual() ?? "1.0.0";
            return version.StartsWith('v') || version.StartsWith('V') ? version : "v" + version;
        }
    }
    public string RepositorioUrl => "https://github.com/rodriguezrobinj/AnimeLocalTracker";
    public string LicenciaTexto => LocalizationService.T("Acerca_LicenciaTexto");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NovedadesTituloTexto))]
    private string _tituloVersionTexto = "AnimeLocalTracker";
    [ObservableProperty] private string _fechaVersionTexto = string.Empty;
    [ObservableProperty] private string _novedadesTexto = LocalizationService.T("Acerca_NovedadesDefault");
    [ObservableProperty] private bool _isCargandoNovedades = false;

    /// <summary>"Novedades: {título}" / "What's new: {title}" — no puede ser un StringFormat de
    /// XAML porque el prefijo también debe traducirse (LOC-08).</summary>
    public string NovedadesTituloTexto => string.Format(LocalizationService.T("Acerca_NovedadesTituloFormato"), TituloVersionTexto);

    public AcercaDeViewModel(
        IUpdateService updateService,
        IDialogService dialogService)
    {
        _updateService = updateService;
        _dialogService = dialogService;

        WeakReferenceMessenger.Default.Register<IdiomaCambiadoMensaje>(this);

        _ = CargarNovedadesAsync();
    }

    /// <summary>LOC-08: "Novedades: {título}" se compone en código, no vía binding a una
    /// clave de LocalizationService, así que no se refresca sola al cambiar de idioma.</summary>
    public void Receive(IdiomaCambiadoMensaje message) => OnPropertyChanged(nameof(NovedadesTituloTexto));

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
                        ? string.Format(LocalizationService.T("Acerca_PublicadoFormato"), release.FechaPublicacion.Value)
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
                string nuevaVersion = update.TargetFullRelease?.Version.ToNormalizedString() ?? LocalizationService.T("Msg_NuevaVersion");
                bool confirmar = await _dialogService.MostrarDialogoAsync(
                    LocalizationService.T("Acerca_ActualizacionDisponibleTitulo"),
                    string.Format(LocalizationService.T("Acerca_ActualizacionDisponibleMsj"), nuevaVersion),
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
                // null NO significa siempre "estás al día": con esManual el servicio ya avisó él
                // mismo del resultado real (modo desarrollo, al día o error de conexión). Mostrar
                // aquí otro "Aplicación actualizada" pisaba el aviso de modo desarrollo.
                await CargarNovedadesAsync(forzar: true);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("AcercaDeViewModel", "Error buscando actualizaciones", ex);
            await _dialogService.MostrarDialogoAsync(
                LocalizationService.T("Acerca_ErrorActualizacionTitulo"),
                string.Format(LocalizationService.T("Acerca_ErrorActualizacionMsj"), ex.Message),
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

    [RelayCommand]
    public void AbrirReportarProblema()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = $"{RepositorioUrl}/issues/new",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLogger.Error("AcercaDeViewModel", "Error abriendo GitHub Issues en navegador", ex);
        }
    }
}
