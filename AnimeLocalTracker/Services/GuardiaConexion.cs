using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace AnimeLocalTracker.Services;

/// <summary>La petición no salió: Windows indica que no hay internet. Los reintentos no la repiten (no tendría sentido).</summary>
public sealed class SinConexionException : HttpRequestException
{
    public SinConexionException() : base("Sin conexión a internet.") { }
}

/// <summary>
/// Decide si una petición sale a la red. Sin internet fallan al instante: antes cada consulta a AniList tardaba 60 s en
/// rendirse (medido) y las pantallas se quedaban cargando un minuto antes de mostrar lo guardado.
/// El indicador de Windows puede equivocarse, y en el equipo del usuario parpadea (medido el 2026-09-29: con internet
/// funcionando dijo "sin internet" tres veces en 10 s al arrancar la app). Por eso manda la realidad: si algún servidor respondió
/// hace menos de <see cref="ConfianzaTrasRespuesta"/>, nada se bloquea diga lo que diga Windows. Solo cuando Windows dice
/// "sin internet" y hace rato que nadie responde se bloquea, dejando pasar una petición de prueba cada <see cref="IntervaloSondeo"/>.
/// Un fallo de red real (sin respuesta del servidor) anula esa confianza, y cuando <see cref="EstadoConexionService"/> confirma con
/// su propia comprobación que no hay internet, todo se bloquea hasta que esa comprobación vuelva a encontrar red: antes, si la red
/// se caía con la app abierta, nada lo notaba y había que cerrarla y volver a abrirla.
/// </summary>
public sealed class GuardiaConexion
{
    internal static readonly TimeSpan IntervaloSondeo = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan ConfianzaTrasRespuesta = TimeSpan.FromMinutes(2);

    private readonly IConectividadRed _red;
    private readonly Func<DateTime> _ahoraUtc;
    private readonly object _lock = new();
    private readonly bool _forzado;
    private DateTime _ultimoSondeoUtc = DateTime.MinValue;
    private DateTime _ultimaRespuestaUtc = DateTime.MinValue;
    private DateTime _ultimoFalloRedUtc = DateTime.MinValue;
    private volatile bool _sinConexionConfirmada;
    private bool _avisoIndicadorErroneo;

    /// <summary>Modo de prueba "sin red" (ANIMELOCALTRACKER_SIN_RED=1).</summary>
    public bool Forzado => _forzado;

    /// <summary>Último fallo de red real (el servidor no llegó a responder: DNS, conexión rechazada…).</summary>
    public DateTime UltimoFalloDeRedUtc
    {
        get { lock (_lock) return _ultimoFalloRedUtc; }
    }

    public GuardiaConexion(IConectividadRed red, Func<DateTime>? ahoraUtc = null, bool sinRedForzado = false)
    {
        _red = red;
        _forzado = sinRedForzado;
        _ahoraUtc = ahoraUtc ?? (() => DateTime.UtcNow);
        if (sinRedForzado) AppLogger.Warn("GuardiaConexion", "Modo de prueba sin red (ANIMELOCALTRACKER_SIN_RED=1): ninguna petición sale a internet.");
    }

    /// <summary>Un servidor respondió hace poco y no hubo fallos de red después.</summary>
    private bool RespondioHacePoco()
    {
        lock (_lock) return RespondioHacePocoSinLock(_ahoraUtc());
    }

    private bool RespondioHacePocoSinLock(DateTime ahora) =>
        ahora - _ultimaRespuestaUtc < ConfianzaTrasRespuesta && _ultimaRespuestaUtc >= _ultimoFalloRedUtc;

    /// <summary>Solo según Windows (y lo que respondió hace poco): para decidir si hace falta una comprobación propia.</summary>
    public bool PareceSinConexionSegunWindows => !_red.HayInternet && !RespondioHacePoco();

    /// <summary>Para los mensajes: sin conexión confirmada, o Windows dice que no hay internet y ningún servidor respondió hace poco.</summary>
    public bool PareceSinConexion => _forzado || _sinConexionConfirmada || PareceSinConexionSegunWindows;

    /// <summary>Lo decide <see cref="EstadoConexionService"/> con su propia comprobación.</summary>
    public void EstablecerSinConexionConfirmada(bool sinConexion) => _sinConexionConfirmada = sinConexion;

    /// <summary>Una petición acaba de fallar sin respuesta: <see cref="EstadoConexionService"/> comprueba en el acto.</summary>
    public event Action? FalloDeRed;

    /// <summary>Una petición falló sin que el servidor llegara a responder.</summary>
    public void RegistrarFalloDeRed()
    {
        lock (_lock) _ultimoFalloRedUtc = _ahoraUtc();
        try { FalloDeRed?.Invoke(); }
        catch (Exception ex) { AppLogger.Debug("GuardiaConexion", $"Error avisando de un fallo de red: {ex.Message}"); }
    }

    /// <summary>True si la petición no debe salir (sin internet de verdad y no toca petición de prueba).</summary>
    public bool DebeBloquear()
    {
        if (_forzado) return true; // modo de prueba "sin red": ni peticiones de prueba
        if (_sinConexionConfirmada) return true; // la comprobación propia de EstadoConexionService avisará cuando vuelva
        if (_red.HayInternet) return false;

        lock (_lock)
        {
            var ahora = _ahoraUtc();
            if (RespondioHacePocoSinLock(ahora)) return false; // Windows se equivoca: hay quien responde
            if (ahora - _ultimoSondeoUtc < IntervaloSondeo) return true;
            _ultimoSondeoUtc = ahora; // esta sale de prueba
            return false;
        }
    }

    /// <summary>Un servidor respondió: hay internet, diga lo que diga Windows.</summary>
    public void RegistrarRespuesta()
    {
        bool avisar;
        lock (_lock)
        {
            _ultimaRespuestaUtc = _ahoraUtc();
            avisar = !_avisoIndicadorErroneo && !_forzado && !_red.HayInternet;
            if (avisar) _avisoIndicadorErroneo = true;
        }
        if (avisar)
            AppLogger.Warn("GuardiaConexion", "Windows indica que no hay internet, pero un servidor respondió: mientras sigan respondiendo se ignora su indicador.");
    }
}

/// <summary>
/// Última pieza antes de la red en cada cliente HTTP (va DESPUÉS de las políticas de reintento, así cada reintento vuelve a
/// consultar la guardia y sin internet no se repite nada).
/// </summary>
public sealed class SinConexionHandler : DelegatingHandler
{
    private readonly GuardiaConexion _guardia;

    public SinConexionHandler(GuardiaConexion guardia) => _guardia = guardia;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_guardia.DebeBloquear()) throw new SinConexionException();

        HttpResponseMessage respuesta;
        try
        {
            respuesta = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex is not SinConexionException && ex.StatusCode == null)
        {
            _guardia.RegistrarFalloDeRed(); // no llegó a responder nadie (DNS, conexión rechazada o cortada)
            throw;
        }
        _guardia.RegistrarRespuesta();
        return respuesta;
    }
}

public static class GuardiaConexionExtensions
{
    /// <summary>Añade <see cref="SinConexionHandler"/> al final del cliente: registrarlo DESPUÉS de las políticas de reintento.</summary>
    public static IHttpClientBuilder ConCorteSinConexion(this IHttpClientBuilder builder) =>
        builder.AddHttpMessageHandler(sp => new SinConexionHandler(sp.GetRequiredService<GuardiaConexion>()));
}
