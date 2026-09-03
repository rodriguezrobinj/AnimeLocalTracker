using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Core;

namespace AnimeLocalTracker.Services;

/// <summary>
/// SEC-03: bloquea la degradación https→http por redirección y los saltos a hosts
/// arbitrarios en el cliente de descargas. El HttpClient base no sigue redirects
/// (AllowAutoRedirect=false); este handler los sigue manualmente validando cada
/// Location con UrlSeguridad (https absoluta y sin credenciales embebidas).
/// </summary>
internal sealed class RedirectSeguroHandler : DelegatingHandler
{
    private const int MaximoSaltos = 5;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var current = request;

        for (int salto = 0; ; salto++)
        {
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
