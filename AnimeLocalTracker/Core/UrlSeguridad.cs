using System;
using System.Net;
using System.Net.Sockets;

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
        // SEC-02: nunca hacia la propia máquina ni la red local (una URL sacada de un scraper/yt-dlp no debe poder
        // hacer que la app pida https://127.0.0.1/…, https://192.168.x.x/… o el endpoint de metadatos de una nube).
        return EsHostPublico(uri);
    }

    /// <summary>
    /// True si el host de la URL puede ser un servidor de Internet: no es una IP literal privada/loopback/enlace
    /// local/reservada, ni "localhost", ni un nombre de una sola etiqueta (intranet) o de sufijos locales
    /// (.local, .localhost, .internal, .lan, .home.arpa). Solo mira el texto: la resolución DNS se valida aparte
    /// (<see cref="EsIpPublica"/> en <c>RedirectSeguroHandler</c>).
    /// </summary>
    public static bool EsHostPublico(Uri uri)
    {
        string host = uri.DnsSafeHost;
        if (string.IsNullOrWhiteSpace(host)) return false;

        if (IPAddress.TryParse(host, out var ip)) return EsIpPublica(ip);

        host = host.TrimEnd('.').ToLowerInvariant();
        if (!host.Contains('.')) return false; // "localhost", "nas", "router"…
        string[] sufijosLocales = { ".localhost", ".local", ".internal", ".lan", ".home.arpa", ".localdomain" };
        foreach (var sufijo in sufijosLocales)
        {
            if (host.EndsWith(sufijo, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    /// <summary>
    /// True si la IP es enrutable en Internet. Rechaza loopback, "esta red" (0.0.0.0/8), privadas RFC 1918,
    /// compartidas CGNAT (100.64/10), enlace local (169.254/16 y fe80::/10 — incluye 169.254.169.254, el endpoint de
    /// metadatos de las nubes), ULA (fc00::/7), site-local, multicast, reservadas (240/4), benchmark (198.18/15) y
    /// documentación (2001:db8::/32). Una IPv6 "mapeada" a IPv4 (::ffff:a.b.c.d) se juzga por su IPv4.
    /// </summary>
    public static bool EsIpPublica(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 0) return false;                                  // 0.0.0.0/8
            if (b[0] == 10) return false;                                 // 10.0.0.0/8
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false;  // 100.64.0.0/10 (CGNAT)
            if (b[0] == 127) return false;                                // loopback
            if (b[0] == 169 && b[1] == 254) return false;                // enlace local
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;   // 172.16.0.0/12
            if (b[0] == 192 && b[1] == 0 && b[2] == 0) return false;      // 192.0.0.0/24 (IETF)
            if (b[0] == 192 && b[1] == 168) return false;                 // 192.168.0.0/16
            if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) return false;  // 198.18.0.0/15
            if (b[0] >= 224) return false;                                // multicast + reservadas + broadcast
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.IPv6None) || ip.Equals(IPAddress.IPv6Any)) return false;
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.IsIPv6UniqueLocal) return false;
            var b = ip.GetAddressBytes();
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) return false; // 2001:db8::/32
            return true;
        }

        return false;
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

    /// <summary>Host permitido para la búsqueda de torrents (Fase MVP: Nyaa.si).</summary>
    private static readonly string[] HostsNyaa = { "nyaa.si" };

    /// <summary>
    /// URL del feed RSS o de un archivo .torrent de Nyaa.si: solo https y el host
    /// esperado. Igual criterio que <see cref="EsUrlEmbedPermitida"/> pero para la
    /// fuente de descargas por BitTorrent.
    /// </summary>
    public static bool EsUrlNyaaPermitida(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        return EsHostPermitido(uri.Host, HostsNyaa);
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
