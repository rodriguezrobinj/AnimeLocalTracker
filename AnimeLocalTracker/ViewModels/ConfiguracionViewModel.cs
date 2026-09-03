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
    private readonly IAuthService _authService;
    private readonly IDatabaseService _databaseService;
    private readonly IDialogService _dialogService;
    private readonly CacheMaintenanceService _cacheMaintenanceService;

    // === ALMACENAMIENTO ===
    [ObservableProperty] private string _rutaBaseAnimes = string.Empty;
    [ObservableProperty] private string _espacioLibreTexto = "Calculando...";
    [ObservableProperty] private int _totalAnimesBiblioteca = 0;

    /// <summary>Contador localizado: "12 animes registrados" / "12 registered anime".</summary>
    public string TotalAnimesTexto => $"{TotalAnimesBiblioteca} {LocalizationService.T("Cfg_TotalAnimes")}";

    partial void OnTotalAnimesBibliotecaChanged(int value) => OnPropertyChanged(nameof(TotalAnimesTexto));

    // === REPRODUCCIÓN Y DESCARGAS ===
    [ObservableProperty] private bool _autoPlaySiguiente = true;
    [ObservableProperty] private bool _autoSkipIntroOutro = false;
    [ObservableProperty] private bool _subtitulosPorDefecto = true;
    [ObservableProperty] private int _descargasSimultaneas = 3;
    [ObservableProperty] private int _intervaloSincronizacionMinutos = 5;

    // === PREFERENCIAS DE USUARIO ===
    [ObservableProperty] private int _umbralMarcadoVisto = 95;
    [ObservableProperty] private bool _notificarNuevosEpisodios = true;
    [ObservableProperty] private string _idioma = "es";
    [ObservableProperty] private double _velocidadReproduccionDefecto = 1.0;

    /// <summary>Atajos de teclado configurables (acción → tecla). Se enlaza por índice desde XAML.</summary>
    public Dictionary<string, string> Atajos { get; set; } = new();

    partial void OnVelocidadReproduccionDefectoChanged(double value)
        => VelocidadReproduccionTexto = value.ToString("0.##x");

    [ObservableProperty] private string _velocidadReproduccionTexto = "1x";

    /// <summary>Texto visible del idioma seleccionado ("Español"/"English").</summary>
    public string IdiomaTexto => Idioma == "en" ? "English" : "Español";

    partial void OnIdiomaChanged(string value)
    {
        // Aplicar el idioma al instante: los bindings de la UI se refrescan solos
        LocalizationService.Instance.Idioma = value;
        OnPropertyChanged(nameof(IdiomaTexto));
        // LOC-04: el contador compone el texto localizado de forma no reactiva → refrescarlo aquí
        OnPropertyChanged(nameof(TotalAnimesTexto));
    }

    // === AUTENTICACIÓN ANILIST ===
    [ObservableProperty] private bool _estaAutenticadoAniList;
    [ObservableProperty] private string _estadoAutenticacionTexto = "No conectado";

    public ConfiguracionViewModel(
        ISettingsService settingsService,
        IAuthService authService,
        IDatabaseService databaseService,
        IDialogService dialogService,
        CacheMaintenanceService cacheMaintenanceService)
    {
        _settingsService = settingsService;
        _authService = authService;
        _databaseService = databaseService;
        _dialogService = dialogService;
        _cacheMaintenanceService = cacheMaintenanceService;

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
        UmbralMarcadoVisto = config.UmbralMarcadoVisto is >= 1 and <= 100 ? config.UmbralMarcadoVisto : 90;
        NotificarNuevosEpisodios = config.NotificarNuevosEpisodios;
        Idioma = config.Idioma == "en" ? "en" : "es";
        VelocidadReproduccionDefecto = config.VelocidadReproduccionDefecto is >= 0.5 and <= 2.0 ? config.VelocidadReproduccionDefecto : 1.0;
        Atajos = config.Atajos ?? new Dictionary<string, string>();
        OnPropertyChanged(nameof(Atajos));

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

                // FUN-009: la carpeta base NO reubica los animes existentes — avisarlo de forma
                // explícita para que el usuario no crea que sus colecciones se movieron.
                await _dialogService.MostrarDialogoAsync(
                    "Almacenamiento Actualizado",
                    $"La carpeta principal de animes se ha configurado a:\n{nuevaRuta}\n\n" +
                    "Los animes ya existentes conservan su carpeta actual: para que la app los " +
                    "encuentre, deberán estar (o copiarse) dentro de la nueva carpeta base.",
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
            config.UmbralMarcadoVisto = Math.Clamp(UmbralMarcadoVisto, 1, 100);
            config.NotificarNuevosEpisodios = NotificarNuevosEpisodios;
            config.Idioma = Idioma == "en" ? "en" : "es";
            config.VelocidadReproduccionDefecto = VelocidadReproduccionDefecto is >= 0.5 and <= 2.0 ? VelocidadReproduccionDefecto : 1.0;
            config.Atajos = new Dictionary<string, string>(Atajos);

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

    /// <summary>
    /// PRI-01: borra TODOS los datos locales (biblioteca, historial, sesión de AniList,
    /// portadas, miniaturas, backups y logs) y cierra la aplicación. La lista de AniList
    /// en la nube NO se toca. Antes no existía ninguna forma de purgar los datos: ni la
    /// desinstalación los borraba (viven en %LocalAppData%\AnimeLocalTrackerData a propósito).
    /// </summary>
    [RelayCommand]
    public async Task BorrarTodosMisDatosAsync()
    {
        bool confirmarPrimero = await _dialogService.MostrarDialogoAsync(
            "Borrar todos mis datos",
            "Se eliminarán: tu biblioteca local (animes, historial de visionado y progreso), " +
            "la sesión de AniList, las portadas, miniaturas, copias de seguridad y logs.\n\n" +
            "Tu lista en la nube de AniList NO se toca. Esta acción NO se puede deshacer.",
            true,
            "DeleteForever",
            "#EF4444");

        if (!confirmarPrimero) return;

        bool confirmarSegundo = await _dialogService.MostrarDialogoAsync(
            "Última confirmación",
            "¿Borrar TODOS tus datos locales ahora? La aplicación se cerrará y, al abrirla " +
            "de nuevo, empezarás desde cero.",
            true,
            "Alert",
            "#EF4444");

        if (!confirmarSegundo) return;

        try
        {
            // 1) Sesión de AniList (token cifrado con DPAPI)
            _authService.CerrarSesion();

            // 2) Biblioteca local (tablas completas en una transacción)
            await _databaseService.VaciarBibliotecaAsync();

            // 3) Datos satélite en disco
            BorrarCarpetaSiExiste(AppDataPaths.CoversDir);
            BorrarCarpetaSiExiste(AppDataPaths.ThumbnailsDir);
            BorrarCarpetaSiExiste(Path.Combine(AppDataPaths.DataRoot, "Backups"));
            BorrarCarpetaSiExiste(AppDataPaths.LogsDir);
            BorrarArchivoSiExiste(AppDataPaths.TokenPath);
            BorrarArchivoSiExiste(Path.Combine(AppDataPaths.DataRoot, "episodios_notificados.json"));
        }
        catch (Exception ex)
        {
            AppLogger.Error("ConfiguracionViewModel", "Error al borrar todos los datos", ex);
            await _dialogService.MostrarDialogoAsync(
                "Error",
                $"No se pudieron borrar todos los datos: {ex.Message}",
                false,
                "AlertCircle",
                "#E53935");
            return;
        }

        await _dialogService.MostrarDialogoAsync(
            "Datos borrados",
            "Todos tus datos locales se han eliminado correctamente. La aplicación se cerrará.",
            false,
            "CheckCircle",
            "#4CAF50");

        // Cerrar para que el arranque siguiente reconstruya todo desde cero.
        var app = System.Windows.Application.Current;
        if (app != null)
        {
            var dispatcher = app.Dispatcher;
            if (!dispatcher.HasShutdownStarted)
            {
                dispatcher.Invoke(() => app!.Shutdown());
            }
        }
    }

    private static void BorrarCarpetaSiExiste(string directorio)
    {
        try
        {
            if (Directory.Exists(directorio))
            {
                Directory.Delete(directorio, recursive: true);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("ConfiguracionViewModel", $"No se pudo borrar '{directorio}': {ex.Message}");
        }
    }

    private static void BorrarArchivoSiExiste(string ruta)
    {
        try
        {
            if (File.Exists(ruta)) File.Delete(ruta);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("ConfiguracionViewModel", $"No se pudo borrar '{ruta}': {ex.Message}");
        }
    }

    /// <summary>
    /// Mantenimiento: elimina miniaturas y portadas de animes/episodios que ya no existen
    /// en la biblioteca (liberan espacio sin tocar ningún episodio).
    /// </summary>
    [RelayCommand]
    public async Task LimpiarCacheAsync()
    {
        bool confirmar = await _dialogService.MostrarDialogoAsync(
            "Limpiar caché de imágenes",
            "¿Eliminar miniaturas y portadas de animes que ya no existen en tu biblioteca?\n\nEsto libera espacio en disco sin borrar ningún episodio.",
            true,
            "Broom",
            "#F59E0B");

        if (!confirmar) return;

        try
        {
            var resultado = await _cacheMaintenanceService.LimpiarCacheHuerfanoAsync();

            await _dialogService.MostrarDialogoAsync(
                "Limpieza completada",
                $"Se liberaron {resultado.MegabytesLiberados:F1} MB\n" +
                $"{resultado.MiniaturasBorradas} miniaturas y {resultado.PortadasBorradas} portadas eliminadas.",
                false,
                "CheckCircleOutline",
                "#4CAF50");
        }
        catch (Exception ex)
        {
            AppLogger.Error("ConfiguracionViewModel", "Error limpiando caché de imágenes", ex);
            await _dialogService.MostrarDialogoAsync("Error", "No se pudo completar la limpieza de caché.", false, "AlertCircleOutline", "#EF4444");
        }
    }

    /// <summary>Backup manual con 1 clic: copia biblioteca.db a la ubicación elegida.</summary>
    [RelayCommand]
    public async Task ExportarBackupAsync()
    {
        var dialogo = new Microsoft.Win32.SaveFileDialog
        {
            Title = LocalizationService.T("Cfg_ExportarBackup"),
            Filter = "Base de datos SQLite (*.db)|*.db",
            FileName = $"biblioteca_backup_{DateTime.Now:yyyyMMdd_HHmmss}.db"
        };

        if (dialogo.ShowDialog() != true) return;

        bool ok = await _databaseService.ExportarCopiaSeguridadAsync(dialogo.FileName);
        await _dialogService.MostrarDialogoAsync(
            ok ? "OK" : "Error",
            ok ? LocalizationService.T("Cfg_BackupOk") : LocalizationService.T("Cfg_BackupError"),
            false, ok ? "CheckCircleOutline" : "AlertCircleOutline", ok ? "#4CAF50" : "#EF4444");
    }

    /// <summary>Exporta la biblioteca (animes + registros) a un JSON portable.</summary>
    [RelayCommand]
    public async Task ExportarBibliotecaAsync()
    {
        var dialogo = new Microsoft.Win32.SaveFileDialog
        {
            Title = LocalizationService.T("Cfg_ExportarBiblioteca"),
            Filter = "JSON (*.json)|*.json",
            FileName = $"biblioteca_{DateTime.Now:yyyyMMdd_HHmmss}.json"
        };

        if (dialogo.ShowDialog() != true) return;

        try
        {
            int animes = await _databaseService.ExportarBibliotecaJsonAsync(dialogo.FileName);
            await _dialogService.MostrarDialogoAsync("OK",
                string.Format(LocalizationService.T("Cfg_BibliotecaExportada"), animes),
                false, "CheckCircleOutline", "#4CAF50");
        }
        catch (Exception ex)
        {
            AppLogger.Error("ConfiguracionViewModel", "Error exportando biblioteca", ex);
            await _dialogService.MostrarDialogoAsync("Error", LocalizationService.T("Cfg_BackupError"), false, "AlertCircleOutline", "#EF4444");
        }
    }

    /// <summary>Restaura la biblioteca desde una copia de seguridad (.db) con validación de integridad (BAK-03).</summary>
    [RelayCommand]
    public async Task RestaurarBackupAsync()
    {
        bool confirmar = await _dialogService.MostrarDialogoAsync(
            LocalizationService.T("Cfg_RestaurarBackup"),
            LocalizationService.T("Cfg_RestaurarConfirmacion"),
            true, "Restore", "#F59E0B");
        if (!confirmar) return;

        var dialogo = new Microsoft.Win32.OpenFileDialog
        {
            Title = LocalizationService.T("Cfg_RestaurarBackup"),
            Filter = "Base de datos SQLite (*.db)|*.db"
        };
        if (dialogo.ShowDialog() != true) return;

        try
        {
            bool ok = await _databaseService.RestaurarCopiaSeguridadAsync(dialogo.FileName);
            if (ok)
            {
                CargarDatosConfiguracion(); // refrescar contador y espacio tras restaurar
            }
            await _dialogService.MostrarDialogoAsync(
                ok ? "OK" : "Error",
                ok ? LocalizationService.T("Cfg_RestaurarOk") : LocalizationService.T("Cfg_RestaurarError"),
                false, ok ? "CheckCircleOutline" : "AlertCircleOutline", ok ? "#4CAF50" : "#EF4444");
        }
        catch (Exception ex)
        {
            AppLogger.Error("ConfiguracionViewModel", "Error restaurando copia de seguridad", ex);
            await _dialogService.MostrarDialogoAsync("Error", LocalizationService.T("Cfg_RestaurarError"), false, "AlertCircleOutline", "#EF4444");
        }
    }

    /// <summary>Importa una biblioteca desde JSON, fusionándola con la existente.</summary>
    [RelayCommand]
    public async Task ImportarBibliotecaAsync()
    {
        bool confirmar = await _dialogService.MostrarDialogoAsync(
            LocalizationService.T("Cfg_ImportarBiblioteca"),
            LocalizationService.T("Cfg_ImportarConfirmacion"),
            true, "Import", "#F59E0B");
        if (!confirmar) return;

        var dialogo = new Microsoft.Win32.OpenFileDialog
        {
            Title = LocalizationService.T("Cfg_ImportarBiblioteca"),
            Filter = "JSON (*.json)|*.json"
        };

        if (dialogo.ShowDialog() != true) return;

        try
        {
            int animes = await _databaseService.ImportarBibliotecaJsonAsync(dialogo.FileName);
            await _dialogService.MostrarDialogoAsync("OK",
                string.Format(LocalizationService.T("Cfg_BibliotecaImportada"), animes),
                false, "CheckCircleOutline", "#4CAF50");
        }
        catch (Exception ex)
        {
            AppLogger.Error("ConfiguracionViewModel", "Error importando biblioteca", ex);
            await _dialogService.MostrarDialogoAsync("Error", LocalizationService.T("Cfg_BackupError"), false, "AlertCircleOutline", "#EF4444");
        }
    }
}
