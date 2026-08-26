using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AnimeLocalTracker.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;

namespace AnimeLocalTracker.Core.ViewModels;

public partial class ReproductorViewModel : ObservableObject, IReproductorViewModel
{
    private readonly LibVLC _libVLC;

    [ObservableProperty]
    private MediaPlayer? _mediaPlayer;

    [ObservableProperty]
    private string _tituloAnime = string.Empty;

    [ObservableProperty]
    private int _episodioActual;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private float _position;

    [ObservableProperty]
    private int _volume = 100;
    
    [ObservableProperty]
    private bool _isMuted;

    public ReproductorViewModel()
    {
        _libVLC = new LibVLC("--no-osd");
        MediaPlayer = new MediaPlayer(_libVLC);
        
        MediaPlayer.TimeChanged += MediaPlayer_TimeChanged;
        MediaPlayer.Playing += (sender, args) => IsPlaying = true;
        MediaPlayer.Paused += (sender, args) => IsPlaying = false;
        MediaPlayer.Stopped += (sender, args) => IsPlaying = false;
        MediaPlayer.VolumeChanged += (sender, args) => Volume = MediaPlayer.Volume;
        MediaPlayer.Muted += (sender, args) => IsMuted = true;
        MediaPlayer.Unmuted += (sender, args) => IsMuted = false;
    }

    private void MediaPlayer_TimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
    {
        if (MediaPlayer != null && MediaPlayer.Length > 0)
        {
            Position = (float)MediaPlayer.Time / MediaPlayer.Length;
        }
    }

    public Task CargarVideoAsync(string rutaVideo, int animeId, string tituloAnime, int episodio, IEnumerable<EpisodioItem>? episodiosDisponibles)
    {
        TituloAnime = tituloAnime;
        EpisodioActual = episodio;
        
        var media = new Media(_libVLC, new Uri(rutaVideo));
        MediaPlayer?.Play(media);
        
        return Task.CompletedTask;
    }
    
    [RelayCommand]
    private void PlayPause()
    {
        if (MediaPlayer == null) return;
        
        if (MediaPlayer.IsPlaying)
        {
            MediaPlayer.Pause();
        }
        else
        {
            MediaPlayer.Play();
        }
    }

    [RelayCommand]
    private void ToggleMute()
    {
        if (MediaPlayer != null)
        {
            MediaPlayer.Mute = !MediaPlayer.Mute;
        }
    }

    public void Dispose()
    {
        if (MediaPlayer != null)
        {
            MediaPlayer.Stop();
            MediaPlayer.TimeChanged -= MediaPlayer_TimeChanged;
            MediaPlayer.Dispose();
        }
        _libVLC.Dispose();
    }
}
