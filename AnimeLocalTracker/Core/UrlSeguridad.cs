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
    /// Hosts permitidos para los embeds de servidores que publica la página de
    /// episodio (Fase 1 multi-servidor). Un animeav1 comprometido no puede inyectar
    /// un servidor arbitrario: solo pasan los proveedores conocidos.
    /// </summary>
    private static readonly string[] HostsEmbedsAnimeAv1 =
    {
        "mp4upload.com", "voe.sx", "byselapuix.com", "animeav1.uns.bio",
        "player.zilla-networks.com", "mega.nz"
    };

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

    /// <summary>
    /// URL de embed de servidor publicada por la página de episodio: solo https y
    /// hosts de proveedores conocidos (mp4upload, voe, byse, upnshare, zilla, mega).
    /// </summary>
    public static bool EsUrlEmbedPermitida(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        return EsHostPermitido(uri.Host, HostsEmbedsAnimeAv1);
    }

    /// <summary>
    /// True si la URL es un manifiesto HLS/DASH (.m3u8/.mpd). En esta fase el
    /// descargador solo maneja archivos directos; los manifiestos se omiten
    /// (la descarga segmentada queda como fase 2).
    /// </summary>
    public static bool EsUrlManifiestoStreaming(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        string u = url.ToLowerInvariant();
        return u.Contains(".m3u8") || u.Contains(".mpd");
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
