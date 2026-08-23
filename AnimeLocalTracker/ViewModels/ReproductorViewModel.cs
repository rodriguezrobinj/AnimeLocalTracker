using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using FlyleafLib;
using FlyleafLib.MediaPlayer;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;

namespace AnimeLocalTracker.ViewModels;

public partial class ReproductorViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsService? _settingsService;
    private readonly IPlaybackStateService _playbackState;
    private readonly ISkipTimesCoordinator _skipCoordinator;

    [ObservableProperty]
    private Player _player = null!;

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

    private string _episodioAnteriorTooltip = "Episodio anterior (P)";
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

    private List<EpisodioItem> _episodiosDisponibles = new();

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

    private int _animeId;
    public int AnimeId => _animeId;

    private int _episodio;
    public int Episodio => _episodio;

    private string _rutaVideo = string.Empty;
    public string RutaVideo => _rutaVideo;

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
        ISkipTimesCoordinator? skipTimesCoordinator = null)
    {
        _settingsService = settingsService;

        _playbackState = playbackStateService ?? new PlaybackStateService(databaseService, animeTrackingService, authService);
        _skipCoordinator = skipTimesCoordinator ?? new SkipTimesCoordinator(aniSkipService);

        if (_settingsService != null)
        {
            var config = _settingsService.ObtenerConfiguracion();
            if (config != null)
            {
                _autoSkipIntroOutro = config.AutoSkipIntroOutro;
                _subtitulosHabilitados = config.SubtitulosPorDefecto;
                _subtitulosIcon = config.SubtitulosPorDefecto ? "Subtitles" : "SubtitlesOutline";
            }
        }
    }

    private static readonly object _engineLock = new();
    private static bool _engineIniciado = false;

    // En entornos de pruebas (headless) Flyleaf puede dejar su hilo maestro bloqueado y
    // cualquier construcción posterior de Config() se cuelga en Dispatcher.Invoke síncrono.
    private static readonly bool _esEntornoPruebas = AppDomain.CurrentDomain.GetAssemblies()
        .Any(a => a.GetName().Name?.StartsWith("xunit", StringComparison.OrdinalIgnoreCase) == true);

    public virtual Player CreateOptimizedPlayer()
    {
        if (_esEntornoPruebas)
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
            
            // Seeking rápido por keyframe (no frame-accurate)
            if (config.Player != null)
            {
                config.Player.SeekAccurate = false;
            }
            
            if (config.Subtitles != null)
            {
                config.Subtitles.Enabled = SubtitulosHabilitados;
            }
            
            var player = new Player(config);
            player.OpenCompleted += (s, e) => EvaluarSubtitulosPorDefecto();
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
            Player.Pause();
            PlayPauseIcon = "Play";
            _ = GuardarProgresoActualAsync();
        }
        else
        {
            // Si el episodio terminó, reproducir de nuevo desde el inicio
            if (Player.Status == Status.Ended)
            {
                try { Player.CurTime = 0; } catch (Exception ex) { AppLogger.Debug("ReproductorViewModel", $"No se pudo reiniciar posición: {ex.Message}"); }
                _fueMarcadoComoVisto = false;
            }
            Player.Play();
            PlayPauseIcon = "Pause";
        }
    }
    
    [RelayCommand]
    public void Rewind10()
    {
        double newSeconds = Math.Max(0, CurrentSeconds - 10);
        Seek(newSeconds);
    }
    
    [RelayCommand]
    public void Forward10()
    {
        double max = TotalSeconds > 0 ? TotalSeconds : double.MaxValue;
        double newSeconds = Math.Min(max, CurrentSeconds + 10);
        Seek(newSeconds);
    }

    // === Scrubbing de la línea de tiempo ===
    private double _posicionAntesArrastre;
    private long _ultimoScrubSeekTicks;
    private DateTime _settleHastaUtc = DateTime.MinValue;

    // Tras un seek, el reproductor tarda unos ms en reportar la nueva posición;
    // durante esa ventana el bucle de tracking no repinta la posición para evitar rebotes.
    private static readonly TimeSpan VentanaSettleSeek = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan IntervaloScrubEnVivo = TimeSpan.FromMilliseconds(250);

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
    /// o usa los atajos de teclado. Aplica el seek y congela el repintado brevemente (anti-rebote).
    /// </summary>
    [RelayCommand]
    public void Seek(double seconds)
    {
        seconds = AcotarPosicion(seconds);

        if (Player != null)
        {
            try
            {
                Player.CurTime = TimeSpan.FromSeconds(seconds).Ticks;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("ReproductorViewModel", $"Excepción al ajustar posición nativa del reproductor: {ex.Message}");
            }
        }

        // Actualizar UI inmediatamente para feedback instantáneo
        _settleHastaUtc = DateTime.UtcNow + VentanaSettleSeek;
        _lastNotifiedSeconds = seconds;
        CurrentSeconds = seconds;
        ActualizarTextosTiempo(seconds);
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
    /// Durante el arrastre: feedback visual instantáneo (thumb + tiempo) y scrub en vivo con throttling.
    /// El seek en vivo usa el seek rápido por keyframe de Flyleaf, así que no bloquea la UI.
    /// </summary>
    public void VistaPreviaArrastre(double segundos)
    {
        if (!IsDraggingSlider) return;

        segundos = AcotarPosicion(segundos);
        if (TotalSeconds > 0 && segundos > TotalSeconds) segundos = TotalSeconds;

        CurrentSeconds = segundos;
        ActualizarTextosTiempo(segundos);

        // Throttle del seek en vivo a ~4 fps para no saturar el demuxer/decodificador
        long ahoraTicks = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(_ultimoScrubSeekTicks) >= IntervaloScrubEnVivo)
        {
            _ultimoScrubSeekTicks = ahoraTicks;
            if (Player != null && !Player.IsDisposed)
            {
                try
                {
                    Player.CurTime = TimeSpan.FromSeconds(segundos).Ticks;
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("ReproductorViewModel", $"Scrub seek throttling catch: {ex.Message}");
                }
            }
        }
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
        var mainWindow = System.Windows.Application.Current?.MainWindow as AnimeLocalTracker.Views.MainWindow;
        if (mainWindow != null)
        {
            mainWindow.TogglePantallaCompleta();
            FullscreenIcon = mainWindow.IsFullScreen ? "FullscreenExit" : "Fullscreen";

            // Devolver foco a MainWindow para que las teclas sigan respondiendo
            System.Windows.Application.Current?.Dispatcher?.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Input,
                () =>
                {
                    mainWindow.Focus();
                    System.Windows.Input.Keyboard.Focus(mainWindow);
                });
        }
    }

    [RelayCommand]
    public void SelectSubtitleStream(object stream)
    {
        if (Player == null || stream == null) return;
        
        HabilitarSubtitulos();
        Player.OpenAsync((dynamic)stream);
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
            bool permitirSubtitulos = true;
            if (_settingsService != null)
            {
                var config = _settingsService.ObtenerConfiguracion();
                if (config != null)
                {
                    permitirSubtitulos = config.SubtitulosPorDefecto;
                }
            }

            if (!permitirSubtitulos)
            {
                DeshabilitarSubtitulos();
                return;
            }

            if (Player?.Subtitles?.Streams == null || Player.Subtitles.Streams.Count == 0)
            {
                DeshabilitarSubtitulos();
                return;
            }

            HabilitarSubtitulos();
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
        if (Player?.Config?.Subtitles != null)
        {
            Player.Config.Subtitles.Enabled = false;
        }
    }

    public void HabilitarSubtitulos()
    {
        SubtitulosHabilitados = true;
        SubtitulosIcon = "Subtitles";
        if (Player?.Config?.Subtitles != null)
        {
            Player.Config.Subtitles.Enabled = true;
        }
    }

    public EpisodioItem? ObtenerSiguienteEpisodio()
    {
        return _episodiosDisponibles
            .Where(e => e.NumeroEpisodio > _episodio && (!string.IsNullOrWhiteSpace(e.RutaCompleta)))
            .OrderBy(e => e.NumeroEpisodio)
            .FirstOrDefault();
    }

    public EpisodioItem? ObtenerAnteriorEpisodio()
    {
        return _episodiosDisponibles
            .Where(e => e.NumeroEpisodio < _episodio && (!string.IsNullOrWhiteSpace(e.RutaCompleta)))
            .OrderByDescending(e => e.NumeroEpisodio)
            .FirstOrDefault();
    }

    public void ActualizarEstadosNavegacionEpisodios()
    {
        var siguiente = ObtenerSiguienteEpisodio();
        TieneEpisodioSiguiente = siguiente != null && !string.IsNullOrWhiteSpace(siguiente.RutaCompleta);
        EpisodioSiguienteTooltip = TieneEpisodioSiguiente 
            ? $"Siguiente: Episodio {siguiente!.NumeroEpisodio} (N)" 
            : "No hay siguiente episodio";

        var anterior = ObtenerAnteriorEpisodio();
        TieneEpisodioAnterior = anterior != null && !string.IsNullOrWhiteSpace(anterior.RutaCompleta);
        EpisodioAnteriorTooltip = TieneEpisodioAnterior 
            ? $"Anterior: Episodio {anterior!.NumeroEpisodio} (P)" 
            : "No hay episodio anterior";
    }

    [RelayCommand]
    public void SiguienteEpisodio()
    {
        var siguiente = ObtenerSiguienteEpisodio();
        if (siguiente != null && !string.IsNullOrWhiteSpace(siguiente.RutaCompleta))
        {
            CargarVideo(siguiente.RutaCompleta, _animeId, TituloAnime, siguiente.NumeroEpisodio, _episodiosDisponibles);
        }
    }

    [RelayCommand]
    public void AnteriorEpisodio()
    {
        var anterior = ObtenerAnteriorEpisodio();
        if (anterior != null && !string.IsNullOrWhiteSpace(anterior.RutaCompleta))
        {
            CargarVideo(anterior.RutaCompleta, _animeId, TituloAnime, anterior.NumeroEpisodio, _episodiosDisponibles);
        }
    }

    private CancellationTokenSource? _trackingCts;

    public async Task VerificarProgresoPrevioAsync(int animeId, int episodio)
    {
        try
        {
            var previo = await _playbackState.ObtenerPosicionParaReanudarAsync(animeId, episodio);
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
            var results = await _skipCoordinator.CargarSkipTimesAsync(animeId, episodio, TotalSeconds, ct);
            if (results.Count > 0)
            {
                _skipTimes = new List<AniSkipResult>(results);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ReproductorViewModel", $"Error cargando skip times de AniSkip: {ex.Message}");
        }
    }

    public void CargarVideo(string rutaVideo, int animeId, string tituloAnime, int episodio, List<EpisodioItem>? listaEpisodios = null)
    {
        _ = GuardarProgresoActualAsync();

        _skipTimes.Clear();
        _skipAutoEjecutados.Clear();
        _currentActiveSkip = null;
        MostrarSkipButton = false;
        MostrarSkipIntro = false;

        if (_settingsService != null)
        {
            var config = _settingsService.ObtenerConfiguracion();
            if (config != null)
            {
                AutoSkipIntroOutro = config.AutoSkipIntroOutro;
                SubtitulosHabilitados = config.SubtitulosPorDefecto;
                SubtitulosIcon = config.SubtitulosPorDefecto ? "Subtitles" : "SubtitlesOutline";
            }
        }

        _rutaVideo = rutaVideo;
        _animeId = animeId;
        _episodio = episodio;
        TituloAnime = tituloAnime;
        TituloEpisodio = $"Episodio {episodio}";
        _fueMarcadoComoVisto = false;
        _durationCached = false;
        _lastNotifiedSeconds = -1;
        _lastSavedSeconds = -1;
        _resumingPositionSeconds = 0;

        if (listaEpisodios != null)
        {
            _episodiosDisponibles = listaEpisodios
                .Where(e => !string.IsNullOrWhiteSpace(e.RutaCompleta))
                .OrderBy(e => e.NumeroEpisodio)
                .ToList();
        }

        ActualizarEstadosNavegacionEpisodios();

        // Cancelar rastreo previo
        _trackingCts?.Cancel();
        _trackingCts?.Dispose();
        _trackingCts = new CancellationTokenSource();

        // Consultar progreso previo en la base de datos
        _ = VerificarProgresoPrevioAsync(animeId, episodio);

        // Cargar marcas de skip de AniSkip en segundo plano
        _ = CargarSkipTimesAsync(animeId, episodio, _trackingCts.Token);

        // Sincronizar ícono de fullscreen con el estado actual de la ventana
        try
        {
            if (System.Windows.Application.Current != null && 
                System.Windows.Application.Current.Dispatcher.CheckAccess() &&
                System.Windows.Application.Current.MainWindow is AnimeLocalTracker.Views.MainWindow mainWindow)
            {
                FullscreenIcon = mainWindow.IsFullScreen ? "FullscreenExit" : "Fullscreen";
            }
        }
        catch
        {
            // Entornos de pruebas sin Dispatcher o en subprocesos en segundo plano
        }

        // Mantener la instancia existente de Player para no destruir la superficie DirectX de FlyleafHost
        if (Player == null || Player.IsDisposed)
        {
            Player = CreateOptimizedPlayer();
        }
        else
        {
            try
            {
                if (Player.Status == Status.Playing)
                {
                    Player.Pause();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug("ReproductorViewModel", $"Player pause antes de cambiar archivo: {ex.Message}");
            }
        }

        if (Player != null)
        {
            Player.OpenAsync(rutaVideo);
            Player.Play();
        }

        _ = RastrearProgresoAsync(_trackingCts.Token);
    }

    public async Task GuardarProgresoActualAsync(bool forzarProgresoCero = false)
    {
        if (_animeId <= 0 || _episodio <= 0) return;

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
                AnimeId = _animeId,
                NumeroEpisodio = _episodio,
                RutaVideo = _rutaVideo,
                PosicionSegundos = curSec,
                DuracionSegundos = durSec,
                ForzarProgresoCero = forzarProgresoCero,
                FueMarcadoComoVisto = _fueMarcadoComoVisto
            });

            // Notificar a DetalleViewModel para actualizar la barra de progreso en vivo
            WeakReferenceMessenger.Default.Send(new Messages.EpisodioActualizadoMensaje(
                _animeId, _episodio, _fueMarcadoComoVisto, resultado.ProgresoSegundos, resultado.TotalSegundos));
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ReproductorViewModel", $"Error al guardar progreso actual: {ex.Message}");
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
                $"Episodio {_episodio} marcado como visto.",
                false, "CheckCircle", "#4CAF50"));

            // Avisar a la vista de detalles para que actualice la lista automáticamente
            WeakReferenceMessenger.Default.Send(new Messages.EpisodioActualizadoMensaje(_animeId, _episodio, true, 0, TotalSeconds));
        }
        catch (Exception ex)
        {
            AppLogger.Error("ReproductorViewModel", "Error en auto-tracking", ex);
        }
    }

    private async Task RastrearProgresoAsync(CancellationToken ct)
    {
        while (Player != null && !Player.IsDisposed && !ct.IsCancellationRequested)
        {
            try
            {
                if (Player.Status == Status.Playing)
                {
                    double curSeconds = TimeSpan.FromTicks(Player.CurTime).TotalSeconds;
                    double durSeconds = TimeSpan.FromTicks(Player.Duration).TotalSeconds;

                    // Reanudación automática desde última posición guardada
                    if (_resumingPositionSeconds > 5 && (durSeconds > 0 || curSeconds >= 0))
                    {
                        double posToSeek = _resumingPositionSeconds;
                        _resumingPositionSeconds = 0; // Solo una vez
                        
                        try
                        {
                            Player.CurTime = TimeSpan.FromSeconds(posToSeek).Ticks;
                            _settleHastaUtc = DateTime.UtcNow + VentanaSettleSeek;
                            _lastNotifiedSeconds = posToSeek;
                            CurrentSeconds = posToSeek;
                            
                            var tPos = TimeSpan.FromSeconds(posToSeek);
                            string tiempoFormateado = tPos.ToString(tPos.Hours > 0 ? @"hh\:mm\:ss" : @"mm\:ss");
                            
                            _ = WeakReferenceMessenger.Default.Send(new Messages.MostrarDialogoRequestMessage(
                                "Reanudar Reproducción", 
                                $"Continuando desde {tiempoFormateado}", 
                                false, "PlaySpeed", "#2196F3"));
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Debug("ReproductorViewModel", $"Error aplicando seek de reanudación: {ex.Message}");
                        }
                    }
                    
                    if (!IsDraggingSlider)
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
                        bool enSettleSeek = DateTime.UtcNow < _settleHastaUtc;

                        // Solo notificar si el cambio es significativo (> 0.3s)
                        if (!enSettleSeek && Math.Abs(curSeconds - _lastNotifiedSeconds) >= 0.3)
                        {
                            CurrentSeconds = curSeconds;
                            _lastNotifiedSeconds = curSeconds;
                            ActualizarTextosTiempo(curSeconds);
                        }
                    }

                    if (PlayPauseIcon != "Pause") PlayPauseIcon = "Pause";

                    // Guardado continuo periódico cada 5 segundos
                    if (Math.Abs(curSeconds - _lastSavedSeconds) >= 5.0)
                    {
                        _lastSavedSeconds = curSeconds;
                        _ = GuardarProgresoActualAsync();
                    }

                    double porcentaje = durSeconds > 0 ? curSeconds / durSeconds : 0;

                    // Auto-Tracking al 90%
                    if (porcentaje >= 0.90 && !_fueMarcadoComoVisto)
                    {
                        await RealizarAutoTrackingAsync();
                    }
                    
                    // Detección de Skip Intro / Outro con AniSkip
                    if (_skipTimes.Count > 0)
                    {
                        var skip = _skipCoordinator.ObtenerSkipActivo(curSeconds, _skipTimes, margenFinalSegundos: 0.5);
                        if (skip != null)
                        {
                            string skipKey = $"{skip.SkipType}_{skip.Interval.StartTime:F1}";
                            if (AutoSkipIntroOutro && !_skipAutoEjecutados.Contains(skipKey))
                            {
                                _skipAutoEjecutados.Add(skipKey);
                                Seek(skip.Interval.EndTime + 0.2);
                                MostrarSkipButton = false;
                                MostrarSkipIntro = false;
                                _currentActiveSkip = null;

                                _ = WeakReferenceMessenger.Default.Send(new Messages.MostrarDialogoRequestMessage(
                                    "AniSkip",
                                    $"{skip.TextoBoton} automáticamente.",
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
                        else
                        {
                            if (MostrarSkipButton)
                            {
                                MostrarSkipButton = false;
                                MostrarSkipIntro = false;
                                _currentActiveSkip = null;
                            }
                        }
                    }
                    else
                    {
                        if (MostrarSkipButton)
                        {
                            MostrarSkipButton = false;
                            MostrarSkipIntro = false;
                            _currentActiveSkip = null;
                        }
                    }
                }
                else if (Player?.Status == Status.Ended)
                {
                    // Al finalizar, resetear progreso a 0
                    _ = GuardarProgresoActualAsync(forzarProgresoCero: true);
                    if (!_fueMarcadoComoVisto)
                    {
                        await RealizarAutoTrackingAsync();
                    }
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

    public void Dispose()
    {
        _ = GuardarProgresoActualAsync();

        try
        {
            _trackingCts?.Cancel();
        }
        catch { }
        _trackingCts = null;

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
    }
}
