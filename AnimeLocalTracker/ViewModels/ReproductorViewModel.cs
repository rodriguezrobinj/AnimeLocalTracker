using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using AnimeLocalTracker.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlyleafLib;
using FlyleafLib.MediaPlayer;
using CommunityToolkit.Mvvm.Messaging;
using AnimeLocalTracker.Messages;

namespace AnimeLocalTracker.ViewModels;

public partial class ReproductorViewModel : ObservableObject, IDisposable
{
    private readonly IDatabaseService _databaseService;
    private readonly IAnimeTrackingService _animeTrackingService;
    private readonly AnimeLocalTracker.Services.IAuthService _authService;

    [ObservableProperty]
    private Player _player;

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
    private int _episodio;
    private string _rutaVideo = string.Empty;
    private bool _fueMarcadoComoVisto = false;
    
    // Cache para evitar recalcular duración en cada tick
    private double _lastNotifiedSeconds = -1;
    private bool _durationCached = false;

    public ReproductorViewModel(IDatabaseService databaseService, IAnimeTrackingService animeTrackingService, AnimeLocalTracker.Services.IAuthService authService)
    {
        _databaseService = databaseService;
        _animeTrackingService = animeTrackingService;
        _authService = authService;
        
        // No crear Player aquí — se crea en CargarVideo() con config optimizada
    }

    private Player CreateOptimizedPlayer()
    {
        var config = new Config();
        
        // Seeking rápido por keyframe (no frame-accurate)
        config.Player.SeekAccurate = false;
        
        // Subtítulos habilitados por defecto
        config.Subtitles.Enabled = SubtitulosHabilitados;
        
        return new Player(config);
    }

    [RelayCommand]
    private void TogglePlayPause()
    {
        if (Player == null) return;
        
        if (Player.Status == Status.Playing)
        {
            Player.Pause();
            PlayPauseIcon = "Play";
        }
        else
        {
            Player.Play();
            PlayPauseIcon = "Pause";
        }
    }
    
    [RelayCommand]
    private void Rewind10()
    {
        if (Player == null) return;
        long newTime = Player.CurTime - TimeSpan.FromSeconds(10).Ticks;
        if (newTime < 0) newTime = 0;
        Player.CurTime = newTime;
    }
    
    [RelayCommand]
    private void Forward10()
    {
        if (Player == null) return;
        long newTime = Player.CurTime + TimeSpan.FromSeconds(10).Ticks;
        if (newTime > Player.Duration) newTime = Player.Duration;
        Player.CurTime = newTime;
    }

    // Comando para cuando el usuario suelta el slider (Thumb.DragCompleted) o click directo
    [RelayCommand]
    private void Seek(double seconds)
    {
        if (Player != null)
        {
            Player.CurTime = TimeSpan.FromSeconds(seconds).Ticks;
            
            // Actualizar UI inmediatamente para feedback instantáneo
            var curSeconds = TimeSpan.FromTicks(Player.CurTime).TotalSeconds;
            CurrentSeconds = curSeconds;
            
            var t = TimeSpan.FromSeconds(curSeconds);
            TiempoActualTexto = t.ToString(t.Hours > 0 ? @"hh\:mm\:ss" : @"mm\:ss");
            TiempoCombinadoTexto = $"{TiempoActualTexto} / {TiempoTotalTexto}";
        }
    }

    [ObservableProperty] private int _volumen = 100;
    [ObservableProperty] private string _volumenIcon = "VolumeHigh";

    partial void OnVolumenChanged(int value)
    {
        if (Player != null)
        {
            try {
                Player.Audio.Volume = value;
            } catch { }
        }
        
        if (value == 0) VolumenIcon = "VolumeMute";
        else if (value < 30) VolumenIcon = "VolumeLow";
        else if (value < 70) VolumenIcon = "VolumeMedium";
        else VolumenIcon = "VolumeHigh";
    }

    private int _volumenAnterior = 100;

    [RelayCommand]
    private void ToggleMute()
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
    private void ToggleFullscreen()
    {
        if (System.Windows.Application.Current.MainWindow is AnimeLocalTracker.Views.MainWindow mainWindow)
        {
            mainWindow.TogglePantallaCompleta();
            
            // Actualizar ícono según el estado actual
            FullscreenIcon = mainWindow.IsFullScreen ? "FullscreenExit" : "Fullscreen";
            
            // Forzar foco de vuelta a la ventana para que F11 siga funcionando
            System.Windows.Application.Current.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Input,
                () =>
                {
                    mainWindow.Focus();
                    System.Windows.Input.Keyboard.Focus(mainWindow);
                });
        }
    }

    [RelayCommand]
    private void SelectSubtitleStream(object stream)
    {
        if (Player == null || stream == null) return;
        
        SubtitulosHabilitados = true;
        Player.Config.Subtitles.Enabled = true;
        Player.OpenAsync((dynamic)stream);
        SubtitulosIcon = "Subtitles";
    }

    [RelayCommand]
    private void TurnOffSubtitles()
    {
        if (Player == null) return;
        
        SubtitulosHabilitados = false;
        Player.Config.Subtitles.Enabled = false;
        SubtitulosIcon = "SubtitlesOutline";
    }

    public void CargarVideo(string rutaVideo, int animeId, string tituloAnime, int episodio)
    {
        _rutaVideo = rutaVideo;
        _animeId = animeId;
        _episodio = episodio;
        TituloAnime = tituloAnime;
        TituloEpisodio = $"Episodio {episodio}";
        _fueMarcadoComoVisto = false;
        _durationCached = false;
        _lastNotifiedSeconds = -1;

        // Sincronizar ícono de fullscreen con el estado actual de la ventana
        if (System.Windows.Application.Current.MainWindow is AnimeLocalTracker.Views.MainWindow mainWindow)
        {
            FullscreenIcon = mainWindow.IsFullScreen ? "FullscreenExit" : "Fullscreen";
        }

        // Dispose del player anterior si existe
        if (Player != null)
        {
            try { Player.Dispose(); } catch { }
        }

        // Crear player con configuración optimizada para seeking rápido
        Player = CreateOptimizedPlayer();

        Player.OpenAsync(rutaVideo);
        Player.Play();

        _ = RastrearProgresoAsync();
    }

    private async Task RastrearProgresoAsync()
    {
        while (Player != null && !Player.IsDisposed)
        {
            if (Player.Status == Status.Playing)
            {
                double curSeconds = TimeSpan.FromTicks(Player.CurTime).TotalSeconds;
                double durSeconds = TimeSpan.FromTicks(Player.Duration).TotalSeconds;
                
                if (!IsDraggingSlider)
                {
                    // Solo notificar si el cambio es significativo (> 0.3s)
                    // para evitar property-changed spam innecesario
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

                double porcentaje = durSeconds > 0 ? curSeconds / durSeconds : 0;

                // Auto-Tracking al 90%
                if (porcentaje >= 0.90 && !_fueMarcadoComoVisto)
                {
                    _fueMarcadoComoVisto = true;
                    
                    try 
                    {
                        // 1. Guardar localmente
                        var registros = await _databaseService.ObtenerRegistrosPorAnimeAsync(_animeId);
                        var registro = registros.FirstOrDefault(r => r.NumeroEpisodio == _episodio);
                        
                        if (registro != null)
                        {
                            registro.VistoLocal = true;
                        }
                        else
                        {
                            registro = new Models.RegistroEpisodio 
                            {
                                AniListId = _animeId,
                                NumeroEpisodio = _episodio,
                                RutaArchivo = _rutaVideo,
                                VistoLocal = true
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
                        WeakReferenceMessenger.Default.Send(new Messages.EpisodioActualizadoMensaje(_animeId, _episodio, true));
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error en auto-tracking: {ex.Message}");
                    }
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
                // Auto-Play siguiente episodio si existe
            }

            // 250ms para UI más responsive (antes era 1000ms)
            await Task.Delay(250);
        }
    }

    [RelayCommand]
    private void SkipIntro()
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
    private void Cerrar()
    {
        Dispose();
        
        // Navegar a la vista anterior (detalle del anime), no a la galería
        WeakReferenceMessenger.Default.Send(new NavegarMensaje_VolverDelReproductor());

        // Forzar el foco de vuelta a la ventana principal para que F11 funcione de inmediato
        System.Windows.Application.Current.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Input,
            () =>
            {
                var window = System.Windows.Application.Current.MainWindow;
                if (window != null)
                {
                    window.Focus();
                    System.Windows.Input.Keyboard.Focus(window);
                }
            });
    }

    public void Dispose()
    {
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
            catch { }
            Player = null!;
        }
    }
}
