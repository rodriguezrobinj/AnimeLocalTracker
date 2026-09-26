using System;
using System.Windows.Media;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Reproductor de una pista de audio local con posición, duración y volumen (para la barra de la sección de música de la
/// ficha). Es independiente del reproductor de video (Flyleaf) y del de clips de los minijuegos. Se usa siempre desde el
/// hilo de UI. Abstraído para poder probar la lógica de la ficha sin sonido real.
/// </summary>
public interface IAudioTrackPlayer : IDisposable
{
    /// <summary>Carga otro archivo (sustituye al anterior). No empieza a sonar hasta llamar a <see cref="Reproducir"/>.</summary>
    void Abrir(string ruta);

    void Reproducir();
    void Pausar();

    /// <summary>Corta el sonido y libera el archivo (importante para poder moverlo o borrarlo).</summary>
    void Cerrar();

    TimeSpan Posicion { get; set; }

    /// <summary>Duración total; cero hasta que el archivo termina de abrirse.</summary>
    TimeSpan Duracion { get; }

    /// <summary>De 0 a 1.</summary>
    double Volumen { get; set; }

    /// <summary>El archivo se abrió y ya se conoce su duración.</summary>
    event EventHandler? Abierto;

    /// <summary>La pista llegó al final.</summary>
    event EventHandler? Terminado;

    /// <summary>No se pudo abrir o reproducir el archivo.</summary>
    event EventHandler? Fallo;
}

public sealed class AudioTrackPlayer : IAudioTrackPlayer
{
    private MediaPlayer? _player;
    private double _volumen = 0.8;

    public event EventHandler? Abierto;
    public event EventHandler? Terminado;
    public event EventHandler? Fallo;

    public TimeSpan Duracion { get; private set; }

    public TimeSpan Posicion
    {
        get => _player?.Position ?? TimeSpan.Zero;
        set
        {
            if (_player == null) return;
            try { _player.Position = value < TimeSpan.Zero ? TimeSpan.Zero : value; }
            catch (Exception ex) { AppLogger.Debug("AudioTrackPlayer", $"No se pudo mover la posición: {ex.Message}"); }
        }
    }

    public double Volumen
    {
        get => _volumen;
        set
        {
            _volumen = Math.Clamp(value, 0, 1);
            if (_player != null) _player.Volume = _volumen;
        }
    }

    public void Abrir(string ruta)
    {
        Cerrar();
        Duracion = TimeSpan.Zero;

        try
        {
            // Un MediaPlayer nuevo por archivo: reabrir el mismo archivo en el mismo reproductor tras un Stop() no volvía a
            // disparar MediaOpened (mismo problema que el reproductor de clips de los minijuegos).
            var player = new MediaPlayer { Volume = _volumen };
            player.MediaOpened += (_, _) =>
            {
                if (!ReferenceEquals(player, _player)) return;
                Duracion = player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan : TimeSpan.Zero;
                Abierto?.Invoke(this, EventArgs.Empty);
            };
            player.MediaEnded += (_, _) =>
            {
                if (ReferenceEquals(player, _player)) Terminado?.Invoke(this, EventArgs.Empty);
            };
            player.MediaFailed += (_, e) =>
            {
                if (!ReferenceEquals(player, _player)) return;
                AppLogger.Debug("AudioTrackPlayer", $"Fallo de reproducción: {e.ErrorException?.Message}");
                Fallo?.Invoke(this, EventArgs.Empty);
            };

            _player = player;
            player.Open(new Uri(ruta, UriKind.Absolute));
        }
        catch (Exception ex)
        {
            AppLogger.Debug("AudioTrackPlayer", $"No se pudo abrir '{ruta}': {ex.Message}");
            Fallo?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Reproducir()
    {
        try { _player?.Play(); }
        catch (Exception ex) { AppLogger.Debug("AudioTrackPlayer", $"No se pudo reproducir: {ex.Message}"); }
    }

    public void Pausar()
    {
        try { _player?.Pause(); }
        catch (Exception ex) { AppLogger.Debug("AudioTrackPlayer", $"No se pudo pausar: {ex.Message}"); }
    }

    public void Cerrar()
    {
        var anterior = _player;
        _player = null;
        Duracion = TimeSpan.Zero;
        if (anterior == null) return;

        try
        {
            anterior.Stop();
            anterior.Close();
        }
        catch (Exception ex)
        {
            AppLogger.Debug("AudioTrackPlayer", $"No se pudo cerrar el reproductor: {ex.Message}");
        }
    }

    public void Dispose() => Cerrar();
}
