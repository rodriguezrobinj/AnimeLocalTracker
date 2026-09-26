using System;
using System.Windows.Threading;
using AnimeLocalTracker.Services;

namespace AnimeLocalTracker.ViewModels;

/// <summary>
/// Decide qué tema suena en la sección de música de la ficha: uno a la vez, con pausa que se reanuda donde se dejó (antes
/// volvía a empezar), salto por la barra, volumen y limpieza al terminar. La lista y los comandos viven en el ViewModel de la
/// ficha; esto solo lleva el reproductor y el estado de reproducción de cada <see cref="TemaAnimeItem"/>.
/// </summary>
public sealed class ControlReproduccionTemas : IDisposable
{
    private static readonly TimeSpan IntervaloActualizacion = TimeSpan.FromMilliseconds(250);

    private readonly IAudioTrackPlayer _player;
    private readonly DispatcherTimer? _temporizador;
    private TimeSpan? _posicionPendiente;
    private bool _reanudarAlAbrir;
    private double _volumen = 0.8;

    /// <summary>La pista cargada en el reproductor (sonando o en pausa), o null.</summary>
    public TemaAnimeItem? Actual { get; private set; }

    /// <summary>No se pudo abrir o reproducir el archivo de este tema (ya se ha dejado el control limpio).</summary>
    public event EventHandler<TemaAnimeItem>? FalloReproduccion;

    /// <summary>Una pista llegó sola al final (no cuando se pausa o se detiene). Es el momento de pasar a la siguiente.</summary>
    public event EventHandler<TemaAnimeItem>? PistaTerminada;

    public ControlReproduccionTemas(IAudioTrackPlayer player, bool usarTemporizador = true)
    {
        _player = player;
        _player.Abierto += AlAbrir;
        _player.Terminado += AlTerminar;
        _player.Fallo += AlFallar;

        if (usarTemporizador)
        {
            _temporizador = new DispatcherTimer { Interval = IntervaloActualizacion };
            _temporizador.Tick += (_, _) => ActualizarPosicion();
        }
    }

    /// <summary>De 0 a 1. Se aplica ya y a las pistas que se abran después.</summary>
    public double Volumen
    {
        get => _volumen;
        set
        {
            _volumen = Math.Clamp(value, 0, 1);
            _player.Volumen = _volumen;
        }
    }

    /// <summary>
    /// Play/pausa del tema. Si ya es la pista actual: pausa, o reanuda desde donde estaba (si había terminado, desde el
    /// principio). Si es otro: corta el anterior y empieza este.
    /// </summary>
    public void Alternar(TemaAnimeItem tema, string ruta)
    {
        if (ReferenceEquals(Actual, tema))
        {
            if (tema.Reproduciendo) Pausar();
            else Reanudar();
            return;
        }

        Detener();

        Actual = tema;
        tema.EsPistaActual = true;
        Sincronizar(tema, 0, 0);

        _player.Volumen = _volumen;
        _posicionPendiente = null;
        _player.Abrir(ruta);
        _player.Reproducir();
        tema.Reproduciendo = true;
        _temporizador?.Start();
    }

    public void Pausar()
    {
        if (Actual == null) return;

        _player.Pausar();
        Actual.Reproduciendo = false;
        _temporizador?.Stop();
        ActualizarPosicion();
    }

    private void Reanudar()
    {
        if (Actual == null) return;

        _player.Reproducir();
        Actual.Reproduciendo = true;
        _temporizador?.Start();
    }

    /// <summary>Salta a un punto de la pista actual (en segundos). Ignora los temas que no son la pista actual.</summary>
    public void Buscar(TemaAnimeItem tema, double segundos)
    {
        if (!ReferenceEquals(Actual, tema) || !double.IsFinite(segundos)) return;

        double maximo = _player.Duracion > TimeSpan.Zero ? _player.Duracion.TotalSeconds : double.MaxValue;
        double destino = Math.Clamp(segundos, 0, maximo);

        _player.Posicion = TimeSpan.FromSeconds(destino);
        Sincronizar(tema, destino, null);
    }

    /// <summary>Corta la pista actual y libera el archivo. Seguro de llamar siempre.</summary>
    public void Detener()
    {
        _temporizador?.Stop();
        _posicionPendiente = null;

        if (Actual != null)
        {
            Actual.Reproduciendo = false;
            Actual.EsPistaActual = false;
            Sincronizar(Actual, 0, 0);
            Actual = null;
        }

        _player.Cerrar();
    }

    /// <summary>
    /// Para mover o borrar el archivo de la pista actual sin cortar la escucha: suelta el archivo, ejecuta
    /// <paramref name="operacion"/> (que devuelve la nueva ruta, o null si falló) y lo reabre en el mismo punto y estado.
    /// Con cualquier otro tema, solo ejecuta la operación.
    /// </summary>
    public void ConservandoPosicion(TemaAnimeItem tema, Func<string?> operacion)
    {
        if (!ReferenceEquals(Actual, tema))
        {
            operacion();
            return;
        }

        var posicion = _player.Posicion;
        bool sonaba = tema.Reproduciendo;

        _temporizador?.Stop();
        _player.Cerrar(); // Windows no deja mover un archivo abierto por el reproductor

        string? nuevaRuta = operacion();
        if (nuevaRuta == null)
        {
            Detener();
            return;
        }

        _posicionPendiente = posicion;
        _reanudarAlAbrir = sonaba;
        _player.Volumen = _volumen;
        _player.Abrir(nuevaRuta);
        if (sonaba) _temporizador?.Start();
    }

    /// <summary>Lee la posición del reproductor y la vuelca en la pista actual (lo llama el temporizador; público para pruebas).</summary>
    public void ActualizarPosicion()
    {
        var tema = Actual;
        if (tema == null || tema.Arrastrando) return;

        double duracion = _player.Duracion.TotalSeconds;
        Sincronizar(tema, _player.Posicion.TotalSeconds, duracion > 0 ? duracion : null);
    }

    private void AlAbrir(object? sender, EventArgs e)
    {
        var tema = Actual;
        if (tema == null) return;

        Sincronizar(tema, null, _player.Duracion.TotalSeconds);
        if (tema.DuracionArchivoSegundos <= 0 && _player.Duracion > TimeSpan.Zero) tema.DuracionArchivoSegundos = _player.Duracion.TotalSeconds;

        if (_posicionPendiente is TimeSpan pendiente)
        {
            _posicionPendiente = null;
            _player.Posicion = pendiente;
            Sincronizar(tema, pendiente.TotalSeconds, null);

            if (_reanudarAlAbrir) _player.Reproducir();
            _reanudarAlAbrir = false;
        }
    }

    private void AlTerminar(object? sender, EventArgs e)
    {
        var tema = Actual;
        if (tema == null) return;

        _temporizador?.Stop();
        tema.Reproduciendo = false;

        // Se queda cargada (con la barra visible) al principio: darle a play la repite. Se PAUSA antes de volver al inicio:
        // al terminar, MediaPlayer sigue en estado "reproduciendo" y mover la posición a 0 lo hacía sonar otra vez sin que la
        // interfaz lo supiera (comprobado en la app real: audio de fondo con el botón en "reproducir").
        _player.Pausar();
        _player.Posicion = TimeSpan.Zero;
        Sincronizar(tema, 0, null);

        PistaTerminada?.Invoke(this, tema);
    }

    private void AlFallar(object? sender, EventArgs e)
    {
        var tema = Actual;
        if (tema == null) return;

        Detener();
        FalloReproduccion?.Invoke(this, tema);
    }

    /// <summary>Actualiza posición/duración marcando que el cambio viene del reproductor, no del usuario.</summary>
    private static void Sincronizar(TemaAnimeItem tema, double? posicion, double? duracion)
    {
        tema.Sincronizando = true;
        try
        {
            if (duracion.HasValue) tema.DuracionSegundos = duracion.Value;
            if (posicion.HasValue) tema.PosicionSegundos = posicion.Value;
        }
        finally
        {
            tema.Sincronizando = false;
        }
    }

    public void Dispose()
    {
        _temporizador?.Stop();
        _player.Abierto -= AlAbrir;
        _player.Terminado -= AlTerminar;
        _player.Fallo -= AlFallar;
        _player.Dispose();
    }
}
