using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using FlyleafLib;
using FlyleafLib.MediaPlayer;
using AnimeLocalTracker.Messages;

namespace AnimeLocalTracker.ViewModels;

public partial class ReproductorViewModel : ObservableObject, IDisposable
{
    private readonly IDatabaseService _databaseService;
    private readonly IAnimeTrackingService _animeTrackingService;
    private readonly IAuthService _authService;

    [ObservableProperty]
    private Player _player = null!;

    [ObservableProperty]
    private string _tituloAnime = string.Empty;

    [ObservableProperty]
    private string _tituloEpisodio = string.Empty;

    [ObservableProperty]
    private bool _mostrarSkipIntro;

    // Propiedades para controles de medios
    [ObservableProperty] private double _currentSeconds;
    [ObservableProperty] private double _totalSeconds;
    [ObservableProperty] private string _tiempoActualTexto = "00:00";
    [ObservableProperty] private string _tiempoTotalTexto = "00:00";
    [ObservableProperty] private string _tiempoCombinadoTexto = "00:00 / 00:00";
    [ObservableProperty] private string _playPauseIcon = "Pause";
    [ObservableProperty] private bool _isDraggingSlider = false;
    
    // Navegación entre episodios y AutoPlay
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

    private bool _autoPlaySiguiente = true;
    public bool AutoPlaySiguiente
    {
        get => _autoPlaySiguiente;
        set => SetProperty(ref _autoPlaySiguiente, value);
    }

    private string _autoPlayIcon = "MotionPlay";
    public string AutoPlayIcon
    {
        get => _autoPlayIcon;
        set => SetProperty(ref _autoPlayIcon, value);
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
    private bool _autoPlayDisparado = false;

    private string _fullscreenIcon = "Fullscreen";
    public string FullscreenIcon
    {
        get => _fullscreenIcon;
        set => SetProperty(ref _fullscreenIcon, value);
    }
    
    private string _subtitulosIcon = "Subtitles";
    public string SubtitulosIcon
    {
        get => _subtitulosIcon;
        set => SetProperty(ref _subtitulosIcon, value);
    }
    
    private bool _subtitulosHabilitados = true;
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

    public ReproductorViewModel(IDatabaseService databaseService, IAnimeTrackingService animeTrackingService, IAuthService authService)
    {
        _databaseService = databaseService;
        _animeTrackingService = animeTrackingService;
        _authService = authService;
    }

    private static readonly object _engineLock = new();
    private static bool _engineIniciado = false;

    public virtual Player CreateOptimizedPlayer()
    {
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
            
            // Subtítulos habilitados por defecto
            if (config.Subtitles != null)
            {
                config.Subtitles.Enabled = SubtitulosHabilitados;
            }
            
            return new Player(config);
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

    // Comando para cuando el usuario suelta el slider (Thumb.DragCompleted) o click directo
    [RelayCommand]
    public void Seek(double seconds)
    {
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
        CurrentSeconds = seconds;
        var t = TimeSpan.FromSeconds(seconds);
        TiempoActualTexto = t.ToString(t.Hours > 0 ? @"hh\:mm\:ss" : @"mm\:ss");
        TiempoCombinadoTexto = $"{TiempoActualTexto} / {TiempoTotalTexto}";
    }

    [ObservableProperty] private int _volumen = 100;
    [ObservableProperty] private string _volumenIcon = "VolumeHigh";

    partial void OnVolumenChanged(int value)
    {
        if (Player != null)
        {
            try 
            {
                Player.Audio.Volume = value;
            } 
            catch (Exception ex) 
            {
                AppLogger.Warn("ReproductorViewModel", $"Excepción al cambiar volumen nativo: {ex.Message}");
            }
        }
        
        if (value == 0) VolumenIcon = "VolumeMute";
        else if (value < 30) VolumenIcon = "VolumeLow";
        else if (value < 70) VolumenIcon = "VolumeMedium";
        else VolumenIcon = "VolumeHigh";
    }

    private int _volumenAnterior = 100;

    [RelayCommand]
    public void ToggleMute()
    {
        if (Volumen > 0)
        {
            _volumenAnterior = Volumen;
            Volumen = 0;
        }
        else
        {
            Volumen = _volumenAnterior > 0 ? _volumenAnterior : 100;
        }
    }

    [RelayCommand]
    public void ToggleFullscreen()
    {
        if (System.Windows.Application.Current?.MainWindow is AnimeLocalTracker.Views.MainWindow mainWindow)
        {
            mainWindow.TogglePantallaCompleta();
            
            // Actualizar ícono según el estado actual
            FullscreenIcon = mainWindow.IsFullScreen ? "FullscreenExit" : "Fullscreen";
            
            // Forzar foco de vuelta a la ventana para que F11 siga funcionando
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
        
        SubtitulosHabilitados = true;
        Player.Config.Subtitles.Enabled = true;
        Player.OpenAsync((dynamic)stream);
        SubtitulosIcon = "Subtitles";
    }

    [RelayCommand]
    public void TurnOffSubtitles()
    {
        if (Player == null) return;
        
        SubtitulosHabilitados = false;
        Player.Config.Subtitles.Enabled = false;
        SubtitulosIcon = "SubtitlesOutline";
    }

    [RelayCommand]
    public void ToggleAutoPlay()
    {
        AutoPlaySiguiente = !AutoPlaySiguiente;
        AutoPlayIcon = AutoPlaySiguiente ? "MotionPlay" : "MotionPlayOff";
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
            var registros = await _databaseService.ObtenerRegistrosPorAnimeAsync(animeId);
            var reg = registros?.FirstOrDefault(r => r.NumeroEpisodio == episodio);
            if (reg != null && reg.ProgresoSegundos > 5)
            {
                // Si no ha terminado (menos del 95% o duración no registrada)
                if (reg.TotalSegundos <= 0 || reg.ProgresoSegundos < reg.TotalSegundos * 0.95)
                {
                    _resumingPositionSeconds = reg.ProgresoSegundos;
                    CurrentSeconds = reg.ProgresoSegundos;
                    var tCur = TimeSpan.FromSeconds(reg.ProgresoSegundos);
                    TiempoActualTexto = tCur.ToString(tCur.Hours > 0 ? @"hh\:mm\:ss" : @"mm\:ss");
                    if (reg.TotalSegundos > 0)
                    {
                        TotalSeconds = reg.TotalSegundos;
                        var tDur = TimeSpan.FromSeconds(reg.TotalSegundos);
                        TiempoTotalTexto = tDur.ToString(tDur.Hours > 0 ? @"hh\:mm\:ss" : @"mm\:ss");
                    }
                    TiempoCombinadoTexto = $"{TiempoActualTexto} / {TiempoTotalTexto}";
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ReproductorViewModel", $"Error comprobando progreso previo: {ex.Message}");
        }
    }

    public void CargarVideo(string rutaVideo, int animeId, string tituloAnime, int episodio, List<EpisodioItem>? listaEpisodios = null)
    {
        _ = GuardarProgresoActualAsync();

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
        _autoPlayDisparado = false;

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

            double progresoAGuardar = forzarProgresoCero ? 0 : ((durSec > 0 && curSec >= durSec * 0.95) ? 0 : curSec);
            if (progresoAGuardar < 3) progresoAGuardar = 0;

            var registros = await _databaseService.ObtenerRegistrosPorAnimeAsync(_animeId);
            var registro = registros?.FirstOrDefault(r => r.NumeroEpisodio == _episodio);

            if (registro != null)
            {
                registro.ProgresoSegundos = progresoAGuardar;
                if (durSec > 0) registro.TotalSegundos = durSec;
                registro.UltimaReproduccion = DateTime.UtcNow;
                if (string.IsNullOrWhiteSpace(registro.RutaArchivo) && !string.IsNullOrWhiteSpace(_rutaVideo))
                {
                    registro.RutaArchivo = _rutaVideo;
                }
                await _databaseService.GuardarRegistroEpisodioAsync(registro);
            }
            else if (progresoAGuardar > 0)
            {
                registro = new RegistroEpisodio
                {
                    AniListId = _animeId,
                    NumeroEpisodio = _episodio,
                    RutaArchivo = _rutaVideo,
                    ProgresoSegundos = progresoAGuardar,
                    TotalSegundos = durSec,
                    VistoLocal = _fueMarcadoComoVisto,
                    UltimaReproduccion = DateTime.UtcNow
                };
                await _databaseService.GuardarRegistroEpisodioAsync(registro);
            }

            // Notificar a DetalleViewModel para actualizar la barra de progreso en vivo
            WeakReferenceMessenger.Default.Send(new Messages.EpisodioActualizadoMensaje(
                _animeId, _episodio, _fueMarcadoComoVisto, progresoAGuardar, durSec));
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
            // 1. Guardar localmente
            var registros = await _databaseService.ObtenerRegistrosPorAnimeAsync(_animeId);
            var registro = registros?.FirstOrDefault(r => r.NumeroEpisodio == _episodio);
            
            if (registro != null)
            {
                registro.VistoLocal = true;
                registro.ProgresoSegundos = 0; // Al marcarse como visto, el progreso se limpia a 0
                registro.UltimaReproduccion = DateTime.UtcNow;
            }
            else
            {
                registro = new Models.RegistroEpisodio 
                {
                    AniListId = _animeId,
                    NumeroEpisodio = _episodio,
                    RutaArchivo = _rutaVideo,
                    VistoLocal = true,
                    ProgresoSegundos = 0,
                    UltimaReproduccion = DateTime.UtcNow
                };
            }
            await _databaseService.GuardarRegistroEpisodioAsync(registro);

            // 2. Guardar en AniList
            var token = _authService.ObtenerTokenGuardado();
            if (!string.IsNullOrEmpty(token))
            {
                await _animeTrackingService.ActualizarProgresoAsync(_animeId, _episodio, token);
            }

            // 3. Notificación flotante sutil (Toast)
            _ = WeakReferenceMessenger.Default.Send(new Messages.MostrarDialogoRequestMessage(
                "Auto-Tracking", 
                $"Episodio {_episodio} marcado como visto.", 
                false, "CheckCircle", "#4CAF50"));
                
            // 4. Avisar a la vista de detalles para que actualice la lista automáticamente
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
                        // Solo notificar si el cambio es significativo (> 0.3s)
                        if (Math.Abs(curSeconds - _lastNotifiedSeconds) >= 0.3)
                        {
                            CurrentSeconds = curSeconds;
                            _lastNotifiedSeconds = curSeconds;
                            
                            TimeSpan tCur = TimeSpan.FromSeconds(curSeconds);
                            TiempoActualTexto = tCur.ToString(tCur.Hours > 0 ? @"hh\:mm\:ss" : @"mm\:ss");
                            
                            // Cachear la duración (no cambia durante la reproducción)
                            if (!_durationCached && durSeconds > 0)
                            {
                                TotalSeconds = durSeconds;
                                TimeSpan tDur = TimeSpan.FromSeconds(durSeconds);
                                TiempoTotalTexto = tDur.ToString(tDur.Hours > 0 ? @"hh\:mm\:ss" : @"mm\:ss");
                                _durationCached = true;
                            }
                            
                            TiempoCombinadoTexto = $"{TiempoActualTexto} / {TiempoTotalTexto}";
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
                    
                    // Botón Skip Intro (Mostrar entre 0:30 y 3:00)
                    if (curSeconds >= 30 && curSeconds <= 180)
                    {
                        if (!MostrarSkipIntro) MostrarSkipIntro = true;
                    }
                    else
                    {
                        if (MostrarSkipIntro) MostrarSkipIntro = false;
                    }
                }
                else if (Player?.Status == Status.Ended)
                {
                    // Al finalizar, resetear progreso a 0
                    _ = GuardarProgresoActualAsync(forzarProgresoCero: true);

                    // Auto-Play siguiente episodio si existe y está habilitado
                    if (AutoPlaySiguiente && TieneEpisodioSiguiente && !_autoPlayDisparado)
                    {
                        _autoPlayDisparado = true;
                        
                        if (!_fueMarcadoComoVisto)
                        {
                            await RealizarAutoTrackingAsync();
                        }

                        var dispatcher = System.Windows.Application.Current?.Dispatcher;
                        if (dispatcher != null && !dispatcher.HasShutdownStarted)
                        {
                            _ = dispatcher.InvokeAsync(() => SiguienteEpisodio());
                        }
                        else
                        {
                            SiguienteEpisodio();
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
        if (Player == null) return;
        
        // Adelantar 85 segundos (1:25)
        long nuevoTiempo = Player.CurTime + TimeSpan.FromSeconds(85).Ticks;
        
        // No pasarse de la duración total
        if (nuevoTiempo > Player.Duration) nuevoTiempo = Player.Duration;
        
        Player.CurTime = nuevoTiempo;
        MostrarSkipIntro = false;
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

        _trackingCts?.Cancel();
        _trackingCts?.Dispose();
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
