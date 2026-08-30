using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using FlyleafLib;
using FlyleafLib.MediaPlayer;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;

namespace AnimeLocalTracker.ViewModels;

[System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
public partial class ReproductorViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsService? _settingsService;
    private readonly IPlaybackStateService _playbackState;
    private readonly ISkipTimesCoordinator _skipCoordinator;
    private CancellationTokenSource? _skipCts;

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

    [ObservableProperty]
    private bool _autoPlaySiguiente = true;

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
    private double _seekPendienteAlAbrir = -1;

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
                _autoPlaySiguiente = config.AutoPlaySiguiente;
                _subtitulosHabilitados = config.SubtitulosPorDefecto;
                _subtitulosIcon = config.SubtitulosPorDefecto ? "Subtitles" : "SubtitlesOutline";
            }
        }
    }

    private void OnVolumenChanged(int value)
    {
        if (Player?.Audio != null)
        {
            try
            {
                Player.Audio.Volume = value;
                if (value > 0 && IsMuted)
                {
                    IsMuted = false;
                    Player.Audio.Mute = false;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug("ReproductorViewModel", $"Error asignando volumen: {ex.Message}");
            }
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
            if (Player?.Audio != null)
            {
                try
                {
                    Player.Audio.Mute = false;
                    Player.Audio.Volume = Volumen;
                }
                catch { }
            }
        }
        else
        {
            _volumenPrevioMute = Volumen;
            IsMuted = true;
            if (Player?.Audio != null)
            {
                try
                {
                    Player.Audio.Mute = true;
                }
                catch { }
            }
        }
        ActualizarVolumenIcon();
    }

    private void ActualizarVolumenIcon()
    {
        if (IsMuted || Volumen == 0)
        {
            VolumenIcon = "VolumeOff";
        }
        else if (Volumen < 35)
        {
            VolumenIcon = "VolumeLow";
        }
        else if (Volumen < 70)
        {
            VolumenIcon = "VolumeMedium";
        }
        else
        {
            VolumenIcon = "VolumeHigh";
        }
    }

    private static readonly object _engineLock = new();
    private static bool _engineIniciado = false;

    // En entornos de pruebas (headless) Flyleaf puede dejar su hilo maestro bloqueado y
    // cualquier construcción posterior de Config() se cuelga en Dispatcher.Invoke síncrono.
    private static readonly bool _esEntornoPruebas = AppDomain.CurrentDomain.GetAssemblies()
        .Any(a => a.GetName().Name?.StartsWith("xunit", StringComparison.OrdinalIgnoreCase) == true);

    public void AsegurarPlayerInicializado()
    {
        if (Player == null || Player.IsDisposed)
        {
            Player = CreateOptimizedPlayer();
        }
    }

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
            
            // 1. Seeking rápido instantáneo por Keyframe (no frame-accurate) -> 0 ms seek latency
            if (config.Player != null)
            {
                config.Player.SeekAccurate = false;
                config.Player.AutoPlay = true;
            }

            // 2. Decoder multi-hilos para decodificación suave de AV1 y HEVC 10-bit
            if (config.Decoder != null)
            {
                config.Decoder.VideoThreads = Math.Max(2, Environment.ProcessorCount / 2);
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
            
            var player = new Player(config);
            player.OpenCompleted += (s, e) =>
            {
                _haCompletadoOpen = true;
                EvaluarSubtitulosPorDefecto();

                try
                {
                    if (player.Status != Status.Playing)
                    {
                        player.Play();
                    }
                    PlayPauseIcon = "Pause";
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

                // IMPORTANTE: NO seekear aquí (ni _seekPendienteAlAbrir ni reanudación).
                // En este punto el decoder de video aún está creando su contexto de
                // renderizado y un Player.CurTime inmediato interrumpe ese proceso
                // en algunos archivos (HEVC/VFR) dejando la pantalla en negro con
                // audio avanzando. El bucle de tracking aplica el seek por
                // SolicitarSeekNativo cuando el video ya está reproduciendo de verdad.
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
    private DateTime _settleHastaUtc = DateTime.MinValue;

    // Tras un seek, el reproductor tarda unos ms en reportar la nueva posición;
    // durante esa ventana el bucle de tracking no repinta la posición para evitar rebotes.
    private static readonly TimeSpan VentanaSettleSeek = TimeSpan.FromMilliseconds(900);

    // === Coalescing de seeks (último-gana) ===
    // Enviar CurTime a Flyleaf más rápido de lo que él procesa los seeks los ENCOLA y pueden
    // aplicarse FUERA DE ORDEN: el video "no se coloca y vuelve donde estaba". Con un intervalo
    // mínimo entre seeks y aplicando siempre el objetivo MÁS RECIENTE, el orden queda garantizado.
    private double _seekPendiente = -1;
    private DateTime _ultimoSeekAplicadoUtc = DateTime.MinValue;
    private CancellationTokenSource? _seekDebounceCts;
    private static readonly TimeSpan IntervaloMinimoSeek = TimeSpan.FromMilliseconds(250);

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
        _settleHastaUtc = DateTime.UtcNow + VentanaSettleSeek;
        _lastNotifiedSeconds = seconds;
        CurrentSeconds = seconds;
        ActualizarTextosTiempo(seconds);

        SolicitarSeekNativo(seconds);
    }

    /// <summary>
    /// Punto único de entrada al seek nativo. Aplica de inmediato si pasó el intervalo mínimo;
    /// si no, guarda el objetivo MÁS RECIENTE y lo aplica al vencer el intervalo (último-gana).
    /// </summary>
    private void SolicitarSeekNativo(double segundos)
    {
        if (Player == null || Player.IsDisposed) return;

        var transcurrido = DateTime.UtcNow - _ultimoSeekAplicadoUtc;
        if (transcurrido >= IntervaloMinimoSeek)
        {
            AplicarSeekNativo(segundos);
            return;
        }

        _seekPendiente = segundos;

        _seekDebounceCts?.Cancel();
        _seekDebounceCts?.Dispose();
        _seekDebounceCts = new CancellationTokenSource();
        var ct = _seekDebounceCts.Token;
        var restante = IntervaloMinimoSeek - transcurrido;
        if (restante < TimeSpan.Zero) restante = TimeSpan.Zero;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(restante, ct);
                double objetivo = Interlocked.Exchange(ref _seekPendiente, -1);
                if (objetivo >= 0 && !ct.IsCancellationRequested)
                {
                    AplicarSeekNativo(objetivo);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AppLogger.Debug("ReproductorViewModel", $"Error en seek coalescido: {ex.Message}");
            }
        }, ct);
    }

    private void AplicarSeekNativo(double segundos)
    {
        if (Player == null || Player.IsDisposed) return;

        // Si el reproductor todavía se está abriendo o no ha completado la inicialización inicial del decodificador,
        // no invocar Player.CurTime de inmediato (interrumpe la creación del contexto de video en FFmpeg/Flyleaf
        // dejando la pantalla negra). Guardamos la posición para aplicarla en cuanto OpenCompleted se active.
        if (!_haCompletadoOpen || Player.Status == Status.Opening || Player.Status == Status.Stopped)
        {
            _seekPendienteAlAbrir = segundos;
            return;
        }

        _ultimoSeekAplicadoUtc = DateTime.UtcNow;
        try
        {
            Player.CurTime = TimeSpan.FromSeconds(segundos).Ticks;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("ReproductorViewModel", $"Excepción al ajustar posición nativa del reproductor: {ex.Message}");
        }
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

        SolicitarSeekNativo(segundos);
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
            // AniSkip API como fuente primaria; si no hay datos, detección local por escenas (Python/ffmpeg)
            // usando la ruta del video local actual (requiere un archivo en disco).
            var results = await _skipCoordinator.CargarSkipTimesAsync(animeId, episodio, TotalSeconds, ct, RutaVideo);
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

    public void CargarVideo(string rutaVideo, int animeId, string tituloAnime, int episodio, List<EpisodioItem>? listaEpisodios = null)
    {
        _ = CargarVideoAsync(rutaVideo, animeId, tituloAnime, episodio, listaEpisodios);
    }

    public async Task CargarVideoAsync(string rutaVideo, int animeId, string tituloAnime, int episodio, List<EpisodioItem>? listaEpisodios = null)
    {
        _ = GuardarProgresoActualAsync();

        // Descartar seeks coalescidos pendientes del episodio anterior
        CancelarSeekPendiente();

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

        if (_settingsService != null)
        {
            var config = _settingsService.ObtenerConfiguracion();
            if (config != null)
            {
                AutoSkipIntroOutro = config.AutoSkipIntroOutro;
                AutoPlaySiguiente = config.AutoPlaySiguiente;
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
        _posicionInicioSegundos = 0;
        _haCompletadoOpen = false;
        _seekPendienteAlAbrir = -1;

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

        // 1. Asegurar que Player existe antes de configurar el nuevo archivo
        AsegurarPlayerInicializado();

        if (Player != null && (Player.Status == Status.Playing || Player.Status == Status.Paused))
        {
            try
            {
                Player.Stop();
            }
            catch (Exception ex)
            {
                AppLogger.Debug("ReproductorViewModel", $"Player stop antes de cambiar archivo: {ex.Message}");
            }
        }

        // 2. Obtener progreso previo ANTES de abrir/reproducir para que comience de inmediato donde se dejó
        await VerificarProgresoPrevioAsync(animeId, episodio);
        _posicionInicioSegundos = _resumingPositionSeconds;

        // 3. Cargar marcas de skip de AniSkip en segundo plano
        _ = CargarSkipTimesAsync(animeId, episodio, currentSkipCts.Token);

        // 4. Sincronizar ícono de fullscreen con el estado actual de la ventana
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

        if (Player != null)
        {
            Player.OpenAsync(rutaVideo);
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

                    // Seek de arranque diferido (reanudación o scrub del usuario durante la
                    // apertura). Se aplica aquí, con el video YA reproduciendo y la duración
                    // conocida: aplicar CurTime en OpenCompleted interrumpe la creación del
                    // contexto de video en algunos archivos (HEVC/VFR) y deja pantalla negra.
                    if (durSeconds > 0 && (_seekPendienteAlAbrir >= 0 || _posicionInicioSegundos > 5))
                    {
                        bool esReanudacion = _seekPendienteAlAbrir < 0 && _posicionInicioSegundos > 5;
                        double posToSeek = _seekPendienteAlAbrir >= 0 ? _seekPendienteAlAbrir : _posicionInicioSegundos;
                        _seekPendienteAlAbrir = -1;
                        _posicionInicioSegundos = 0;

                        // Antes de disparar el seek nativo: congelar el repintado para que la
                        // barra no "rebote" a la posición vieja mientras el seek se procesa.
                        _settleHastaUtc = DateTime.UtcNow + VentanaSettleSeek;
                        _lastNotifiedSeconds = posToSeek;
                        CurrentSeconds = posToSeek;

                        SolicitarSeekNativo(posToSeek);

                        if (esReanudacion)
                        {
                            var tPos = TimeSpan.FromSeconds(posToSeek);
                            string tiempoFormateado = tPos.ToString(tPos.Hours > 0 ? @"hh\:mm\:ss" : @"mm\:ss");

                            _ = WeakReferenceMessenger.Default.Send(new Messages.MostrarDialogoRequestMessage(
                                "Reanudar Reproducción",
                                $"Continuando desde {tiempoFormateado}",
                                false, "PlaySpeed", "#2196F3"));
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

                    // Auto-Play al siguiente episodio
                    if (AutoPlaySiguiente && TieneEpisodioSiguiente && !_autoPlayEjecutado)
                    {
                        _autoPlayEjecutado = true;

                        var siguiente = ObtenerSiguienteEpisodio();
                        string msg = siguiente != null 
                            ? $"Reproduciendo Episodio {siguiente.NumeroEpisodio}..." 
                            : "Reproduciendo siguiente episodio...";

                        _ = WeakReferenceMessenger.Default.Send(new Messages.MostrarDialogoRequestMessage(
                            "Auto-Play",
                            msg,
                            false, "FastForward", "#4CAF50"));

                        await Task.Delay(1500, ct);
                        if (!ct.IsCancellationRequested)
                        {
                            System.Windows.Application.Current?.Dispatcher?.Invoke(() => SiguienteEpisodio());
                        }
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

    private bool _disposeHecho;

    public void Dispose()
    {
        // Idempotente: Cerrar() dispone y OnVistaActualChanged también dispone al navegar fuera
        if (_disposeHecho) return;
        _disposeHecho = true;

        _ = GuardarProgresoActualAsync();

        CancelarSeekPendiente();

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

    private void CancelarSeekPendiente()
    {
        Interlocked.Exchange(ref _seekPendiente, -1);
        try { _seekDebounceCts?.Cancel(); } catch { }
        _seekDebounceCts?.Dispose();
        _seekDebounceCts = null;
    }
}
