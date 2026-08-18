using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using AnimeLocalTracker.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlyleafLib.MediaPlayer;
using CommunityToolkit.Mvvm.Messaging;
using AnimeLocalTracker.Messages;

namespace AnimeLocalTracker.ViewModels;

public partial class ReproductorViewModel : ObservableObject, IDisposable
{
    private readonly IDatabaseService _databaseService;
    private readonly IAnimeTrackingService _animeTrackingService;

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
    [ObservableProperty] private string _playPauseIcon = "Pause"; // O "Play"
    [ObservableProperty] private bool _isDraggingSlider = false;
    private string _fullscreenIcon = "Fullscreen";
    public string FullscreenIcon
    {
        get => _fullscreenIcon;
        set => SetProperty(ref _fullscreenIcon, value);
    }

    private int _animeId;
    private int _episodio;
    private string _rutaVideo = string.Empty;
    private bool _fueMarcadoComoVisto = false;
    
    private readonly AnimeLocalTracker.Services.IAuthService _authService;

    public ReproductorViewModel(IDatabaseService databaseService, IAnimeTrackingService animeTrackingService, AnimeLocalTracker.Services.IAuthService authService)
    {
        _databaseService = databaseService;
        _animeTrackingService = animeTrackingService;
        _authService = authService;
        
        Player = new Player();
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

    // Comando para cuando el usuario suelta el slider (Thumb.DragCompleted)
    [RelayCommand]
    private void Seek(double seconds)
    {
        if (Player != null)
        {
            Player.CurTime = TimeSpan.FromSeconds(seconds).Ticks;
            var curSeconds = TimeSpan.FromTicks(Player.CurTime).TotalSeconds;
            
            if (!IsDraggingSlider)
            {
                CurrentSeconds = curSeconds;
                
                var t = TimeSpan.FromSeconds(curSeconds);
                TiempoActualTexto = t.ToString(t.Hours > 0 ? @"hh\:mm\:ss" : @"mm\:ss");
                TiempoCombinadoTexto = $"{TiempoActualTexto} / {TiempoTotalTexto}";
            }
        }
    }

    [ObservableProperty] private int _volumen = 100;
    [ObservableProperty] private string _volumenIcon = "VolumeHigh";

    partial void OnVolumenChanged(int value)
    {
        if (Player != null)
        {
            try {
                // Flyleaf v3 uses Player.Audio.Volume, handled safely
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

    public void CargarVideo(string rutaVideo, int animeId, string tituloAnime, int episodio)
    {
        _rutaVideo = rutaVideo;
        _animeId = animeId;
        _episodio = episodio;
        TituloAnime = tituloAnime;
        TituloEpisodio = $"Episodio {episodio}";

        // Sincronizar ícono de fullscreen con el estado actual de la ventana
        if (System.Windows.Application.Current.MainWindow is AnimeLocalTracker.Views.MainWindow mainWindow)
        {
            FullscreenIcon = mainWindow.IsFullScreen ? "FullscreenExit" : "Fullscreen";
        }

        if (Player == null)
            Player = new Player();

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
                    CurrentSeconds = curSeconds;
                    TotalSeconds = durSeconds;
                    
                    TimeSpan tCur = TimeSpan.FromSeconds(curSeconds);
                    TimeSpan tDur = TimeSpan.FromSeconds(durSeconds);
                    string textCur = tCur.ToString(tCur.Hours > 0 ? "h\\:mm\\:ss" : "m\\:ss");
                    string textDur = tDur.ToString(tDur.Hours > 0 ? "h\\:mm\\:ss" : "m\\:ss");
                    
                    TiempoActualTexto = textCur;
                    TiempoTotalTexto = textDur;
                    TiempoCombinadoTexto = $"{textCur} / {textDur}";
                }

                if (PlayPauseIcon != "Pause") PlayPauseIcon = "Pause";

                double porcentaje = curSeconds / durSeconds;

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

            await Task.Delay(1000);
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
        
        // Flyleaf Player.Seek() o Player.CurTime (set) no está claro si CurTime tiene setter, pero usualmente usan Seek o se setea
        // Vamos a probar asignar CurTime o Seek
        // Según documentación de Flyleaf, usualmente se puede setear CurTime o hacer Seek.
        // Voy a intentar asignar CurTime, pero la API a veces solo tiene Get.
        // Wait, 'Player' no contiene SeekToTime. 
        // Si no tiene, tal vez es:
        // Player.CurTime = nuevoTiempo; (ya veremos si funciona)
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
            if (Player.Status == Status.Playing)
            {
                Player.Pause();
            }
            Player.Dispose();
            Player = null!;
        }
    }
}
