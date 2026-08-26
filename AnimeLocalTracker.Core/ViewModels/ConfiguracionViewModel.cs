using AnimeLocalTracker.Core.Services;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using AnimeLocalTracker.Core.Messages;
using AnimeLocalTracker.Core.Models;
using AnimeLocalTracker.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AnimeLocalTracker.Core.ViewModels;

public partial class ConfiguracionViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly IAuthService _authService;
    private readonly IDatabaseService _databaseService;
    private readonly IDialogService _dialogService;

    // === ALMACENAMIENTO ===
    [ObservableProperty] private string _rutaBaseAnimes = string.Empty;
    [ObservableProperty] private string _espacioLibreTexto = "Calculando...";
    [ObservableProperty] private int _totalAnimesBiblioteca = 0;

    // === REPRODUCCIÓN Y DESCARGAS ===
    [ObservableProperty] private bool _autoPlaySiguiente = true;
    [ObservableProperty] private bool _autoSkipIntroOutro = false;
    [ObservableProperty] private bool _subtitulosPorDefecto = true;
    [ObservableProperty] private int _descargasSimultaneas = 3;
    [ObservableProperty] private int _intervaloSincronizacionMinutos = 5;

    // === AUTENTICACIÓN ANILIST ===
    [ObservableProperty] private bool _estaAutenticadoAniList;
    [ObservableProperty] private string _estadoAutenticacionTexto = "No conectado";

    public ConfiguracionViewModel(
        ISettingsService settingsService,
        IAuthService authService,
        IDatabaseService databaseService,
        IDialogService dialogService)
    {
        _settingsService = settingsService;
        _authService = authService;
        _databaseService = databaseService;
        _dialogService = dialogService;

        CargarDatosConfiguracion();
    }

    public void CargarDatosConfiguracion()
    {
        var config = _settingsService?.ObtenerConfiguracion() ?? new AppSettings();
        RutaBaseAnimes = config.RutaBaseAnimes ?? string.Empty;
        AutoPlaySiguiente = config.AutoPlaySiguiente;
        AutoSkipIntroOutro = config.AutoSkipIntroOutro;
        SubtitulosPorDefecto = config.SubtitulosPorDefecto;
        DescargasSimultaneas = config.DescargasSimultaneas;
        IntervaloSincronizacionMinutos = config.IntervaloSincronizacionMinutos;

        CalcularEspacioDisco(RutaBaseAnimes);
        _ = ActualizarEstadisticasBibliotecaAsync();
        ActualizarEstadoAutenticacion();
    }

    private void ActualizarEstadoAutenticacion()
    {
        EstaAutenticadoAniList = _authService?.EstaAutenticado() ?? false;
        EstadoAutenticacionTexto = EstaAutenticadoAniList 
            ? "Conectado con AniList (Sincronización activa)" 
            : "Sesión no iniciada (Modo Local Offline)";
    }

    private async Task ActualizarEstadisticasBibliotecaAsync()
    {
        try
        {
            if (_databaseService != null)
            {
                var animes = await _databaseService.ObtenerTodosLosAnimesAsync();
                TotalAnimesBiblioteca = animes?.Count ?? 0;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ConfiguracionViewModel", $"Error obteniendo conteo de animes: {ex.Message}");
        }
    }

    public void CalcularEspacioDisco(string ruta)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ruta))
            {
                EspacioLibreTexto = "Ruta no configurada";
                return;
            }

            var root = Path.GetPathRoot(ruta);
            if (string.IsNullOrWhiteSpace(root))
            {
                EspacioLibreTexto = "Desconocido";
                return;
            }

            var drive = new DriveInfo(root);
            if (drive.IsReady)
            {
                double gbLibres = drive.AvailableFreeSpace / (1024.0 * 1024 * 1024);
                double gbTotales = drive.TotalSize / (1024.0 * 1024 * 1024);
                string etiqueta = !string.IsNullOrWhiteSpace(drive.VolumeLabel) ? $" ({drive.VolumeLabel})" : string.Empty;
                EspacioLibreTexto = $"{gbLibres:F1} GB libres de {gbTotales:F1} GB{etiqueta}";
            }
            else
            {
                EspacioLibreTexto = "Unidad no disponible";
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ConfiguracionViewModel", $"Error al calcular espacio en disco: {ex.Message}");
            EspacioLibreTexto = "Información no disponible";
        }
    }

    [RelayCommand]
    public async Task SeleccionarCarpetaAnimesAsync()
    {
        try
        {
            var initDir = System.IO.Directory.Exists(RutaBaseAnimes) ? RutaBaseAnimes : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            var ruta = _dialogService.SeleccionarCarpeta("Selecciona la carpeta donde guardarás tus colecciones de anime", initDir);
            
            if (!string.IsNullOrEmpty(ruta))
            {
                await _settingsService.EstablecerRutaBaseAnimesAsync(ruta);
                RutaBaseAnimes = ruta;
                CalcularEspacioDisco(ruta);

                await _dialogService.MostrarDialogoAsync(
                    "Almacenamiento Actualizado",
                    $"La carpeta principal de animes se ha configurado a:\n{ruta}",
                    false,
                    "FolderCheck",
                    "#4CAF50");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("ConfiguracionViewModel", "Error al seleccionar carpeta de animes", ex);
            await _dialogService.MostrarDialogoAsync(
                "Error",
                $"No se pudo cambiar la carpeta de almacenamiento: {ex.Message}",
                false,
                "AlertCircle",
                "#E53935");
        }
    }

    [RelayCommand]
    public void AbrirCarpetaEnExplorador()
    {
        try
        {
            if (Directory.Exists(RutaBaseAnimes))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = RutaBaseAnimes,
                    UseShellExecute = true
                });
            }
            else
            {
                _ = _dialogService.MostrarDialogoAsync(
                    "Carpeta no encontrada",
                    $"La carpeta especificada no existe en disco:\n{RutaBaseAnimes}",
                    false,
                    "FolderAlert",
                    "#FFA000");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("ConfiguracionViewModel", "Error abriendo carpeta en explorador", ex);
        }
    }

    [RelayCommand]
    public async Task GuardarPreferenciasAsync()
    {
        try
        {
            var config = _settingsService.ObtenerConfiguracion();
            config.AutoPlaySiguiente = AutoPlaySiguiente;
            config.AutoSkipIntroOutro = AutoSkipIntroOutro;
            config.SubtitulosPorDefecto = SubtitulosPorDefecto;
            config.DescargasSimultaneas = DescargasSimultaneas;
            config.IntervaloSincronizacionMinutos = IntervaloSincronizacionMinutos;

            await _settingsService.GuardarConfiguracionAsync(config);

            await _dialogService.MostrarDialogoAsync(
                "Preferencias Guardadas",
                "Tus preferencias han sido guardadas correctamente.",
                false,
                "CheckCircle",
                "#4CAF50");
        }
        catch (Exception ex)
        {
            AppLogger.Error("ConfiguracionViewModel", "Error guardando preferencias", ex);
        }
    }

    [RelayCommand]
    public async Task CerrarSesionAniListAsync()
    {
        bool confirmar = await _dialogService.MostrarDialogoAsync(
            "Cerrar Sesión",
            "¿Deseas desconectar tu cuenta de AniList? La aplicación continuará funcionando en modo offline local.",
            true,
            "Logout",
            "#F44336");

        if (confirmar)
        {
            _authService.CerrarSesion();
            ActualizarEstadoAutenticacion();
            WeakReferenceMessenger.Default.Send(new UsuarioDesconectadoMensaje());
        }
    }
}
