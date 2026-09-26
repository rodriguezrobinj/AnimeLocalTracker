using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Core;

namespace AnimeLocalTracker.Services;

/// <summary>
/// SEC-03: bloquea la degradación https→http por redirección y los saltos a hosts
/// arbitrarios en el cliente de descargas. El HttpClient base no sigue redirects
/// (AllowAutoRedirect=false); este handler los sigue manualmente validando cada
/// Location con UrlSeguridad (https absoluta y sin credenciales embebidas).
///
/// SEC-02: además, antes de CADA petición (la inicial y cada salto) resuelve el DNS del host y rechaza el destino si
/// alguna de sus IPs es privada, loopback o de enlace local — así un nombre público que apunte a 192.168.x.x o a
/// 169.254.169.254 no puede usar la app como puente hacia la red local. Limitación conocida: entre esta comprobación y
/// la conexión real puede cambiar el DNS ("DNS rebinding"); cerrarlo del todo exigiría validar la IP en el connect,
/// lo que rompería a quien usa un proxy del sistema.
/// </summary>
internal sealed class RedirectSeguroHandler : DelegatingHandler
{
    private const int MaximoSaltos = 5;

    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolutor;

    public RedirectSeguroHandler(Func<string, CancellationToken, Task<IPAddress[]>>? resolutor = null)
    {
        _resolutor = resolutor ?? ((host, ct) => Dns.GetHostAddressesAsync(host, ct));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var current = request;

        for (int salto = 0; ; salto++)
        {
            if (!await DestinoEsPublicoAsync(current.RequestUri, cancellationToken).ConfigureAwait(false))
            {
                AppLogger.Warn("RedirectSeguroHandler", $"Destino en red local/privada bloqueado: {current.RequestUri?.Host}");
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    ReasonPhrase = "Destino en red local bloqueado"
                };
            }

            var response = await base.SendAsync(current, cancellationToken).ConfigureAwait(false);

            var location = response.Headers.Location;
            if (!EsRedireccion(response) || location == null)
            {
                return response;
            }

            Uri destino = location.IsAbsoluteUri
                ? location
                : new Uri(current.RequestUri!, location);

            if (!UrlSeguridad.EsUrlDescargaHttpSegura(destino.ToString()))
            {
                AppLogger.Warn("RedirectSeguroHandler", $"Redirección a URL no segura bloqueada: {destino.Scheme}://{destino.Authority}");
                response.Dispose();
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    ReasonPhrase = "Redireccion a URL no segura bloqueada"
                };
            }

            if (salto >= MaximoSaltos)
            {
                AppLogger.Warn("RedirectSeguroHandler", "Demasiadas redirecciones en la cadena de descarga; se aborta.");
                response.Dispose();
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    ReasonPhrase = "Demasiadas redirecciones"
                };
            }

            current = ClonarRequest(current, destino);
            response.Dispose();
        }
    }

    /// <summary>
    /// True si el host de la petición resuelve solo a IPs públicas. Una IP literal ya la valida UrlSeguridad. Si el DNS
    /// falla no se bloquea aquí: la petición fallará sola al conectar y el error real llegará al llamador.
    /// </summary>
    private async Task<bool> DestinoEsPublicoAsync(Uri? uri, CancellationToken ct)
    {
        if (uri == null) return false;
        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6) return UrlSeguridad.EsHostPublico(uri);

        IPAddress[] ips;
        try
        {
            ips = await _resolutor(uri.DnsSafeHost, ct).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return true;
        }

        foreach (var ip in ips)
        {
            if (!UrlSeguridad.EsIpPublica(ip)) return false;
        }
        return true;
    }

    private static bool EsRedireccion(HttpResponseMessage response)
    {
        return response.StatusCode == HttpStatusCode.MovedPermanently
               || response.StatusCode == HttpStatusCode.Found
               || response.StatusCode == HttpStatusCode.SeeOther
               || response.StatusCode == HttpStatusCode.TemporaryRedirect
               || response.StatusCode == HttpStatusCode.PermanentRedirect;
    }

    private static HttpRequestMessage ClonarRequest(HttpRequestMessage original, Uri destino)
    {
        var clon = new HttpRequestMessage(original.Method, destino);
        foreach (var header in original.Headers)
        {
            clon.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return clon;
    }
}
