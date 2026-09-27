using System;
using System.Collections.Generic;
using System.Linq;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Decide cuántas conexiones usa UNA descarga por trozos, midiendo en vez de suponer:
/// <list type="bullet">
/// <item>Parte del reparto justo entre las descargas activas (8 si va sola).</item>
/// <item>Prueba a abrir 2 conexiones más; si la velocidad media sube al menos un 10 % se quedan,
/// y si no (la línea ya va a tope o el servidor limita por usuario) se quitan y no vuelve a
/// probar en un rato. MP4Upload, por ejemplo, limita cada conexión (~85 KB/s medidos) y la
/// velocidad crece casi en proporción al número de conexiones.</item>
/// <item>Si el servidor pide calma (429/503) baja a la mitad y se recupera de una en una.</item>
/// </list>
/// La velocidad de estos servidores gratuitos varía mucho de un momento a otro: comparar dos
/// ventanas sueltas de 4 s daba falsos "no aceleró" en uso real. Por eso se compara la MEDIA de
/// varias ventanas antes y después del cambio.
/// Lógica pura (el reloj y las velocidades llegan por parámetro) para poder testearla sin red.
/// </summary>
internal sealed class ControlConexionesDescarga
{
    internal const int Minimo = 2;
    internal const int PasoSondeo = 2;
    internal const double MejoraMinima = 0.10;
    /// <summary>Ventanas iniciales descartadas: las conexiones recién abiertas aún están acelerando.</summary>
    internal const int VentanasCalentamiento = 2;
    /// <summary>Ventanas que se promedian (como mínimo) para la velocidad de referencia y para la de prueba.</summary>
    internal const int VentanasPorMedicion = 3;
    /// <summary>Tope de ventanas por medición en servidores muy lentos (para que probar no tarde minutos).</summary>
    internal const int MaxVentanasPorMedicion = 8;
    internal const int TrozosParaRecuperar = 8;
    internal static readonly TimeSpan Enfriamiento = TimeSpan.FromSeconds(60);

    private enum Fase { Midiendo, Asentando, Comparando }

    private readonly object _lock = new();
    // Conexiones de más (o de menos, tras un 429) respecto al reparto justo.
    private int _ajuste;
    private int _trozosSinQuejas;
    private int _ventanas;
    private Fase _fase = Fase.Midiendo;
    private readonly List<double> _muestras = new();
    private double _velocidadBase;
    private int _pasoProbado;
    private DateTime _sinSondearHasta = DateTime.MinValue;

    /// <summary>Conexiones que puede usar ahora, dado el reparto justo (<paramref name="inicio"/>) y el tope por descarga.</summary>
    public int Permitidas(int inicio, int techo)
    {
        lock (_lock) return Calcular(inicio, techo);
    }

    private int Calcular(int inicio, int techo) => Math.Clamp(inicio + _ajuste, Minimo, Math.Max(Minimo, techo));

    /// <summary>El servidor pidió calma: mitad de las conexiones actuales y un rato sin sondear.</summary>
    public void Reducir(int inicio, int techo, DateTime ahora)
    {
        lock (_lock)
        {
            int nuevas = Math.Max(Minimo, Calcular(inicio, techo) / 2);
            _ajuste = nuevas - inicio;
            _trozosSinQuejas = 0;
            _fase = Fase.Midiendo;
            _muestras.Clear();
            _sinSondearHasta = ahora + Enfriamiento;
        }
    }

    /// <summary>Tras un recorte por saturación, cada tanda de trozos sin quejas devuelve una conexión.</summary>
    public void RegistrarTrozoCompletado()
    {
        lock (_lock)
        {
            if (_ajuste >= 0) return;
            if (++_trozosSinQuejas < TrozosParaRecuperar) return;
            _trozosSinQuejas = 0;
            _ajuste++;
        }
    }

    /// <summary>
    /// Ventanas a promediar según lo que tarda el servidor en contestar cada petición: la medición debe
    /// cubrir al menos el doble de esa espera. MP4Upload cierra la conexión tras cada trozo y a3 tarda
    /// ~20 s en negociar la siguiente: con 3 ventanas de 4 s, una de esas esperas parecía una caída a
    /// cero ("1,55 → 0,00 MB/s" en uso real) y el veredicto salía al revés.
    /// </summary>
    public static int VentanasParaLatencia(TimeSpan? latenciaServidor, TimeSpan ventana)
    {
        if (latenciaServidor is not TimeSpan l || ventana <= TimeSpan.Zero) return VentanasPorMedicion;
        int necesarias = (int)Math.Ceiling(2 * l.TotalSeconds / ventana.TotalSeconds);
        return Math.Clamp(necesarias, VentanasPorMedicion, MaxVentanasPorMedicion);
    }

    /// <summary>
    /// Se llama una vez por ventana de medición con la velocidad de esa ventana. Devuelve un texto
    /// para el registro cuando cambia algo (null si no). <paramref name="servidorAlTope"/>: el servidor
    /// ya no acepta más conexiones de este usuario (ver <see cref="LimitadorPorServidor"/>), así que
    /// probar más no tiene sentido — solo quedarían esperando turno y la medición sería ruido.
    /// <paramref name="ventanasPorMedicion"/>: ver <see cref="VentanasParaLatencia"/>.
    /// <paramref name="enFinal"/>: quedan menos trozos por repartir que conexiones; la velocidad baja
    /// sola al terminar, así que esas ventanas no se usan para decidir nada.
    /// </summary>
    public string? EvaluarVentana(double bytesPorSegundo, int inicio, int techo, DateTime ahora,
        bool servidorAlTope = false, int ventanasPorMedicion = VentanasPorMedicion, bool enFinal = false)
    {
        lock (_lock)
        {
            _ventanas++;
            if (_ventanas <= VentanasCalentamiento || enFinal) return null;
            int necesarias = Math.Max(1, ventanasPorMedicion);

            int actuales = Calcular(inicio, techo);
            switch (_fase)
            {
                case Fase.Asentando:
                    // La ventana en la que se abrieron las conexiones nuevas no es comparable.
                    _fase = Fase.Comparando;
                    _muestras.Clear();
                    return null;

                case Fase.Comparando:
                    _muestras.Add(bytesPorSegundo);
                    if (_muestras.Count < necesarias) return null;

                    double media = _muestras.Average();
                    _fase = Fase.Midiendo;
                    if (media >= _velocidadBase * (1 + MejoraMinima))
                    {
                        // Estas mismas muestras ya son la referencia del nuevo número: se puede volver
                        // a probar en la siguiente ventana, así la subida no tarda minutos.
                        return $"Más conexiones aceleraron la descarga ({_velocidadBase / 1048576:F2} → {media / 1048576:F2} MB/s): se mantienen {actuales}.";
                    }
                    _muestras.Clear();
                    _ajuste -= _pasoProbado;
                    _sinSondearHasta = ahora + Enfriamiento;
                    return $"Más conexiones no aceleraron la descarga ({_velocidadBase / 1048576:F2} → {media / 1048576:F2} MB/s): se vuelve a {actuales - _pasoProbado}.";

                default:
                    _muestras.Add(bytesPorSegundo);
                    while (_muestras.Count > necesarias) _muestras.RemoveAt(0);
                    if (_muestras.Count < necesarias) return null;
                    if (ahora < _sinSondearHasta || _ajuste < 0 || actuales >= techo || servidorAlTope) return null;

                    double referencia = _muestras.Average();
                    if (referencia <= 0) return null;
                    _pasoProbado = Math.Min(PasoSondeo, techo - actuales);
                    _velocidadBase = referencia;
                    _ajuste += _pasoProbado;
                    _fase = Fase.Asentando;
                    _muestras.Clear();
                    return null;
            }
        }
    }
}
