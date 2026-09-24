using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using FlyleafLib;
using FlyleafLib.MediaFramework.MediaDecoder;
using FlyleafLib.MediaPlayer;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Logros;

namespace AnimeLocalTracker.ViewModels;

[System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
public partial class ReproductorViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsService? _settingsService;
    private readonly IPlaybackStateService _playbackState;
    private readonly ISkipTimesCoordinator _skipCoordinator;
    private readonly IEpisodeNavigator _episodeNavigator;
    private readonly IFrameCaptureService _frameCaptureService;
    private readonly IPlaybackWindowModeCoordinator _windowModeCoordinator;
    private readonly ISubtitleCoordinator _subtitleCoordinator;
    private readonly IPlaybackVolumeCoordinator _volumeCoordinator;
    private readonly IPlaybackSeekCoordinator _seekCoordinator;
    private readonly ISystemMediaControlsService? _smtc;
    private readonly IScreenSaverPreventionService? _screenSaverPrevention;
    private CancellationTokenSource? _skipCts;

    // FUN-011: serializa los guardados periódicos de progreso (un guardado a la vez).
    private readonly SemaphoreSlim _guardadoLock = new(1, 1);

    [ObservableProperty]
    private Player _player = null!;

    [ObservableProperty]
    private bool _esModoMini;

    [ObservableProperty]
    private string _tituloAnime = string.Empty;

    [ObservableProperty]
    private string _tituloEpisodio = string.Empty;

    // Skip Intro / Outro (AniSkip & Fallback)
    [ObservableProperty]
    private bool _mostrarSkipIntro;

    [ObservableProperty]
    private bool _mostrarSkipButton;

    [ObservableProperty]
    private string _skipButtonTexto = "Saltar intro (S)";

    [ObservableProperty]
    private string _skipButtonIcon = "FastForward";

    [ObservableProperty]
    private bool _autoSkipIntroOutro = false;

    [ObservableProperty]
    private string _accionFinEpisodio = AccionFinEpisodioValores.AutoPlayCuentaAtras;

    // === Pasos de salto configurables (Configuración → Reproducción) ===
    [ObservableProperty]
    private int _pasosSaltoSegundos = 10;

    public string RetrocederTooltip => string.Format(LocalizationService.T("Player_RetrocederTooltipFormato"), PasosSaltoSegundos);
    public string AdelantarTooltip => string.Format(LocalizationService.T("Player_AdelantarTooltipFormato"), PasosSaltoSegundos);

    partial void OnPasosSaltoSegundosChanged(int value)
    {
        OnPropertyChanged(nameof(RetrocederTooltip));
        OnPropertyChanged(nameof(AdelantarTooltip));
    }

    // === Cuenta atrás de auto-play al siguiente episodio (AccionFinEpisodioValores.AutoPlayCuentaAtras) ===
    [ObservableProperty] private bool _mostrarCuentaAtrasSiguiente;
    [ObservableProperty] private int _segundosCuentaAtrasSiguiente;
    [ObservableProperty] private string _tituloSiguienteEnCuentaAtras = string.Empty;

    [RelayCommand]
    private void CancelarAutoPlay() => MostrarCuentaAtrasSiguiente = false;

    // Control de volumen y mute
    private int _volumen = 100;
    public int Volumen
    {
        get => _volumen;
        set
        {
            int clamped = Math.Clamp(value, 0, 100);
            if (SetProperty(ref _volumen, clamped))
            {
                OnVolumenChanged(clamped);
            }
        }
    }

    [ObservableProperty]
    private bool _isMuted = false;

    [ObservableProperty]
    private string _volumenIcon = "VolumeHigh";

    private int _volumenPrevioMute = 100;
    private bool _autoPlayEjecutado = false;
    private double _posicionInicioSegundos = 0;
    private volatile bool _haCompletadoOpen = false;

    private List<AniSkipResult> _skipTimes = new();
    public List<AniSkipResult> SkipTimes => _skipTimes;

    private AniSkipResult? _currentActiveSkip;
    public AniSkipResult? CurrentActiveSkip => _currentActiveSkip;

    private readonly HashSet<string> _skipAutoEjecutados = new();

    // Propiedades para controles de medios
    [ObservableProperty] private double _currentSeconds;
    [ObservableProperty] private double _totalSeconds;
    [ObservableProperty] private string _tiempoActualTexto = "00:00";
    [ObservableProperty] private string _tiempoTotalTexto = "00:00";
    [ObservableProperty] private string _tiempoCombinadoTexto = "00:00 / 00:00";
    [ObservableProperty] private string _playPauseIcon = "Pause";
    [ObservableProperty] private bool _isDraggingSlider = false;

    // Oculta el frame de video (queda solo la carátula/fondo) mientras se reanuda un episodio:
    // el seek de reanudación se aplica diferido (ver IPlaybackSeekCoordinator) y sin esto se ve
    // el video arrancar en el segundo 0 antes de saltar visiblemente al punto guardado.
    [ObservableProperty] private bool _ocultarVideoInicio;
    private DateTime _ocultarVideoDesdeUtc = DateTime.MinValue;
    private static readonly TimeSpan MaxOcultarVideoInicio = TimeSpan.FromSeconds(5);
    
    // Navegación entre episodios
    private bool _tieneEpisodioAnterior;
    public bool TieneEpisodioAnterior
    {
        get => _tieneEpisodioAnterior;
        set => SetProperty(ref _tieneEpisodioAnterior, value);
    }

    private bool _tieneEpisodioSiguiente;
    public bool TieneEpisodioSiguiente
    {
        get => _tieneEpisodioSiguiente;
        set => SetProperty(ref _tieneEpisodioSiguiente, value);
    }

    private string _episodioAnteriorTooltip = "Episodio anterior (B)";
    public string EpisodioAnteriorTooltip
    {
        get => _episodioAnteriorTooltip;
        set => SetProperty(ref _episodioAnteriorTooltip, value);
    }

    private string _episodioSiguienteTooltip = "Episodio siguiente (N)";
    public string EpisodioSiguienteTooltip
    {
        get => _episodioSiguienteTooltip;
        set => SetProperty(ref _episodioSiguienteTooltip, value);
    }

    // === CAJÓN LATERAL DE EPISODIOS (tecla L) ===
    // Reutiliza la lista del navigator (ya filtrada a episodios con archivo local, ver
    // CargarVideoAsync) — sin consulta nueva a la BD/disco, el dato ya estaba cargado para
    // Siguiente/Anterior episodio.
    public IReadOnlyList<EpisodioItem> EpisodiosDelCajon => _episodeNavigator.EpisodiosDisponibles;

    [ObservableProperty] private bool _cajonEpisodiosAbierto;

    [RelayCommand]
    private void ToggleCajonEpisodios() => CajonEpisodiosAbierto = !CajonEpisodiosAbierto;

    [RelayCommand]
    private void SaltarAEpisodio(EpisodioItem? episodio)
    {
        if (episodio == null || string.IsNullOrWhiteSpace(episodio.RutaCompleta)) return;
        CajonEpisodiosAbierto = false;
        CargarVideo(episodio.RutaCompleta, _animeId, TituloAnime, episodio.NumeroEpisodio, _episodeNavigator.EpisodiosDisponibles.ToList(), _rutaPortada);
    }

    private string _fullscreenIcon = "Fullscreen";
    public string FullscreenIcon
    {
        get => _fullscreenIcon;
        set => SetProperty(ref _fullscreenIcon, value);
    }
    
    private string _subtitulosIcon = "SubtitlesOutline";
    public string SubtitulosIcon
    {
        get => _subtitulosIcon;
        set => SetProperty(ref _subtitulosIcon, value);
    }
    
    private bool _subtitulosHabilitados = false;
    public bool SubtitulosHabilitados
    {
        get => _subtitulosHabilitados;
        set => SetProperty(ref _subtitulosHabilitados, value);
    }

    private bool _modoNocheActivo = false;
    /// <summary>
    /// "Modo noche": compresor de rango dinámico (FFmpeg acompressor) sobre el audio para que
    /// el diálogo se escuche parejo sin que las escenas/openings fuertes suenen a todo volumen.
    /// Se persiste como preferencia por defecto (AppSettings.ModoNocheActivo).
    /// </summary>
    public bool ModoNocheActivo
    {
        get => _modoNocheActivo;
        set
        {
            if (SetProperty(ref _modoNocheActivo, value))
            {
                AplicarModoNocheAlPlayer(value);
                GuardarModoNochePreferencia(value);
            }
        }
    }

    private static readonly List<Filter> FiltrosModoNoche = new()
    {
        new Filter { Id = "modonoche", Name = "acompressor", Args = "threshold=0.089:ratio=9:attack=200:release=1000:makeup=2" }
    };

    private void AplicarModoNocheAlPlayer(bool activo)
    {
        if (Player?.Config?.Audio == null) return;
        try
        {
            Player.Config.Audio.Filters = activo ? FiltrosModoNoche : new List<Filter>();
            Player.Config.Audio.ReloadFilters();
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ReproductorViewModel", $"Error aplicando Modo Noche: {ex.Message}");
        }
    }

    private void GuardarModoNochePreferencia(bool activo)
    {
        if (_settingsService == null) return;
        var config = _settingsService.ObtenerConfiguracion();
        if (config == null || config.ModoNocheActivo == activo) return;
        config.ModoNocheActivo = activo;
        _ = _settingsService.GuardarConfiguracionAsync(config);
    }

    [RelayCommand]
    private void ToggleModoNoche() => ModoNocheActivo = !ModoNocheActivo;

    private int _animeId;
    public int AnimeId => _animeId;

    private int _episodio;
    public int Episodio => _episodio;

    private string _rutaVideo = string.Empty;
    public string RutaVideo => _rutaVideo;

    private string? _rutaPortada;

    private bool _fueMarcadoComoVisto = false;
    
    // Cache para evitar recalcular duración en cada tick
    private double _lastNotifiedSeconds = -1;
    private double _lastSavedSeconds = -1;
    private double _resumingPositionSeconds = 0;
    public double ResumingPositionSeconds => _resumingPositionSeconds;
    private bool _durationCached = false;

    public ReproductorViewModel(
        IDatabaseService databaseService,
        IAnimeTrackingService animeTrackingService,
        IAuthService authService,
        IAniSkipService? aniSkipService = null,
        ISettingsService? settingsService = null,
        IPlaybackStateService? playbackStateService = null,
        ISkipTimesCoordinator? skipTimesCoordinator = null,
        IVentanaPrincipal? ventanaPrincipal = null,
        ISystemMediaControlsService? systemMediaControlsService = null,
        ILogrosService? logrosService = null,
        IDialogService? dialogService = null,
        IScreenSaverPreventionService? screenSaverPreventionService = null,
        IEpisodeNavigator? episodeNavigator = null,
        IFrameCaptureService? frameCaptureService = null,
        IPlaybackWindowModeCoordinator? windowModeCoordinator = null,
        ISubtitleCoordinator? subtitleCoordinator = null,
        IPlaybackVolumeCoordinator? volumeCoordinator = null,
        IPlaybackSeekCoordinator? seekCoordinator = null)
    {
        _settingsService = settingsService;
        _logrosService = logrosService;
        DialogService = dialogService;
        _screenSaverPrevention = screenSaverPreventionService;

        _playbackState = playbackStateService ?? new PlaybackStateService(databaseService, animeTrackingService, authService);

        _skipCoordinator = skipTimesCoordinator ?? new SkipTimesCoordinator(aniSkipService);
        _episodeNavigator = episodeNavigator ?? new EpisodeNavigator();
        _frameCaptureService = frameCaptureService ?? new FrameCaptureService();
        _windowModeCoordinator = windowModeCoordinator ?? new PlaybackWindowModeCoordinator(ventanaPrincipal);
        _subtitleCoordinator = subtitleCoordinator ?? new SubtitleCoordinator();
        _volumeCoordinator = volumeCoordinator ?? new PlaybackVolumeCoordinator();
        _seekCoordinator = seekCoordinator ?? new PlaybackSeekCoordinator();

        // SMT-01: SMTC es un singleton (un único HWND); esta instancia se suscribe a sus
        // botones mientras controla la reproducción y se desuscribe en Dispose(). Al ser
        // ReproductorViewModel transient (una instancia nueva por navegación al reproductor),
        // nunca hay dos VMs escuchando el mismo evento a la vez (NavigationService dispone el
        // anterior antes de crear uno nuevo).
        _smtc = systemMediaControlsService;
        if (_smtc != null)
        {
            _smtc.PlayRequested += OnSmtcPlayRequested;
            _smtc.PauseRequested += OnSmtcPauseRequested;
            _smtc.NextRequested += OnSmtcNextRequested;
            _smtc.PreviousRequested += OnSmtcPreviousRequested;
        }

        if (_settingsService != null)
        {
            var config = _settingsService.ObtenerConfiguracion();
            if (config != null)
            {
                _autoSkipIntroOutro = config.AutoSkipIntroOutro;
                _accionFinEpisodio = string.IsNullOrWhiteSpace(config.AccionFinEpisodio) ? AccionFinEpisodioValores.AutoPlayCuentaAtras : config.AccionFinEpisodio;
                _pasosSaltoSegundos = config.PasosSaltoSegundos is 5 or 10 or 30 or 60 ? config.PasosSaltoSegundos : 10;
                _subtitulosHabilitados = config.SubtitulosPorDefecto;
                _subtitulosIcon = config.SubtitulosPorDefecto ? "Subtitles" : "SubtitlesOutline";
                _modoNocheActivo = config.ModoNocheActivo;
            }
        }
    }

    /// <summary>
    /// FUN-003: umbral configurable de "marcado como visto" (AppSettings.UmbralMarcadoVisto,
    /// 1-100 → 0-1). Antes el auto-marcado estaba fijo en 90% y el ajuste de la UI era decorativo.
    /// </summary>
    private double UmbralMarcadoVistoActual
    {
        get
        {
            int porcentaje = _settingsService?.ObtenerConfiguracion()?.UmbralMarcadoVisto ?? 90;
            return Math.Clamp(porcentaje, 1, 100) / 100.0;
        }
    }

    private void OnVolumenChanged(int value)
    {
        if (_volumeCoordinator.AplicarVolumen(Player, value, IsMuted))
        {
            IsMuted = false;
        }

        ActualizarVolumenIcon();
    }

    [RelayCommand]
    public void ToggleMute()
    {
        if (IsMuted || Volumen == 0)
        {
            IsMuted = false;
            Volumen = _volumenPrevioMute > 0 ? _volumenPrevioMute : 100;
            _volumeCoordinator.Desmutear(Player, Volumen);
        }
        else
        {
            _volumenPrevioMute = Volumen;
            IsMuted = true;
            // Bug: la barra de volumen seguía al máximo al silenciar porque Volumen
            // no cambiaba — ahora baja a 0 (y se restaura el previo al desmutear)
            Volumen = 0;
            _volumeCoordinator.Mutear(Player);
        }
        ActualizarVolumenIcon();
    }

    private void ActualizarVolumenIcon()
    {
        VolumenIcon = _volumeCoordinator.CalcularIcono(Volumen, IsMuted);
    }

    private static readonly object _engineLock = new();
    private static bool _engineIniciado = false;

    // En entornos de pruebas (headless) Flyleaf puede dejar su hilo maestro bloqueado y
    // cualquier construcción posterior de Config() se cuelga en Dispatcher.Invoke síncrono.
    private static bool EsEntornoPruebas()
    {
        try
        {
            return EsEntornoPruebas(
                Process.GetCurrentProcess().ProcessName,
                Environment.CommandLine,
                AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Criterio deliberadamente ESTRECHO: solo los marcadores del ejecutor de pruebas (testhost /
    /// vstest / xunit). Un simple "contiene 'test'" es peligroso — un plugin cargado en la app
    /// (p. ej. un ensamblado "plugin_test") o una ruta de instalación como "C:\Users\test\..."
    /// hacían creer a la app que corría bajo pruebas y dejaban los videos sin reproductor.
    /// </summary>
    internal static bool EsEntornoPruebas(string nombreProceso, string lineaComandos, IEnumerable<string?> nombresEnsamblados)
    {
        if (nombreProceso.Contains("testhost", StringComparison.OrdinalIgnoreCase) ||
            nombreProceso.Contains("vstest", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (lineaComandos.Contains("vstest", StringComparison.OrdinalIgnoreCase) ||
            lineaComandos.Contains("testhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return nombresEnsamblados.Any(nombre =>
            nombre != null && (
                nombre.StartsWith("xunit", StringComparison.OrdinalIgnoreCase) ||
                nombre.Equals("testhost", StringComparison.OrdinalIgnoreCase) ||
                nombre.StartsWith("Microsoft.TestPlatform", StringComparison.OrdinalIgnoreCase)));
    }

    public void AsegurarPlayerInicializado()
    {
        if (Player == null || Player.IsDisposed)
        {
            Player = CreateOptimizedPlayer();
        }
    }

    public virtual Player CreateOptimizedPlayer()
    {
        if (EsEntornoPruebas())
        {
            return null!;
        }

        try
        {
            lock (_engineLock)
            {
                if (!_engineIniciado)
                {
                    try
                    {
                        Engine.Start(new EngineConfig()
                        {
                            FFmpegPath = ":FFmpeg",
                            UIRefresh = true
                        });
                        _engineIniciado = true;
                    }
                    catch (Exception initEx)
                    {
                        AppLogger.Debug("ReproductorViewModel", $"No se pudo iniciar motor Flyleaf: {initEx.Message}");
                    }
                }
            }

            var config = new Config();
            
            // 1. Seeking rápido instantáneo por Keyframe (no frame-accurate) -> 0 ms seek latency
            if (config.Player != null)
            {
                config.Player.SeekAccurate = false;
                config.Player.AutoPlay = true;

                // El hilo de reproducción es el que alimenta el audio: con la ventana sin foco y
                // otras apps compitiendo por CPU, en prioridad Normal el sonido se entrecorta.
                config.Player.ThreadPriority = ThreadPriority.Highest;
            }

            // 2. Decoder multi-hilos para decodificación suave de AV1 y HEVC 10-bit
            if (config.Decoder != null)
            {
                config.Decoder.VideoThreads = Math.Max(2, Environment.ProcessorCount / 2);
            }

            // 2b. Decodificación por GPU (Direct3D) explícita: es el valor por defecto de FlyleafLib,
            // pero se deja explícito para que no dependa de un default que la librería podría cambiar
            // en una actualización futura. El soporte real de AV1/HEVC 10-bit por hardware varía mucho
            // entre GPUs, así que si falla, FlyleafLib cae solo a decodificación por software (más lenta
            // pero funcional) — se registra cuál de las dos se usó en OpenCompleted, más abajo.
            if (config.Video != null)
            {
                config.Video.VideoAcceleration = true;
            }

            // 3. Buffer de Demuxer en RAM (30 segundos precargados en memoria para reproducción sin tirones)
            if (config.Demuxer != null)
            {
                // BufferDuration en ticks (1 tick = 100ns -> 30 segundos = 300,000,000 ticks)
                config.Demuxer.BufferDuration = 300_000_000L;
            }

            // 4. Subtítulos
            if (config.Subtitles != null)
            {
                config.Subtitles.Enabled = SubtitulosHabilitados;
            }

            // 5. Modo Noche (compresor de rango dinámico), si el usuario lo dejó activo la vez anterior
            if (config.Audio != null && ModoNocheActivo)
            {
                config.Audio.Filters = FiltrosModoNoche;
            }

            var player = new Player(config);

            // Prioridad del proceso solo mientras hay un video abierto (se restaura en Dispose).
            if (!_prioridadElevada)
            {
                PrioridadReproduccion.Elevar();
                _prioridadElevada = true;
            }

            player.OpenCompleted += (s, e) =>
            {
                _haCompletadoOpen = true;
                EvaluarSubtitulosPorDefecto();

                // Diagnóstico: qué decodificador se negoció de verdad para este archivo. El soporte de
                // AV1/HEVC 10-bit por GPU varía mucho entre tarjetas — sin este log, una caída
                // silenciosa a software (más lenta, más CPU) se confundiría con "el reproductor va lento"
                // sin pista de la causa real.
                try
                {
                    bool porHardware = player.VideoDecoder?.VideoAccelerated ?? false;
                    AppLogger.Debug("ReproductorViewModel", $"Decodificación de video: {(porHardware ? "hardware (GPU)" : "software (CPU)")}");
                }
                catch { }

                try
                {
                    if (player.Status != Status.Playing)
                    {
                        player.Play();
                    }
                    PlayPauseIcon = "Pause";
                    _smtc?.ActualizarEstadoReproduccion(true);
                    IniciarProteccionPantallaSiCorresponde();
                }
                catch { }

                if (player.Audio != null)
                {
                    try
                    {
                        player.Audio.Volume = Volumen;
                        player.Audio.Mute = IsMuted;
                    }
                    catch { }
                }

                // IMPORTANTE: NO seekear aquí (ni el diferido-al-abrir del coordinador ni la
                // reanudación). En este punto el decoder de video aún está creando su contexto de
                // renderizado y un Player.CurTime inmediato interrumpe ese proceso
                // en algunos archivos (HEVC/VFR) dejando la pantalla en negro con
                // audio avanzando. El bucle de tracking aplica el seek vía
                // IPlaybackSeekCoordinator cuando el video ya está reproduciendo de verdad.
            };
            return player;
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ReproductorViewModel", $"Player nativo no disponible en este entorno: {ex.Message}");
            return null!;
        }
    }

    [RelayCommand]
    public void TogglePlayPause()
    {
        if (Player == null) return;

        if (Player.Status == Status.Playing)
        {
            Pausar();
        }
        else
        {
            Reproducir();
        }
    }

    private void Pausar()
    {
        if (Player == null || Player.Status != Status.Playing) return;

        Player.Pause();
        PlayPauseIcon = "Play";
        _smtc?.ActualizarEstadoReproduccion(false);
        DetenerProteccionPantalla();
        _ = GuardarProgresoActualAsync();
    }

    private void Reproducir()
    {
        if (Player == null || Player.Status == Status.Playing) return;

        // Si el episodio terminó, reproducir de nuevo desde el inicio
        if (Player.Status == Status.Ended)
        {
            try { Player.CurTime = 0; } catch (Exception ex) { AppLogger.Debug("ReproductorViewModel", $"No se pudo reiniciar posición: {ex.Message}"); }
            _fueMarcadoComoVisto = false;
        }
        Player.Play();
        PlayPauseIcon = "Pause";
        _smtc?.ActualizarEstadoReproduccion(true);
        IniciarProteccionPantallaSiCorresponde();
    }

    /// <summary>Activa SetThreadExecutionState (evitar suspensión) solo si el usuario lo pidió en
    /// Configuración; se llama en cada Play (manual, SMTC o autoplay al abrir un episodio).</summary>
    private void IniciarProteccionPantallaSiCorresponde()
    {
        bool evitarSuspension = _settingsService?.ObtenerConfiguracion()?.EvitarSuspensionPantalla ?? true;
        if (evitarSuspension)
        {
            _screenSaverPrevention?.Activar();
        }
    }

    private void DetenerProteccionPantalla() => _screenSaverPrevention?.Desactivar();

    /// <summary>
    /// SMT-01: el evento ButtonPressed de SMTC llega en un hilo COM/MTA ajeno al Dispatcher
    /// de WPF — nunca tocar Player ni propiedades observables sin saltar antes al hilo de UI.
    /// </summary>
    private void OnSmtcPlayRequested(object? sender, EventArgs e) =>
        System.Windows.Application.Current?.Dispatcher?.BeginInvoke(Reproducir);

    private void OnSmtcPauseRequested(object? sender, EventArgs e) =>
        System.Windows.Application.Current?.Dispatcher?.BeginInvoke(Pausar);

    private void OnSmtcNextRequested(object? sender, EventArgs e) =>
        System.Windows.Application.Current?.Dispatcher?.BeginInvoke(SiguienteEpisodio);

    private void OnSmtcPreviousRequested(object? sender, EventArgs e) =>
        System.Windows.Application.Current?.Dispatcher?.BeginInvoke(AnteriorEpisodio);
    
    [RelayCommand]
    public void Rewind10()
    {
        double newSeconds = Math.Max(0, CurrentSeconds - PasosSaltoSegundos);
        Seek(newSeconds);
    }

    [RelayCommand]
    public void Forward10()
    {
        double max = TotalSeconds > 0 ? TotalSeconds : double.MaxValue;
        double newSeconds = Math.Min(max, CurrentSeconds + PasosSaltoSegundos);
        Seek(newSeconds);
    }

    // === Scrubbing de la línea de tiempo ===
    private double _posicionAntesArrastre;

    private void ActualizarTextosTiempo(double posicionSegundos)
    {
        var t = TimeSpan.FromSeconds(posicionSegundos);
        TiempoActualTexto = t.ToString(t.Hours > 0 ? @"hh\:mm\:ss" : @"mm\:ss");
        TiempoCombinadoTexto = $"{TiempoActualTexto} / {TiempoTotalTexto}";
    }

    private static double AcotarPosicion(double segundos)
    {
        if (segundos < 0) return 0;
        return segundos;
    }

    /// <summary>
    /// Comando para cuando el usuario suelta el slider (Thumb.DragCompleted), hace clic en la pista
    /// o usa los atajos de teclado. Feedback de UI inmediato + seek nativo coalescido (último-gana).
    /// </summary>
    [RelayCommand]
    public void Seek(double seconds)
    {
        seconds = AcotarPosicion(seconds);

        // Actualizar UI inmediatamente para feedback instantáneo
        _seekCoordinator.IniciarVentanaDeSettle();
        _lastNotifiedSeconds = seconds;
        CurrentSeconds = seconds;
        ActualizarTextosTiempo(seconds);

        _seekCoordinator.SolicitarSeek(Player, seconds, () => _haCompletadoOpen);
    }

    /// <summary>
    /// Inicio de arrastre del pulgar: congela el repintado del bucle y recuerda la posición real del reproductor.
    /// </summary>
    public void IniciarArrastre()
    {
        IsDraggingSlider = true;

        // La posición autoritativa es la del reproductor, no la del slider (el binding pudo ya mover el thumb)
        double posicionReal = CurrentSeconds;
        if (Player != null && !Player.IsDisposed)
        {
            try { posicionReal = TimeSpan.FromTicks(Player.CurTime).TotalSeconds; } catch { }
        }
        _posicionAntesArrastre = posicionReal;
    }

    /// <summary>
    /// Durante el arrastre: feedback visual instantáneo (thumb + tiempo) y scrub en vivo
    /// a través del mismo coalescing de seeks (último-gana, máx ~4 seeks/seg).
    /// </summary>
    public void VistaPreviaArrastre(double segundos)
    {
        if (!IsDraggingSlider) return;

        segundos = AcotarPosicion(segundos);
        if (TotalSeconds > 0 && segundos > TotalSeconds) segundos = TotalSeconds;

        CurrentSeconds = segundos;
        ActualizarTextosTiempo(segundos);

        _seekCoordinator.SolicitarSeek(Player, segundos, () => _haCompletadoOpen);
    }

    /// <summary>
    /// Fin de arrastre: aplica el seek final garantizado, actualiza UI y descongela el bucle.
    /// </summary>
    public void FinalizarArrastre(double segundos)
    {
        segundos = AcotarPosicion(segundos);
        if (TotalSeconds > 0 && segundos > TotalSeconds) segundos = TotalSeconds;

        IsDraggingSlider = false;

        // Si la posición apenas cambió respecto a antes de arrastrar, restauramos sin seek extra
        if (Math.Abs(segundos - _posicionAntesArrastre) < 0.25)
        {
            CurrentSeconds = _posicionAntesArrastre;
            ActualizarTextosTiempo(_posicionAntesArrastre);
            return;
        }

        Seek(segundos);
    }

    [RelayCommand]
    public void ToggleFullscreen()
    {
        string? icono = _windowModeCoordinator.AlternarPantallaCompleta();
        if (icono != null) FullscreenIcon = icono;
    }

    [RelayCommand]
    public void AlternarModoMini()
    {
        if (EsModoMini)
        {
            RestaurarFormatoHabitual();
        }
        else
        {
            MinimizarAMini();
        }
    }

    /// <summary>
    /// PIP-01: además de conmutar el flag que muestra los controles compactos, encoge la
    /// VENTANA de verdad (Topmost, sin chrome, anclada a la esquina) vía IVentanaPrincipal —
    /// así el video sigue visible por encima de otras apps, no solo de otras pestañas de esta.
    /// </summary>
    [RelayCommand]
    public void MinimizarAMini()
    {
        EsModoMini = true;
        _windowModeCoordinator.EntrarModoMini();
    }

    [RelayCommand]
    public void RestaurarFormatoHabitual()
    {
        EsModoMini = false;
        _windowModeCoordinator.SalirModoMini();
    }


    [RelayCommand]
    public void SelectSubtitleStream(object stream)
    {
        if (Player == null || stream == null) return;

        SubtitulosHabilitados = true;
        SubtitulosIcon = "Subtitles";
        _subtitleCoordinator.SeleccionarPista(Player, stream);
    }

    [RelayCommand]
    public void TurnOffSubtitles()
    {
        DeshabilitarSubtitulos();
    }

    public void EvaluarSubtitulosPorDefecto()
    {
        try
        {
            bool permitirSubtitulos = _settingsService?.ObtenerConfiguracion()?.SubtitulosPorDefecto ?? true;

            if (_subtitleCoordinator.DebenHabilitarsePorDefecto(Player, permitirSubtitulos))
            {
                HabilitarSubtitulos();
            }
            else
            {
                DeshabilitarSubtitulos();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ReproductorViewModel", $"Error al evaluar subtítulos por defecto: {ex.Message}");
        }
    }

    public void DeshabilitarSubtitulos()
    {
        SubtitulosHabilitados = false;
        SubtitulosIcon = "SubtitlesOutline";
        _subtitleCoordinator.Deshabilitar(Player);
    }

    public void HabilitarSubtitulos()
    {
        SubtitulosHabilitados = true;
        SubtitulosIcon = "Subtitles";
        _subtitleCoordinator.Habilitar(Player);
    }

    public EpisodioItem? ObtenerSiguienteEpisodio() => _episodeNavigator.ObtenerSiguiente(_episodio);

    public EpisodioItem? ObtenerAnteriorEpisodio() => _episodeNavigator.ObtenerAnterior(_episodio);

    public void ActualizarEstadosNavegacionEpisodios()
    {
        var siguiente = ObtenerSiguienteEpisodio();
        _siguienteEpisodioCache = siguiente;
        TieneEpisodioSiguiente = siguiente != null && !string.IsNullOrWhiteSpace(siguiente.RutaCompleta);
        EpisodioSiguienteTooltip = TieneEpisodioSiguiente
            ? $"Siguiente: Episodio {siguiente!.NumeroEpisodio} (N)"
            : "No hay siguiente episodio";

        var anterior = ObtenerAnteriorEpisodio();
        TieneEpisodioAnterior = anterior != null && !string.IsNullOrWhiteSpace(anterior.RutaCompleta);
        EpisodioAnteriorTooltip = TieneEpisodioAnterior
            ? $"Anterior: Episodio {anterior!.NumeroEpisodio} (B)"
            : "No hay episodio anterior";

        _smtc?.ActualizarNavegacionDisponible(TieneEpisodioSiguiente, TieneEpisodioAnterior);
    }

    [RelayCommand]
    public void SiguienteEpisodio()
    {
        var siguiente = ObtenerSiguienteEpisodio();
        if (siguiente != null && !string.IsNullOrWhiteSpace(siguiente.RutaCompleta))
        {
            CargarVideo(siguiente.RutaCompleta, _animeId, TituloAnime, siguiente.NumeroEpisodio, _episodeNavigator.EpisodiosDisponibles.ToList(), _rutaPortada);
        }
    }

    [RelayCommand]
    public void AnteriorEpisodio()
    {
        var anterior = ObtenerAnteriorEpisodio();
        if (anterior != null && !string.IsNullOrWhiteSpace(anterior.RutaCompleta))
        {
            CargarVideo(anterior.RutaCompleta, _animeId, TituloAnime, anterior.NumeroEpisodio, _episodeNavigator.EpisodiosDisponibles.ToList(), _rutaPortada);
        }
    }

    /// <summary>ARQ-01: decisión pura de pre-carga — movida a <see cref="EpisodeNavigator"/>; se
    /// mantiene este reenvío para no tocar los tests existentes que la llaman por este nombre.</summary>
    internal static bool DebePrecargarSiguienteEpisodio(double porcentaje, string? rutaSiguiente, string? rutaYaPrecargada) =>
        EpisodeNavigator.DebePrecargarSiguienteEpisodio(porcentaje, rutaSiguiente, rutaYaPrecargada);

    private CancellationTokenSource? _trackingCts;

    /// <summary>
    /// PERF: <see cref="ObtenerSiguienteEpisodio"/> recorre y ordena _episodiosDisponibles (puede tener
    /// cientos/miles de elementos en animes largos, p. ej. One Piece). La lista no cambia durante la
    /// reproducción del episodio actual, así que el resultado se calcula una vez en
    /// ActualizarEstadosNavegacionEpisodios() y el loop de progreso (250ms) lee este caché en vez de
    /// recalcularlo en cada tick.
    /// </summary>
    private EpisodioItem? _siguienteEpisodioCache;

    public async Task VerificarProgresoPrevioAsync(int animeId, int episodio)
    {
        try
        {
            var previo = await _playbackState.ObtenerPosicionParaReanudarAsync(animeId, episodio, _rutaVideo);
            if (previo.HasValue)
            {
                var (posicion, duracion) = previo.Value;
                _resumingPositionSeconds = posicion;
                CurrentSeconds = posicion;
                var tCur = TimeSpan.FromSeconds(posicion);
                TiempoActualTexto = tCur.ToString(tCur.Hours > 0 ? @"hh\:mm\:ss" : @"mm\:ss");
                if (duracion > 0)
                {
                    TotalSeconds = duracion;
                    var tDur = TimeSpan.FromSeconds(duracion);
                    TiempoTotalTexto = tDur.ToString(tDur.Hours > 0 ? @"hh\:mm\:ss" : @"mm\:ss");
                }
                TiempoCombinadoTexto = $"{TiempoActualTexto} / {TiempoTotalTexto}";
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ReproductorViewModel", $"Error comprobando progreso previo: {ex.Message}");
        }
    }

    public async Task CargarSkipTimesAsync(int animeId, int episodio, CancellationToken ct = default)
    {
        try
        {
            // AniSkip API como fuente primaria; si no hay datos, detección local por escenas (Python/ffmpeg)
            // usando la ruta del video local actual (requiere un archivo en disco).
            var results = await _skipCoordinator.CargarSkipTimesAsync(animeId, episodio, TotalSeconds, RutaVideo, ct);
            if (!ct.IsCancellationRequested && results != null && results.Count > 0)
            {
                Interlocked.Exchange(ref _skipTimes, new List<AniSkipResult>(results));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLogger.Debug("ReproductorViewModel", $"Error cargando skip times de AniSkip: {ex.Message}");
        }
    }

    public void CargarVideo(string rutaVideo, int animeId, string tituloAnime, int episodio, List<EpisodioItem>? listaEpisodios = null, string? rutaPortada = null)
    {
        _ = CargarVideoAsync(rutaVideo, animeId, tituloAnime, episodio, listaEpisodios, rutaPortada);
    }

    public async Task CargarVideoAsync(string rutaVideo, int animeId, string tituloAnime, int episodio, List<EpisodioItem>? listaEpisodios = null, string? rutaPortada = null)
    {
        _ = GuardarProgresoActualAsync();

        CancellationToken skipCtToken = CancelarTrabajoPendienteDelEpisodioAnterior();
        RecargarConfiguracionDeSesion();
        AsignarMetadatosDeEpisodio(rutaVideo, animeId, episodio, tituloAnime, rutaPortada);
        EstablecerListaDeEpisodiosSiCorresponde(listaEpisodios);
        ActualizarEstadosNavegacionEpisodios();

        // Cancelar rastreo previo
        _trackingCts?.Cancel();
        _trackingCts?.Dispose();
        _trackingCts = new CancellationTokenSource();

        // 1. Asegurar que Player existe antes de configurar el nuevo archivo
        AsegurarPlayerInicializado();
        DetenerPlayerSiEstabaActivo();

        // 2. Obtener progreso previo ANTES de abrir/reproducir para que comience de inmediato donde se dejó
        await VerificarProgresoPrevioAsync(animeId, episodio);
        _posicionInicioSegundos = _resumingPositionSeconds;

        // Cubrir el video hasta que el seek de reanudación diferido se aplique de verdad
        // (evita el "flash" del episodio arrancando en 0:00 antes de saltar al punto guardado).
        OcultarVideoInicio = _posicionInicioSegundos > 5;
        _ocultarVideoDesdeUtc = DateTime.UtcNow;

        // 3. Cargar marcas de skip de AniSkip en segundo plano
        _ = CargarSkipTimesAsync(animeId, episodio, skipCtToken);

        // 4. Sincronizar ícono de fullscreen con el estado actual de la ventana
        string? iconoFullscreen = _windowModeCoordinator.IconoPantallaCompletaActual();
        if (iconoFullscreen != null) FullscreenIcon = iconoFullscreen;

        if (Player != null)
        {
            Player.OpenAsync(rutaVideo);

            // Velocidad de reproducción por defecto configurable
            try
            {
                double velocidad = _settingsService?.ObtenerConfiguracion()?.VelocidadReproduccionDefecto ?? 1.0;
                Player.Speed = (float)Math.Clamp(velocidad, 0.5, 2.0);
            }
            catch { }
        }

        _ = RastrearProgresoAsync(_trackingCts.Token);
    }

    /// <summary>
    /// Cancela/reinicia todo lo que quedaba en curso del episodio anterior (seek coalescido,
    /// detección de skips, pre-carga del siguiente). Devuelve el token de la detección de skips
    /// YA capturado en una variable local — a propósito, no debe releerse <c>_skipCts</c> después
    /// del primer <c>await</c> de <see cref="CargarVideoAsync"/>: una segunda llamada concurrente
    /// (doble clic en "Siguiente") podría reasignar el campo antes de que se use el token.
    /// </summary>
    private CancellationToken CancelarTrabajoPendienteDelEpisodioAnterior()
    {
        // Descartar seeks coalescidos pendientes del episodio anterior
        _seekCoordinator.Reiniciar();

        // Cancelar detección de skips previa
        _skipCts?.Cancel();
        _skipCts?.Dispose();
        _skipCts = new CancellationTokenSource();
        var currentSkipCts = _skipCts;

        Interlocked.Exchange(ref _skipTimes, new List<AniSkipResult>());
        _skipAutoEjecutados.Clear();
        _currentActiveSkip = null;
        MostrarSkipButton = false;
        MostrarSkipIntro = false;
        _autoPlayEjecutado = false;
        MostrarCuentaAtrasSiguiente = false;

        // Cancelar la pre-carga del siguiente episodio del video anterior (si seguía en curso)
        _episodeNavigator.CancelarPrecargaYReiniciar();

        return currentSkipCts.Token;
    }

    private void RecargarConfiguracionDeSesion()
    {
        if (_settingsService == null) return;

        var config = _settingsService.ObtenerConfiguracion();
        if (config == null) return;

        AutoSkipIntroOutro = config.AutoSkipIntroOutro;
        AccionFinEpisodio = string.IsNullOrWhiteSpace(config.AccionFinEpisodio) ? AccionFinEpisodioValores.AutoPlayCuentaAtras : config.AccionFinEpisodio;
        PasosSaltoSegundos = config.PasosSaltoSegundos is 5 or 10 or 30 or 60 ? config.PasosSaltoSegundos : 10;
        SubtitulosHabilitados = config.SubtitulosPorDefecto;
        SubtitulosIcon = config.SubtitulosPorDefecto ? "Subtitles" : "SubtitlesOutline";
    }

    private void AsignarMetadatosDeEpisodio(string rutaVideo, int animeId, int episodio, string tituloAnime, string? rutaPortada)
    {
        _rutaVideo = rutaVideo;
        _animeId = animeId;
        _episodio = episodio;
        OnPropertyChanged(nameof(Episodio));
        _rutaPortada = rutaPortada;
        TituloAnime = tituloAnime;
        TituloEpisodio = string.Format(LocalizationService.T("Act_EpisodioFormato"), episodio);
        _fueMarcadoComoVisto = false;

        // SMT-01: overlay nativo de Windows — título, episodio y portada del anime que arranca.
        _smtc?.ActualizarMetadatos(tituloAnime, episodio, rutaPortada);
        _durationCached = false;
        _lastNotifiedSeconds = -1;
        _lastSavedSeconds = -1;
        _resumingPositionSeconds = 0;
        _posicionInicioSegundos = 0;
        _haCompletadoOpen = false;
    }

    private void EstablecerListaDeEpisodiosSiCorresponde(List<EpisodioItem>? listaEpisodios)
    {
        if (listaEpisodios == null) return;

        _episodeNavigator.EstablecerEpisodios(listaEpisodios);
        OnPropertyChanged(nameof(EpisodiosDelCajon));
    }

    private void DetenerPlayerSiEstabaActivo()
    {
        if (Player == null || Player.Status == Status.Stopped) return;

        try
        {
            Player.Stop();
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ReproductorViewModel", $"Player stop antes de cambiar archivo: {ex.Message}");
        }
    }

    /// <summary>Tecla configurada para una acción del reproductor (con fallback).</summary>
    public System.Windows.Input.Key ObtenerTeclaPara(string accion)
    {
        var config = _settingsService?.ObtenerConfiguracion();
        string nombre = config?.ObtenerTecla(accion, accion switch
        {
            "PlayPausa" => "Space",
            "PantallaCompleta" => "F11",
            "Silenciar" => "M",
            "SubirVolumen" => "Up",
            "BajarVolumen" => "Down",
            "Adelantar10" => "Right",
            "Retroceder10" => "Left",
            "SaltarIntro" => "S",
            "SiguienteEpisodio" => "N",
            // PIP-01: "P" pasa a ser el atajo de Modo Mini/PiP (los tooltips ya lo anunciaban
            // así desde antes, pero el atajo real estaba tomado por AnteriorEpisodio — nunca
            // llegaba a activarse). AnteriorEpisodio se mueve a "B" para liberar "P".
            "AnteriorEpisodio" => "B",
            "ModoMini" => "P",
            "Cerrar" => "Escape",
            "CapturarFrame" => "C",
            _ => string.Empty
        }) ?? string.Empty;

        return Enum.TryParse<System.Windows.Input.Key>(nombre, true, out var tecla)
            ? tecla
            : System.Windows.Input.Key.None;
    }

    /// <summary>
    /// Captura el fotograma actual (ver <see cref="FrameCaptureService"/>: snapshot directo del
    /// buffer Direct3D de Flyleaf, sin relanzar ffmpeg ni re-decodificar), lo copia al portapapeles
    /// y lo guarda en Imágenes\AnimeLocalTracker. Este comando solo traduce el resultado a un toast.
    /// </summary>
    [RelayCommand]
    private async Task CapturarFrameAsync()
    {
        string? ruta = await _frameCaptureService.CapturarYGuardarAsync(Player);

        if (ruta == null)
        {
            _ = WeakReferenceMessenger.Default.Send(new Messages.MostrarDialogoRequestMessage(
                LocalizationService.T("Player_CapturaTitulo"), LocalizationService.T("Player_CapturaErrorMsj"), false, "AlertCircleOutline", "#EF4444"));
            return;
        }

        _ = WeakReferenceMessenger.Default.Send(new Messages.MostrarDialogoRequestMessage(
            LocalizationService.T("Player_CapturaTitulo"), string.Format(LocalizationService.T("Player_CapturaListaFormato"), ruta), false, "CameraOutline", "#4CAF50"));
    }

    public async Task GuardarProgresoActualAsync(bool forzarProgresoCero = false)
    {
        if (_animeId <= 0 || _episodio <= 0) return;

        // FUN-011: un solo guardado a la vez. Si el tick de 3 s llega con otro en curso,
        // se descarta (el siguiente tick guardará el estado más reciente).
        if (!_guardadoLock.Wait(0)) return;

        // FUN-017: snapshot de identidad al iniciar — un guardado que termina después de
        // cambiar de episodio no debe notificar el progreso del episodio viejo bajo el ID
        // del nuevo (antes _animeId/_episodio se leían en el momento del envío). Esto incluye
        // _fueMarcadoComoVisto: si no se captura aquí, la notificación posterior al await lee
        // el campo YA reseteado a false por CargarVideoAsync del episodio siguiente (que arranca
        // de inmediato tras este guardado en fire-and-forget) y le avisa a DetalleView que el
        // capítulo que se acaba de terminar de ver "no está visto", aunque sí se guardó bien.
        int animeId = _animeId;
        int episodio = _episodio;
        string rutaVideo = _rutaVideo;
        bool fueMarcadoComoVisto = _fueMarcadoComoVisto;

        try
        {
            double curSec = 0;
            double durSec = 0;

            if (Player != null && !Player.IsDisposed)
            {
                curSec = TimeSpan.FromTicks(Player.CurTime).TotalSeconds;
                durSec = TimeSpan.FromTicks(Player.Duration).TotalSeconds;
            }

            if (durSec <= 0 && TotalSeconds > 0) durSec = TotalSeconds;
            if (curSec <= 0 && CurrentSeconds > 0) curSec = CurrentSeconds;

            var resultado = await _playbackState.GuardarProgresoAsync(new DatosProgresoReproduccion
            {
                AnimeId = animeId,
                NumeroEpisodio = episodio,
                RutaVideo = rutaVideo,
                PosicionSegundos = curSec,
                DuracionSegundos = durSec,
                ForzarProgresoCero = forzarProgresoCero,
                FueMarcadoComoVisto = fueMarcadoComoVisto
            });

            // Notificar a DetalleViewModel para actualizar la barra de progreso en vivo
            WeakReferenceMessenger.Default.Send(new Messages.EpisodioActualizadoMensaje(
                animeId, episodio, fueMarcadoComoVisto, resultado.ProgresoSegundos, resultado.TotalSegundos));
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ReproductorViewModel", $"Error al guardar progreso actual: {ex.Message}");
        }
        finally
        {
            _guardadoLock.Release();
        }
    }

    private async Task EvaluarLogrosAsync()
    {
        if (_logrosService == null) return;

        try
        {
            await _logrosService.EvaluarAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ReproductorViewModel", $"No se pudieron evaluar los logros: {ex.Message}");
        }
    }

    public async Task RealizarAutoTrackingAsync()
    {
        if (_fueMarcadoComoVisto) return;
        _fueMarcadoComoVisto = true;

        try
        {
            bool ok = await _playbackState.MarcarComoVistoYSincronizarAsync(_animeId, _episodio, _rutaVideo, TotalSeconds);
            if (!ok) return;

            // Notificación flotante sutil (Toast)
            _ = WeakReferenceMessenger.Default.Send(new Messages.MostrarDialogoRequestMessage(
                "Auto-Tracking",
                string.Format(LocalizationService.T("Player_EpisodioMarcadoVistoMsj"), _episodio),
                false, "CheckCircle", "#4CAF50"));

            // Avisar a la vista de detalles para que actualice la lista automáticamente
            WeakReferenceMessenger.Default.Send(new Messages.EpisodioActualizadoMensaje(_animeId, _episodio, true, 0, TotalSeconds));

            // Terminar un episodio puede desbloquear un logro: se evalúa aquí para avisar en el momento
            // (y no solo la próxima vez que abras Estadísticas o Logros).
            _ = EvaluarLogrosAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Error("ReproductorViewModel", "Error en auto-tracking", ex);
        }
    }

    /// <summary>
    /// Qué hacer al llegar al final de un episodio, según Configuración → Reproducción → "Al terminar
    /// un episodio". Se llama UNA vez por episodio (guardado por <c>_autoPlayEjecutado</c> en el
    /// llamador). "Permanecer pausado" y "sin siguiente episodio" no hacen nada: el video se queda
    /// en el último fotograma, que es exactamente el comportamiento por defecto de Flyleaf al terminar.
    /// </summary>
    private async Task EjecutarAccionFinEpisodioAsync(CancellationToken ct)
    {
        if (!TieneEpisodioSiguiente || AccionFinEpisodio == AccionFinEpisodioValores.PermanecerPausado)
        {
            return;
        }

        if (AccionFinEpisodio == AccionFinEpisodioValores.PausarYSalirFicha)
        {
            _ = GuardarProgresoActualAsync();
            Dispose();
            WeakReferenceMessenger.Default.Send(new NavegarMensaje_VolverDelReproductor());
            return;
        }

        if (AccionFinEpisodio == AccionFinEpisodioValores.AutoPlayInmediato)
        {
            var siguienteInmediato = ObtenerSiguienteEpisodio();
            string msg = siguienteInmediato != null
                ? string.Format(LocalizationService.T("Player_SiguienteEpisodioFormato"), siguienteInmediato.NumeroEpisodio)
                : LocalizationService.T("Player_SiguienteEpisodioGenerico");

            _ = WeakReferenceMessenger.Default.Send(new Messages.MostrarDialogoRequestMessage(
                "Auto-Play", msg, false, "FastForward", "#4CAF50"));

            await Task.Delay(1500, ct);
            if (!ct.IsCancellationRequested)
            {
                SiguienteEpisodio();
            }
            return;
        }

        // AccionFinEpisodioValores.AutoPlayCuentaAtras (default): cuenta atrás de 5s, cancelable.
        var siguiente = ObtenerSiguienteEpisodio();
        TituloSiguienteEnCuentaAtras = siguiente != null
            ? string.Format(LocalizationService.T("Player_SiguienteEpisodioFormato"), siguiente.NumeroEpisodio)
            : LocalizationService.T("Player_SiguienteEpisodioGenerico");
        SegundosCuentaAtrasSiguiente = 5;
        MostrarCuentaAtrasSiguiente = true;

        try
        {
            for (int i = 5; i > 0; i--)
            {
                await Task.Delay(1000, ct);
                if (!MostrarCuentaAtrasSiguiente) return; // el usuario canceló (CancelarAutoPlayCommand)
                SegundosCuentaAtrasSiguiente = i - 1;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        MostrarCuentaAtrasSiguiente = false;
        SiguienteEpisodio();
    }

    private async Task RastrearProgresoAsync(CancellationToken ct)
    {
        while (Player != null && !Player.IsDisposed && !ct.IsCancellationRequested)
        {
            try
            {
                // Red de seguridad: si el seek de reanudación nunca llega a aplicarse (archivo
                // que falla al abrir, nunca alcanza Status.Playing, etc.) no dejar el video
                // oculto indefinidamente.
                if (OcultarVideoInicio && DateTime.UtcNow - _ocultarVideoDesdeUtc > MaxOcultarVideoInicio)
                {
                    OcultarVideoInicio = false;
                }

                if (Player.Status == Status.Playing)
                {
                    double curSeconds = TimeSpan.FromTicks(Player.CurTime).TotalSeconds;
                    double durSeconds = TimeSpan.FromTicks(Player.Duration).TotalSeconds;

                    AplicarSeekDeArranqueDiferidoSiCorresponde(durSeconds);

                    if (!IsDraggingSlider)
                    {
                        ActualizarPosicionYDuracion(curSeconds, durSeconds);
                    }

                    if (PlayPauseIcon != "Pause") PlayPauseIcon = "Pause";

                    // Guardado continuo periódico cada 3 segundos: ya se persiste directo a SQLite
                    // (WAL, durable en cuanto termina el await) — este intervalo solo acota cuánto
                    // progreso se puede perder si la luz se va o Windows se reinicia a la fuerza
                    // entre guardado y guardado.
                    if (Math.Abs(curSeconds - _lastSavedSeconds) >= 3.0)
                    {
                        _lastSavedSeconds = curSeconds;
                        _ = GuardarProgresoActualAsync();
                    }

                    double porcentaje = durSeconds > 0 ? curSeconds / durSeconds : 0;

                    // PERF: cerca del final, pre-abrir (y descartar) el demuxer del siguiente episodio
                    // en un Demuxer aparte del Player en reproducción — adelanta el sondeo de
                    // contenedor/streams y calienta la caché de E/S de Windows para ese archivo, sin
                    // tocar en absoluto la reproducción actual. Como máximo una vez por episodio.
                    if (TieneEpisodioSiguiente && !EsEntornoPruebas())
                    {
                        _episodeNavigator.ConsiderarPrecarga(porcentaje, _siguienteEpisodioCache?.RutaCompleta);
                    }

                    // Auto-Tracking al umbral configurado (FUN-003: antes fijo en 90%)
                    if (porcentaje >= UmbralMarcadoVistoActual && !_fueMarcadoComoVisto)
                    {
                        await RealizarAutoTrackingAsync();
                    }

                    ProcesarDeteccionDeSkip(curSeconds);
                }
                else if (Player?.Status == Status.Ended && _haCompletadoOpen)
                {
                    await ManejarFinDeEpisodioAsync(ct);
                }

                // Sondeo adaptativo: 250ms mientras reproduce, 1000ms cuando está en pausa/detenido
                int delayMs = (Player?.Status == Status.Playing) ? 250 : 1000;
                await Task.Delay(delayMs, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("ReproductorViewModel", $"Error en bucle de progreso: {ex.Message}");
                await Task.Delay(1000, ct);
            }
        }
    }

    /// <summary>
    /// Seek de arranque diferido (reanudación o scrub del usuario durante la apertura). Se aplica
    /// aquí, con el video YA reproduciendo y la duración conocida: aplicar CurTime en
    /// OpenCompleted interrumpe la creación del contexto de video en algunos archivos (HEVC/VFR)
    /// y deja pantalla negra. Solo se consume el diferido del coordinador una vez que se conoce
    /// la duración: si aún no se conoce, se queda pendiente para un tick futuro en vez de perderse.
    /// </summary>
    private void AplicarSeekDeArranqueDiferidoSiCorresponde(double durSeconds)
    {
        double? seekDiferido = durSeconds > 0 ? _seekCoordinator.ConsumirSeekPendienteAlAbrir() : null;
        if (!(durSeconds > 0 && (seekDiferido.HasValue || _posicionInicioSegundos > 5))) return;

        bool esReanudacion = !seekDiferido.HasValue && _posicionInicioSegundos > 5;
        double posToSeek = seekDiferido ?? _posicionInicioSegundos;

        // FUN-006: nunca buscar más allá de la duración real del archivo que se
        // está reproduciendo (un archivo reemplazado por otro más corto haría
        // que el seek quedara fuera de rango y el video terminara al instante).
        if (durSeconds > 0 && posToSeek >= durSeconds)
        {
            posToSeek = Math.Max(0, durSeconds - 1.0);
        }

        _posicionInicioSegundos = 0;

        // Antes de disparar el seek nativo: congelar el repintado para que la
        // barra no "rebote" a la posición vieja mientras el seek se procesa.
        _seekCoordinator.IniciarVentanaDeSettle();
        _lastNotifiedSeconds = posToSeek;
        CurrentSeconds = posToSeek;

        _seekCoordinator.SolicitarSeek(Player, posToSeek, () => _haCompletadoOpen);

        if (esReanudacion)
        {
            var tPos = TimeSpan.FromSeconds(posToSeek);
            string tiempoFormateado = tPos.ToString(tPos.Hours > 0 ? @"hh\:mm\:ss" : @"mm\:ss");

            _ = WeakReferenceMessenger.Default.Send(new Messages.MostrarDialogoRequestMessage(
                LocalizationService.T("Player_ReanudarReproduccionTitulo"),
                string.Format(LocalizationService.T("Player_ContinuandoDesdeFormato"), tiempoFormateado),
                false, "PlaySpeed", "#2196F3"));
        }
    }

    /// <summary>Cachea la duración la primera vez, revela el video tras la ventana de settle, y
    /// notifica la posición actual si el cambio es significativo. Solo se llama sin arrastre.</summary>
    private void ActualizarPosicionYDuracion(double curSeconds, double durSeconds)
    {
        // Cachear la duración (no cambia durante la reproducción; independiente del settle)
        if (!_durationCached && durSeconds > 0)
        {
            TotalSeconds = durSeconds;
            TimeSpan tDur = TimeSpan.FromSeconds(durSeconds);
            TiempoTotalTexto = tDur.ToString(tDur.Hours > 0 ? @"hh\:mm\:ss" : @"mm\:ss");
            _durationCached = true;
            TiempoCombinadoTexto = $"{TiempoActualTexto} / {TiempoTotalTexto}";
        }

        // Durante la ventana de settle tras un seek, el reproductor aún reporta la
        // posición vieja: no repintar para que la barra no "rebote" hacia atrás.
        bool enSettleSeek = _seekCoordinator.EnVentanaDeSettle;

        // El seek de reanudación ya se asentó: revelar el video en el punto correcto.
        if (!enSettleSeek && OcultarVideoInicio)
        {
            OcultarVideoInicio = false;
        }

        // Solo notificar si el cambio es significativo (> 0.3s)
        if (!enSettleSeek && Math.Abs(curSeconds - _lastNotifiedSeconds) >= 0.3)
        {
            CurrentSeconds = curSeconds;
            _lastNotifiedSeconds = curSeconds;
            ActualizarTextosTiempo(curSeconds);
        }
    }

    /// <summary>Detección de Skip Intro/Outro con AniSkip: auto-salta o muestra el botón, según
    /// configuración, y limpia el estado del botón cuando no hay ningún skip activo.</summary>
    private void ProcesarDeteccionDeSkip(double curSeconds)
    {
        if (_skipTimes.Count == 0)
        {
            if (MostrarSkipButton)
            {
                MostrarSkipButton = false;
                MostrarSkipIntro = false;
                _currentActiveSkip = null;
            }
            return;
        }

        var skip = _skipCoordinator.ObtenerSkipActivo(curSeconds, _skipTimes, margenFinalSegundos: 0.5);
        if (skip == null)
        {
            if (MostrarSkipButton)
            {
                MostrarSkipButton = false;
                MostrarSkipIntro = false;
                _currentActiveSkip = null;
            }
            return;
        }

        string skipKey = $"{skip.SkipType}_{skip.Interval.StartTime:F1}";
        if (AutoSkipIntroOutro && !_skipAutoEjecutados.Contains(skipKey))
        {
            _skipAutoEjecutados.Add(skipKey);
            // FUN-007: el salto automático se acota al final del video
            // (igual que el manual) para no disparar Ended prematuramente.
            double destino = skip.Interval.EndTime + 0.2;
            if (TotalSeconds > 0 && destino > TotalSeconds) destino = TotalSeconds;
            Seek(destino);
            MostrarSkipButton = false;
            MostrarSkipIntro = false;
            _currentActiveSkip = null;

            _ = WeakReferenceMessenger.Default.Send(new Messages.MostrarDialogoRequestMessage(
                "AniSkip",
                string.Format(LocalizationService.T("Player_SkipAutoFormato"), skip.TextoBoton),
                false, skip.IconoBoton, "#2196F3"));
        }
        else if (!AutoSkipIntroOutro)
        {
            _currentActiveSkip = skip;
            SkipButtonTexto = $"{skip.TextoBoton} (S)";
            SkipButtonIcon = skip.IconoBoton;
            MostrarSkipButton = true;
            MostrarSkipIntro = true;
        }
    }

    /// <summary>Al terminar un episodio: resetea el progreso a 0, marca como visto si llegó al
    /// final real, y ejecuta la acción de fin de episodio configurada (una sola vez).</summary>
    private async Task ManejarFinDeEpisodioAsync(CancellationToken ct)
    {
        _smtc?.ActualizarEstadoReproduccion(false);

        // Al finalizar, resetear progreso a 0
        _ = GuardarProgresoActualAsync(forzarProgresoCero: true);

        // FUN-012: solo marcar como visto si se alcanzó el final REAL del archivo:
        // un video truncado/corrupto también dispara Ended antes de tiempo.
        bool llegoAlFinalReal = TotalSeconds > 0 && CurrentSeconds >= TotalSeconds * UmbralMarcadoVistoActual;
        if (!_fueMarcadoComoVisto && llegoAlFinalReal)
        {
            await RealizarAutoTrackingAsync();
        }

        // Acción configurable al terminar el episodio (Configuración → Reproducción)
        if (!_autoPlayEjecutado)
        {
            _autoPlayEjecutado = true;
            await EjecutarAccionFinEpisodioAsync(ct);
        }
    }

    [RelayCommand]
    public void SkipIntro()
    {
        SkipIntroOutro();
    }

    [RelayCommand]
    public void SkipIntroOutro()
    {
        var skipActivo = _currentActiveSkip ?? _skipCoordinator.ObtenerSkipActivo(CurrentSeconds, _skipTimes);

        if (skipActivo != null)
        {
            double destino = skipActivo.Interval.EndTime + 0.2;
            if (TotalSeconds > 0 && destino > TotalSeconds) destino = TotalSeconds;
            Seek(destino);
        }

        MostrarSkipButton = false;
        MostrarSkipIntro = false;
        _currentActiveSkip = null;
    }

    [RelayCommand]
    public void Cerrar()
    {
        EsModoMini = false;
        _windowModeCoordinator.SalirModoMini();
        _ = GuardarProgresoActualAsync();
        Dispose();
        
        // Navegar a la vista anterior (detalle del anime), no a la galería
        WeakReferenceMessenger.Default.Send(new NavegarMensaje_VolverDelReproductor());

        // Forzar el foco de vuelta a la ventana principal para que F11 funcione de inmediato
        System.Windows.Application.Current?.Dispatcher?.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Input,
            () =>
            {
                var window = System.Windows.Application.Current?.MainWindow;
                if (window != null)
                {
                    window.Focus();
                    System.Windows.Input.Keyboard.Focus(window);
                }
            });
    }

    private bool _disposeHecho;

    public void Dispose()
    {
        // Idempotente: Cerrar() dispone y OnVistaActualChanged también dispone al navegar fuera
        if (_disposeHecho) return;
        _disposeHecho = true;
        GC.SuppressFinalize(this);

        if (_smtc != null)
        {
            _smtc.PlayRequested -= OnSmtcPlayRequested;
            _smtc.PauseRequested -= OnSmtcPauseRequested;
            _smtc.NextRequested -= OnSmtcNextRequested;
            _smtc.PreviousRequested -= OnSmtcPreviousRequested;
            _smtc.Deshabilitar();
        }

        _ = GuardarProgresoActualAsync();

        _seekCoordinator.Dispose();

        try
        {
            _skipCts?.Cancel();
            _skipCts?.Dispose();
            _skipCts = null;
        }
        catch { }

        try
        {
            _trackingCts?.Cancel();
            _trackingCts?.Dispose();
        }
        catch { }
        _trackingCts = null;

        try
        {
            _episodeNavigator.Dispose();
        }
        catch { }

        if (Player != null)
        {
            try
            {
                if (Player.Status == Status.Playing)
                {
                    Player.Pause();
                }
                Player.Dispose();
            }
            catch (Exception ex)
            {
                AppLogger.Debug("ReproductorViewModel", $"Player dispose cleanup: {ex.Message}");
            }
            Player = null!;
        }

        if (_prioridadElevada)
        {
            _prioridadElevada = false;
            PrioridadReproduccion.Restaurar();
        }

        DetenerProteccionPantalla();
    }

    private bool _prioridadElevada;

    private readonly ILogrosService? _logrosService;

    /// <summary>
    /// Los avisos (toast) de la ventana principal quedan TAPADOS por el video: Flyleaf dibuja en su propia
    /// ventana nativa. La vista del reproductor los dibuja por su cuenta, encima del video, a partir de esto.
    /// </summary>
    public IDialogService? DialogService { get; }
}
