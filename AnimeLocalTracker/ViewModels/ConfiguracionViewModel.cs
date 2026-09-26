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
    private readonly IPluginService _pluginService;
    private readonly IStartupService? _startupService;

    // === PLUGINS ===
    [ObservableProperty] private System.Collections.ObjectModel.ObservableCollection<string> _pluginsInstalados = new();

    // SEC-01: los plugins son código de terceros con los permisos de la app. Apagados por defecto; cada archivo
    // requiere que el usuario confíe en él (huella SHA-256 fijada). Estas acciones se aplican al instante.
    [ObservableProperty] private bool _pluginsHabilitados;
    [ObservableProperty] private bool _pluginsRequiereReinicio;
    public System.Collections.ObjectModel.ObservableCollection<PluginItemViewModel> PluginsDetalle { get; } = new();
    private bool _cargandoPlugins;

    /// <summary>Icono del aviso al confiar en un plugin (tono ámbar de advertencia).</summary>
    internal const string IconoConfiarPlugin = "ShieldAlertOutline";

    partial void OnPluginsHabilitadosChanged(bool value)
    {
        if (_cargandoPlugins) return; // se está rellenando desde la configuración guardada
        _ = GuardarPluginsHabilitadosAsync(value);
    }

    private async Task GuardarPluginsHabilitadosAsync(bool valor)
    {
        try
        {
            var config = _settingsService.ObtenerConfiguracion();
            config.PluginsHabilitados = valor;
            await _settingsService.GuardarConfiguracionAsync(config);
            PluginsRequiereReinicio = true; // los .dll solo se cargan al iniciar la app
        }
        catch (Exception ex)
        {
            AppLogger.Error("ConfiguracionViewModel", "Error guardando el interruptor de plugins", ex);
        }
    }

    [RelayCommand]
    private async Task AlternarConfianzaPluginAsync(PluginItemViewModel? plugin)
    {
        if (plugin == null) return;

        bool confiar = !plugin.EsConfiable; // "Sin confiar" y "Modificado" → confiar; "Confiable" → retirar
        if (confiar)
        {
            // Confiar = ejecutar código de terceros con los permisos del usuario: pedir confirmación explícita.
            bool aceptado = await _dialogService.MostrarDialogoAsync(
                LocalizationService.T("Cfg_PluginConfiarTitulo"),
                string.Format(LocalizationService.T("Cfg_PluginConfiarMsj"), plugin.Nombre, plugin.HashCompleto),
                true,
                IconoConfiarPlugin,
                "#F59E0B");
            if (!aceptado) return;
        }

        if (await _pluginService.EstablecerConfianzaAsync(plugin.Nombre, confiar))
        {
            PluginsRequiereReinicio = true;
            CargarPlugins();
        }
    }

    // === ALMACENAMIENTO ===
    [ObservableProperty] private string _rutaBaseAnimes = string.Empty;
    [ObservableProperty] private string _espacioLibreTexto = LocalizationService.T("Cfg_EspacioCalculando");
    [ObservableProperty] private int _totalAnimesBiblioteca = 0;

    /// <summary>Contador localizado: "12 animes registrados" / "12 registered anime".</summary>
    public string TotalAnimesTexto => $"{TotalAnimesBiblioteca} {LocalizationService.T("Cfg_TotalAnimes")}";

    partial void OnTotalAnimesBibliotecaChanged(int value) => OnPropertyChanged(nameof(TotalAnimesTexto));

    // === REPRODUCCIÓN Y DESCARGAS ===
    [ObservableProperty] private bool _autoSkipIntroOutro = false;
    [ObservableProperty] private bool _subtitulosPorDefecto = true;
    [ObservableProperty] private int _descargasSimultaneas = 3;
    [ObservableProperty] private int _intervaloSincronizacionMinutos = 5;
    [ObservableProperty] private int _pasosSaltoSegundos = 10;
    [ObservableProperty] private bool _evitarSuspensionPantalla = true;
    [ObservableProperty] private string _accionFinEpisodio = AccionFinEpisodioValores.AutoPlayCuentaAtras;
    [ObservableProperty] private string _preferenciaAudioAnimeAv1 = "";
    [ObservableProperty] private string _servidorPreferidoAnimeAv1 = "";
    [ObservableProperty] private bool _busquedaTorrentHabilitada;
    [ObservableProperty] private string _grupoFansubPreferidoTorrent = "";
    [ObservableProperty] private string _resolucionPreferidaTorrent = "";
    [ObservableProperty] private bool _seguirSembrandoTorrents;

    // === ESTILO DE SUBTÍTULOS (la vista previa se recalcula sola al cambiar cualquiera) ===
    [ObservableProperty] private string _estiloFuente = EstiloSubtitulos.FuentePorDefecto;
    [ObservableProperty] private double _estiloTamano = 44;
    [ObservableProperty] private bool _estiloNegrita = true;
    [ObservableProperty] private bool _estiloCursiva;
    [ObservableProperty] private bool _estiloSubrayado;
    [ObservableProperty] private string _estiloColorTexto = "#FFFFFF";
    [ObservableProperty] private string _estiloColorContorno = "#000000";
    [ObservableProperty] private double _estiloGrosorContorno = 3;
    [ObservableProperty] private double _estiloOpacidadFondo;
    [ObservableProperty] private double _estiloGrosorBorde;
    [ObservableProperty] private string _estiloColorBorde = "#FFFFFF";
    [ObservableProperty] private EstiloSubtitulos _estiloVistaPrevia = new();

    /// <summary>Tipografías disponibles para los subtítulos.</summary>
    public IReadOnlyList<string> FuentesSubtitulos => EstiloSubtitulos.FuentesDisponibles;

    /// <summary>Paleta compartida por los tres selectores de color (texto, contorno y borde de la caja).</summary>
    public IReadOnlyList<ColorEstiloOpcion> ColoresSubtitulos { get; } =
        EstiloSubtitulos.Paleta.Select(c => new ColorEstiloOpcion(c.Clave, c.Hex)).ToList();

    private static readonly HashSet<string> PropiedadesDelEstilo = new()
    {
        nameof(EstiloFuente), nameof(EstiloTamano), nameof(EstiloNegrita), nameof(EstiloCursiva),
        nameof(EstiloSubrayado), nameof(EstiloColorTexto), nameof(EstiloColorContorno),
        nameof(EstiloGrosorContorno), nameof(EstiloOpacidadFondo), nameof(EstiloGrosorBorde), nameof(EstiloColorBorde)
    };

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName != null && PropiedadesDelEstilo.Contains(e.PropertyName))
        {
            EstiloVistaPrevia = ConstruirEstilo();
        }
    }

    /// <summary>Junta los valores de la pantalla en un estilo válido (los fuera de rango se corrigen).</summary>
    private EstiloSubtitulos ConstruirEstilo() => new EstiloSubtitulos
    {
        Fuente = EstiloFuente,
        Tamano = EstiloTamano,
        Negrita = EstiloNegrita,
        Cursiva = EstiloCursiva,
        Subrayado = EstiloSubrayado,
        ColorTexto = EstiloColorTexto,
        ColorContorno = EstiloColorContorno,
        GrosorContorno = EstiloGrosorContorno,
        OpacidadFondo = (int)Math.Round(EstiloOpacidadFondo),
        GrosorBorde = EstiloGrosorBorde,
        ColorBorde = EstiloColorBorde
    }.Normalizar();

    private void AplicarEstilo(EstiloSubtitulos? origen)
    {
        var e = (origen ?? new EstiloSubtitulos()).Normalizar();
        EstiloFuente = e.Fuente;
        EstiloTamano = e.Tamano;
        EstiloNegrita = e.Negrita;
        EstiloCursiva = e.Cursiva;
        EstiloSubrayado = e.Subrayado;
        EstiloColorTexto = e.ColorTexto;
        EstiloColorContorno = e.ColorContorno;
        EstiloGrosorContorno = e.GrosorContorno;
        EstiloOpacidadFondo = e.OpacidadFondo;
        EstiloGrosorBorde = e.GrosorBorde;
        EstiloColorBorde = e.ColorBorde;
        EstiloVistaPrevia = ConstruirEstilo();
    }

    /// <summary>Vuelve el estilo al de fábrica. Solo cambia la pantalla: se aplica al reproductor con "Guardar preferencias".</summary>
    [RelayCommand]
    private void RestablecerEstiloSubtitulos() => AplicarEstilo(new EstiloSubtitulos());

    // === PREFERENCIAS DE USUARIO ===
    [ObservableProperty] private int _umbralMarcadoVisto = 95;
    [ObservableProperty] private bool _notificarNuevosEpisodios = true;
    [ObservableProperty] private bool _minimizarABandejaAlCerrar = false;
    [ObservableProperty] private bool _notificarConBandejaSiempre = false;
    [ObservableProperty] private string _idioma = "es";
    [ObservableProperty] private double _velocidadReproduccionDefecto = 1.0;
    [ObservableProperty] private bool _iniciarConWindows = false;
    [ObservableProperty] private bool _teclaPanicoActiva = false;
    [ObservableProperty] private string _teclaPanico = "F12";

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
        foreach (var color in ColoresSubtitulos) color.Refrescar();
        foreach (var plugin in PluginsDetalle) plugin.RefrescarTextos();
        // LOC-08: avisa a los ViewModels con colecciones/texto compuesto en código (Galería,
        // Agregar Anime, Acerca de) para que se regeneren en el nuevo idioma.
        WeakReferenceMessenger.Default.Send(new IdiomaCambiadoMensaje());
    }

    // === AUTENTICACIÓN ANILIST ===
    [ObservableProperty] private bool _estaAutenticadoAniList;
    [ObservableProperty] private string _estadoAutenticacionTexto = LocalizationService.T("Cfg_NoConectado");

    // === NAVEGACIÓN POR CATEGORÍAS ===
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EsSeccionBiblioteca))]
    [NotifyPropertyChangedFor(nameof(EsSeccionReproduccion))]
    [NotifyPropertyChangedFor(nameof(EsSeccionAtajos))]
    [NotifyPropertyChangedFor(nameof(EsSeccionDescargas))]
    [NotifyPropertyChangedFor(nameof(EsSeccionGeneral))]
    [NotifyPropertyChangedFor(nameof(EsSeccionPlugins))]
    [NotifyPropertyChangedFor(nameof(MostrarBarraGuardar))]
    private SeccionConfiguracion _seccionActiva = SeccionConfiguracion.Biblioteca;

    public bool EsSeccionBiblioteca => SeccionActiva == SeccionConfiguracion.Biblioteca;
    public bool EsSeccionReproduccion => SeccionActiva == SeccionConfiguracion.Reproduccion;
    public bool EsSeccionAtajos => SeccionActiva == SeccionConfiguracion.Atajos;
    public bool EsSeccionDescargas => SeccionActiva == SeccionConfiguracion.Descargas;
    public bool EsSeccionGeneral => SeccionActiva == SeccionConfiguracion.General;
    public bool EsSeccionPlugins => SeccionActiva == SeccionConfiguracion.Plugins;

    /// <summary>"Guardar preferencias" solo tiene sentido en las categorías con preferencias; las de
    /// Biblioteca (carpeta, copias) y Plugins actúan al instante con sus propios botones.</summary>
    public bool MostrarBarraGuardar => SeccionActiva is SeccionConfiguracion.Reproduccion
        or SeccionConfiguracion.Atajos
        or SeccionConfiguracion.Descargas
        or SeccionConfiguracion.General;

    [RelayCommand]
    private void SeleccionarSeccion(SeccionConfiguracion seccion) => SeccionActiva = seccion;

    public ConfiguracionViewModel(
        ISettingsService settingsService,
        IAuthService authService,
        IDatabaseService databaseService,
        IDialogService dialogService,
        CacheMaintenanceService cacheMaintenanceService,
        IPluginService pluginService,
        IStartupService? startupService = null)
    {
        _settingsService = settingsService;
        _authService = authService;
        _databaseService = databaseService;
        _dialogService = dialogService;
        _cacheMaintenanceService = cacheMaintenanceService;
        _pluginService = pluginService;
        _startupService = startupService;

        CargarDatosConfiguracion();
    }

    public void CargarDatosConfiguracion()
    {
        var config = _settingsService?.ObtenerConfiguracion() ?? new AppSettings();
        RutaBaseAnimes = config.RutaBaseAnimes ?? string.Empty;
        AutoSkipIntroOutro = config.AutoSkipIntroOutro;
        SubtitulosPorDefecto = config.SubtitulosPorDefecto;
        AplicarEstilo(config.EstiloSubtitulos);
        DescargasSimultaneas = config.DescargasSimultaneas;
        IntervaloSincronizacionMinutos = config.IntervaloSincronizacionMinutos;
        PasosSaltoSegundos = config.PasosSaltoSegundos is 5 or 10 or 30 or 60 ? config.PasosSaltoSegundos : 10;
        EvitarSuspensionPantalla = config.EvitarSuspensionPantalla;
        AccionFinEpisodio = string.IsNullOrWhiteSpace(config.AccionFinEpisodio) ? AccionFinEpisodioValores.AutoPlayCuentaAtras : config.AccionFinEpisodio;
        PreferenciaAudioAnimeAv1 = config.PreferenciaAudioAnimeAv1 ?? "";
        ServidorPreferidoAnimeAv1 = config.ServidorPreferidoAnimeAv1 ?? "";
        BusquedaTorrentHabilitada = config.BusquedaTorrentHabilitada;
        GrupoFansubPreferidoTorrent = config.GrupoFansubPreferidoTorrent ?? "";
        ResolucionPreferidaTorrent = config.ResolucionPreferidaTorrent ?? "";
        SeguirSembrandoTorrents = config.SeguirSembrandoTorrents;
        UmbralMarcadoVisto = config.UmbralMarcadoVisto is >= 1 and <= 100 ? config.UmbralMarcadoVisto : 90;
        NotificarNuevosEpisodios = config.NotificarNuevosEpisodios;
        MinimizarABandejaAlCerrar = config.MinimizarABandejaAlCerrar;
        NotificarConBandejaSiempre = config.NotificarConBandejaSiempre;
        Idioma = config.Idioma == "en" ? "en" : "es";
        VelocidadReproduccionDefecto = config.VelocidadReproduccionDefecto is >= 0.5 and <= 2.0 ? config.VelocidadReproduccionDefecto : 1.0;
        // El registro de Windows es la fuente de verdad (no AppSettings): así se refleja si el
        // usuario lo desactivó desde el Administrador de tareas en vez de desde esta pantalla.
        IniciarConWindows = _startupService?.EstaHabilitado() ?? false;
        TeclaPanicoActiva = config.TeclaPanicoActiva;
        TeclaPanico = config.TeclaPanico == "Escape" ? "Escape" : "F12";
        Atajos = config.Atajos ?? new Dictionary<string, string>();
        OnPropertyChanged(nameof(Atajos));

        CalcularEspacioDisco(RutaBaseAnimes);
        _ = ActualizarEstadisticasBibliotecaAsync();
        ActualizarEstadoAutenticacion();
        _cargandoPlugins = true;
        PluginsHabilitados = config.PluginsHabilitados;
        _cargandoPlugins = false;
        CargarPlugins();
    }

    public void CargarPlugins()
    {
        try
        {
            PluginsInstalados.Clear();
            var plugins = _pluginService.ObtenerPluginsInstalados() ?? new List<string>();
            foreach (var plugin in plugins)
            {
                PluginsInstalados.Add(plugin);
            }

            PluginsDetalle.Clear();
            foreach (var info in _pluginService.ObtenerDetallesPlugins() ?? Array.Empty<PluginInfo>())
            {
                PluginsDetalle.Add(new PluginItemViewModel(info));
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("ConfiguracionViewModel", "Error cargando plugins", ex);
        }
    }

    private void ActualizarEstadoAutenticacion()
    {
        EstaAutenticadoAniList = _authService?.EstaAutenticado() ?? false;
        EstadoAutenticacionTexto = EstaAutenticadoAniList
            ? LocalizationService.T("Cfg_ConectadoSincronizacionActiva")
            : LocalizationService.T("Cfg_SesionNoIniciadaOffline");
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
                EspacioLibreTexto = LocalizationService.T("Cfg_RutaNoConfigurada");
                return;
            }

            var root = Path.GetPathRoot(ruta);
            if (string.IsNullOrWhiteSpace(root))
            {
                EspacioLibreTexto = LocalizationService.T("Cfg_Desconocido");
                return;
            }

            var drive = new DriveInfo(root);
            if (drive.IsReady)
            {
                double gbLibres = drive.AvailableFreeSpace / (1024.0 * 1024 * 1024);
                double gbTotales = drive.TotalSize / (1024.0 * 1024 * 1024);
                string etiqueta = !string.IsNullOrWhiteSpace(drive.VolumeLabel) ? $" ({drive.VolumeLabel})" : string.Empty;
                EspacioLibreTexto = string.Format(LocalizationService.T("Cfg_EspacioFormato"), gbLibres, gbTotales, etiqueta);
            }
            else
            {
                EspacioLibreTexto = LocalizationService.T("Cfg_UnidadNoDisponible");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ConfiguracionViewModel", $"Error al calcular espacio en disco: {ex.Message}");
            EspacioLibreTexto = LocalizationService.T("Cfg_EspacioInfoNoDisponible");
        }
    }

    [RelayCommand]
    public async Task SeleccionarCarpetaAnimesAsync()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = LocalizationService.T("Cfg_SeleccionarCarpetaTitulo"),
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
                    LocalizationService.T("Cfg_AlmacenamientoActualizadoTitulo"),
                    string.Format(LocalizationService.T("Cfg_AlmacenamientoActualizadoMsj"), nuevaRuta),
                    false,
                    "FolderCheck",
                    "#4CAF50");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("ConfiguracionViewModel", "Error al seleccionar carpeta de animes", ex);
            await _dialogService.MostrarDialogoAsync(
                LocalizationService.T("Dlg_ErrorTitulo"),
                string.Format(LocalizationService.T("Cfg_ErrorCambiarCarpetaMsj"), ex.Message),
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
                    LocalizationService.T("Cfg_CarpetaNoEncontradaTitulo"),
                    string.Format(LocalizationService.T("Cfg_CarpetaNoEncontradaMsj"), RutaBaseAnimes),
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
    public void AbrirCarpetaPlugins()
    {
        try
        {
            var ruta = AppDataPaths.PluginsFolder;
            if (Directory.Exists(ruta))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = ruta,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("ConfiguracionViewModel", "Error abriendo carpeta de plugins", ex);
        }
    }

    [RelayCommand]
    public async Task GuardarPreferenciasAsync()
    {
        try
        {
            var config = _settingsService.ObtenerConfiguracion();
            config.AutoSkipIntroOutro = AutoSkipIntroOutro;
            config.SubtitulosPorDefecto = SubtitulosPorDefecto;
            config.EstiloSubtitulos = ConstruirEstilo();
            config.DescargasSimultaneas = DescargasSimultaneas;
            config.IntervaloSincronizacionMinutos = IntervaloSincronizacionMinutos;
            config.PasosSaltoSegundos = PasosSaltoSegundos;
            config.EvitarSuspensionPantalla = EvitarSuspensionPantalla;
            config.AccionFinEpisodio = AccionFinEpisodio;
            config.PreferenciaAudioAnimeAv1 = string.IsNullOrEmpty(PreferenciaAudioAnimeAv1) ? null : PreferenciaAudioAnimeAv1;
            config.ServidorPreferidoAnimeAv1 = string.IsNullOrEmpty(ServidorPreferidoAnimeAv1) ? null : ServidorPreferidoAnimeAv1;
            config.BusquedaTorrentHabilitada = BusquedaTorrentHabilitada;
            config.GrupoFansubPreferidoTorrent = string.IsNullOrEmpty(GrupoFansubPreferidoTorrent) ? null : GrupoFansubPreferidoTorrent;
            config.ResolucionPreferidaTorrent = string.IsNullOrEmpty(ResolucionPreferidaTorrent) ? null : ResolucionPreferidaTorrent;
            config.SeguirSembrandoTorrents = SeguirSembrandoTorrents;
            config.UmbralMarcadoVisto = Math.Clamp(UmbralMarcadoVisto, 1, 100);
            config.NotificarNuevosEpisodios = NotificarNuevosEpisodios;
            config.MinimizarABandejaAlCerrar = MinimizarABandejaAlCerrar;
            config.NotificarConBandejaSiempre = NotificarConBandejaSiempre;
            config.Idioma = Idioma == "en" ? "en" : "es";
            config.VelocidadReproduccionDefecto = VelocidadReproduccionDefecto is >= 0.5 and <= 2.0 ? VelocidadReproduccionDefecto : 1.0;
            config.TeclaPanicoActiva = TeclaPanicoActiva;
            config.TeclaPanico = TeclaPanico == "Escape" ? "Escape" : "F12";
            config.Atajos = new Dictionary<string, string>(Atajos);

            await _settingsService.GuardarConfiguracionAsync(config);
            _startupService?.Sincronizar(IniciarConWindows);

            await _dialogService.MostrarDialogoAsync(
                LocalizationService.T("Cfg_PreferenciasGuardadasTitulo"),
                LocalizationService.T("Cfg_PreferenciasGuardadasMsj"),
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
            LocalizationService.T("Cfg_CerrarSesion"),
            LocalizationService.T("Cfg_CerrarSesionConfirmacionMsj"),
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
            LocalizationService.T("Cfg_BorrarDatosTitulo"),
            LocalizationService.T("Cfg_BorrarDatosConfirmacionMsj"),
            true,
            "DeleteForever",
            "#EF4444");

        if (!confirmarPrimero) return;

        bool confirmarSegundo = await _dialogService.MostrarDialogoAsync(
            LocalizationService.T("Cfg_UltimaConfirmacionTitulo"),
            LocalizationService.T("Cfg_UltimaConfirmacionMsj"),
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
                LocalizationService.T("Dlg_ErrorTitulo"),
                string.Format(LocalizationService.T("Cfg_ErrorBorrarDatosMsj"), ex.Message),
                false,
                "AlertCircle",
                "#E53935");
            return;
        }

        await _dialogService.MostrarDialogoAsync(
            LocalizationService.T("Cfg_DatosBorradosTitulo"),
            LocalizationService.T("Cfg_DatosBorradosMsj"),
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
            LocalizationService.T("Cfg_LimpiarCache"),
            LocalizationService.T("Cfg_LimpiarCacheConfirmacionMsj"),
            true,
            "Broom",
            "#F59E0B");

        if (!confirmar) return;

        try
        {
            var resultado = await _cacheMaintenanceService.LimpiarCacheHuerfanoAsync();

            await _dialogService.MostrarDialogoAsync(
                LocalizationService.T("Cfg_LimpiezaCompletadaTitulo"),
                string.Format(LocalizationService.T("Cfg_LimpiezaCompletadaMsj"), resultado.MegabytesLiberados, resultado.MiniaturasBorradas, resultado.PortadasBorradas),
                false,
                "CheckCircleOutline",
                "#4CAF50");
        }
        catch (Exception ex)
        {
            AppLogger.Error("ConfiguracionViewModel", "Error limpiando caché de imágenes", ex);
            await _dialogService.MostrarDialogoAsync(LocalizationService.T("Dlg_ErrorTitulo"), LocalizationService.T("Cfg_LimpiezaErrorMsj"), false, "AlertCircleOutline", "#EF4444");
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
