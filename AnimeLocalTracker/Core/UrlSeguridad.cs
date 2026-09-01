using System;

namespace AnimeLocalTracker.Core;

/// <summary>
/// Política de URLs seguras para la cadena de descarga (hardening INT-01):
/// las URLs que provienen del scraping o de yt-dlp se validan ANTES de cualquier
/// petición de red. Nunca se descargan enlaces http en claro, esquemas locales
/// (file://, ftp://) ni URLs con credenciales embebidas.
/// </summary>
public static class UrlSeguridad
{
    /// <summary>Hosts permitidos para la URL directa extraída del scraper de AnimeAV1.</summary>
    private static readonly string[] HostsVideoScraper = { "mp4upload.com" };

    /// <summary>
    /// URL de video extraída de HTML de terceros: solo https y el host del proveedor
    /// esperado (mp4upload.com y subdominios). Un animeav1/mp4upload comprometido no
    /// puede redirigir la descarga a un servidor arbitrario.
    /// </summary>
    public static bool EsUrlVideoPermitida(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;
        return EsHostPermitido(uri.Host, HostsVideoScraper);
    }

    /// <summary>
    /// URL de descarga en el punto de red (defensa en profundidad, aplica a cualquier
    /// origen: scraper, yt-dlp o entrada de usuario): https absoluta, sin credenciales
    /// embebidas (user:pass@host) y sin esquemas locales.
    /// </summary>
    public static bool EsUrlDescargaHttpSegura(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;
        return true;
    }

    private static bool EsHostPermitido(string host, string[] permitidos)
    {
        foreach (var permitido in permitidos)
        {
            if (host.Equals(permitido, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + permitido, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
