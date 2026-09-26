using System;
using System.Windows.Media;
using System.Windows.Threading;

namespace AnimeLocalTracker.Services.Minijuegos;

/// <summary>
/// Reproduce un trozo de un mp3 local. Es un <see cref="MediaPlayer"/> propio, independiente del reproductor de video
/// (Flyleaf) y de la vista previa de la ficha, para no interferir con ellos. Se usa siempre desde el hilo de UI.
/// </summary>
public interface IClipPlayer
{
    /// <summary>Suena <paramref name="duracion"/> desde el punto indicado (0–1 de la duración total, ajustado para que quepa el clip).</summary>
    void Reproducir(string ruta, TimeSpan duracion, double posicionRelativa);

    /// <summary>Corta el sonido si lo hay. Seguro de llamar siempre.</summary>
    void Detener();

    bool EstaReproduciendo { get; }

    /// <summary>Se dispara al acabar el clip (por tiempo o por fin del archivo), no al llamar a <see cref="Detener"/>.</summary>
    event EventHandler? ReproduccionTerminada;
}

public sealed class ClipPlayer : IClipPlayer
{
    private const double Volumen = 0.85;

    private MediaPlayer? _player;
    private DispatcherTimer? _temporizador;
    private TimeSpan _duracion;
    private double _posicionRelativa;

    public bool EstaReproduciendo { get; private set; }

    public event EventHandler? ReproduccionTerminada;

    public void Reproducir(string ruta, TimeSpan duracion, double posicionRelativa)
    {
        Detener();
        CerrarPlayer();

        try
        {
            _duracion = duracion;
            _posicionRelativa = Math.Clamp(posicionRelativa, 0, 1);

            // Un MediaPlayer nuevo en cada reproducción: reabrir el mismo archivo en el mismo reproductor tras un Stop()
            // no volvía a disparar MediaOpened y "Escuchar de nuevo" se quedaba mudo (comprobado en la app real).
            _player = CrearPlayer();

            // La posición inicial depende de la duración total del archivo: se fija en MediaOpened, antes de
            // dar a Play (si no, se oiría un instante del arranque del tema).
            _player.Open(new Uri(ruta, UriKind.Absolute));
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ClipPlayer", $"No se pudo abrir '{ruta}': {ex.Message}");
            EstaReproduciendo = false;
        }
    }

    public void Detener()
    {
        _temporizador?.Stop();
        EstaReproduciendo = false;
        try
        {
            _player?.Stop();
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ClipPlayer", $"No se pudo detener: {ex.Message}");
        }
    }

    private void CerrarPlayer()
    {
        var anterior = _player;
        _player = null;
        if (anterior == null) return;

        try
        {
            anterior.Close();
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ClipPlayer", $"No se pudo cerrar el reproductor anterior: {ex.Message}");
        }
    }

    private MediaPlayer CrearPlayer()
    {
        var player = new MediaPlayer { Volume = Volumen };

        // Los eventos de un reproductor ya sustituido se ignoran (podrían llegar tarde y arrancar o cortar el clip nuevo).
        player.MediaOpened += (_, _) =>
        {
            if (ReferenceEquals(player, _player)) AlAbrir(player);
        };
        player.MediaEnded += (_, _) =>
        {
            if (ReferenceEquals(player, _player)) Terminar();
        };
        player.MediaFailed += (_, e) =>
        {
            if (!ReferenceEquals(player, _player)) return;
            AppLogger.Debug("ClipPlayer", $"Fallo de reproducción: {e.ErrorException?.Message}");
            EstaReproduciendo = false;
        };
        return player;
    }

    private void AlAbrir(MediaPlayer player)
    {
        try
        {
            var total = player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan : TimeSpan.Zero;
            var maximoInicio = total > _duracion ? total - _duracion : TimeSpan.Zero;
            player.Position = TimeSpan.FromSeconds(maximoInicio.TotalSeconds * _posicionRelativa);
            player.Play();
            EstaReproduciendo = true;

            _temporizador ??= new DispatcherTimer();
            _temporizador.Stop();
            _temporizador.Interval = _duracion;
            _temporizador.Tick -= AlVencerElTemporizador;
            _temporizador.Tick += AlVencerElTemporizador;
            _temporizador.Start();
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ClipPlayer", $"No se pudo iniciar el clip: {ex.Message}");
            EstaReproduciendo = false;
        }
    }

    private void AlVencerElTemporizador(object? sender, EventArgs e) => Terminar();

    private void Terminar()
    {
        if (!EstaReproduciendo) return;
        Detener();
        ReproduccionTerminada?.Invoke(this, EventArgs.Empty);
    }
}
