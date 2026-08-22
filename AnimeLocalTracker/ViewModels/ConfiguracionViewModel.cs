using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AnimeLocalTracker.ViewModels;

public partial class ConfiguracionViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly IUpdateService _updateService;
    private readonly IAuthService _authService;
    private readonly IDatabaseService _databaseService;
    private readonly IDialogService _dialogService;

    // === ALMACENAMIENTO ===
    [ObservableProperty] private string _rutaBaseAnimes = string.Empty;
    [ObservableProperty] private string _espacioLibreTexto = "Calculando...";
    [ObservableProperty] private int _totalAnimesBiblioteca = 0;

    // === REPRODUCCIÓN Y DESCARGAS ===
    [ObservableProperty] private bool _autoPlaySiguiente = true;
    [ObservableProperty] private bool _subtitulosPorDefecto = true;
    [ObservableProperty] private int _descargasSimultaneas = 3;
    [ObservableProperty] private int _intervaloSincronizacionMinutos = 5;

    // === AUTENTICACIÓN ANILIST ===
    [ObservableProperty] private bool _estaAutenticadoAniList;
    [ObservableProperty] private string _estadoAutenticacionTexto = "No conectado";

    // === ACERCA DE Y NOVEDADES DESDE GITHUB (CACHÉ LOCAL) ===
    public string VersionAppTexto => _updateService.ObtenerVersionActual();
    public string RepositorioUrl => "https://github.com/rodriguezrobinj/AnimeLocalTracker";
    public string AutorTexto => "Robin Rodriguez";
    public string LicenciaTexto => "Licencia MIT - Software de Código Abierto";

    [ObservableProperty] private string _tituloVersionTexto = "AnimeLocalTracker";
    [ObservableProperty] private string _fechaVersionTexto = string.Empty;
    [ObservableProperty] private string _novedadesTexto = "• Gestor y reproductor nativo multimedia para colecciones de anime locales.\n• Auto-tracking local e integración bidireccional con AniList.\n• Motor acelerado por hardware con Flyleaf y DirectX.\n• Actualizaciones automáticas con Velopack y GitHub Releases.";
    [ObservableProperty] private bool _isCargandoNovedades = false;

    public ConfiguracionViewModel(
        ISettingsService settingsService,
        IUpdateService updateService,
        IAuthService authService,
        IDatabaseService databaseService,
        IDialogService dialogService)
    {
        _settingsService = settingsService;
        _updateService = updateService;
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
        SubtitulosPorDefecto = config.SubtitulosPorDefecto;
        DescargasSimultaneas = config.DescargasSimultaneas;
        IntervaloSincronizacionMinutos = config.IntervaloSincronizacionMinutos;

        CalcularEspacioDisco(RutaBaseAnimes);
        _ = ActualizarEstadisticasBibliotecaAsync();
        ActualizarEstadoAutenticacion();
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
            AppLogger.Debug("ConfiguracionViewModel", $"Error cargando novedades: {ex.Message}");
        }
        finally
        {
            IsCargandoNovedades = false;
        }
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
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Selecciona la carpeta donde guardarás tus colecciones de anime",
                InitialDirectory = Directory.Exists(RutaBaseAnimes) ? RutaBaseAnimes : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
            };

            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
            {
                string nuevaRuta = dialog.FolderName;
                await _settingsService.EstablecerRutaBaseAnimesAsync(nuevaRuta);
                RutaBaseAnimes = nuevaRuta;
                CalcularEspacioDisco(nuevaRuta);

                await _dialogService.MostrarDialogoAsync(
                    "Almacenamiento Actualizado",
                    $"La carpeta principal de animes se ha configurado a:\n{nuevaRuta}",
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
    public async Task BuscarActualizacionesAsync()
    {
        await _updateService.ComprobarActualizacionesAsync(esManual: true);
        await CargarNovedadesAsync(forzar: true);
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
            AppLogger.Error("ConfiguracionViewModel", "Error abriendo repositorio en navegador", ex);
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
