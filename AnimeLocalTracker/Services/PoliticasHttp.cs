using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Polly;

namespace AnimeLocalTracker.Services;

/// <summary>Reintentos de las APIs (AniList, AniSkip, AnimeThemes).</summary>
public static class PoliticasHttp
{
    /// <summary>
    /// Fallos pasajeros de red o del servidor (error de conexión, 5xx, 408). NO incluye <see cref="SinConexionException"/>: sin
    /// internet reintentar solo retrasa el momento de mostrar lo guardado.
    /// </summary>
    public static PolicyBuilder<HttpResponseMessage> ErroresPasajeros() => Policy<HttpResponseMessage>
        .Handle<HttpRequestException>(ex => ex is not SinConexionException)
        .OrResult(r => (int)r.StatusCode >= 500 || r.StatusCode == HttpStatusCode.RequestTimeout);

    /// <summary>
    /// AniList y AniSkip. Antes cualquier error de red esperaba 60 s antes de reintentar (esa espera es la del límite de peticiones
    /// de AniList) y el cliente cortaba a los 60 s: sin internet, cada consulta tardaba un minuto en rendirse. Ahora:
    /// <list type="bullet">
    /// <item>error de red o 5xx: dos reintentos cortos (1 s y 2 s);</item>
    /// <item>429 (límite de peticiones): se espera lo que pide AniList (Retry-After, 60 s si no lo dice), hasta 3 veces; tras 3
    /// seguidos el circuito se abre 2 min y las peticiones esperan en vez de insistir.</item>
    /// </list>
    /// </summary>
    public static IAsyncPolicy<HttpResponseMessage> AniList(TimeSpan? esperaRedBase = null, TimeSpan? esperaLimitePorDefecto = null)
    {
        var baseRed = esperaRedBase ?? TimeSpan.FromSeconds(1);
        var esperaLimite = esperaLimitePorDefecto ?? TimeSpan.FromSeconds(60); // AniList bloquea 1 minuto
        var random = new Random();

        var circuito = Policy<HttpResponseMessage>
            .HandleResult(r => r.StatusCode == HttpStatusCode.TooManyRequests)
            .CircuitBreakerAsync(
                handledEventsAllowedBeforeBreaking: 3,
                durationOfBreak: TimeSpan.FromMinutes(2),
                onBreak: (_, tiempo) => AppLogger.Warn("AniListTrackingService", $"Circuit Breaker ABIERTO por {tiempo.TotalSeconds}s (límite de peticiones)"),
                onReset: () => AppLogger.Info("AniListTrackingService", "Circuit Breaker RESET CERRADO"),
                onHalfOpen: () => AppLogger.Info("AniListTrackingService", "Circuit Breaker MEDIO ABIERTO"));

        var reintentosRed = ErroresPasajeros()
            .WaitAndRetryAsync(2, intento => TimeSpan.FromTicks(baseRed.Ticks * (1L << (intento - 1))));

        var reintentosLimite = Policy<HttpResponseMessage>
            .HandleResult(r => r.StatusCode == HttpStatusCode.TooManyRequests)
            .Or<Polly.CircuitBreaker.BrokenCircuitException>() // circuito abierto por el límite: esperar y reintentar
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: (_, resultado, _) =>
                {
                    var espera = resultado?.Result?.Headers.RetryAfter?.Delta is TimeSpan pedida ? pedida.Add(TimeSpan.FromSeconds(1)) : esperaLimite;
                    lock (random) return espera.Add(TimeSpan.FromMilliseconds(esperaLimite >= TimeSpan.FromSeconds(1) ? random.Next(500, 2000) : 0)); // jitter
                },
                onRetryAsync: (_, espera, intento, _) =>
                {
                    AppLogger.Debug("AniListTrackingService", $"Límite de peticiones: esperando {espera.TotalSeconds:F0} s (reintento {intento}).");
                    return Task.CompletedTask;
                });

        return Policy.WrapAsync(reintentosLimite, reintentosRed, circuito);
    }
}
