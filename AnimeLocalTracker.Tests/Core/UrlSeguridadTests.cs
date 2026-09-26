using AnimeLocalTracker.Core;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Core;

/// <summary>
/// Política de URLs seguras de la cadena de descarga (hardening INT-01):
/// el scraper solo devuelve https de mp4upload.com y el punto de descarga solo
/// acepta https sin credenciales, sea cual sea el origen de la URL.
/// </summary>
public class UrlSeguridadTests
{
    // === EsUrlVideoPermitida (resultado del scraper) ===

    [Theory]
    [InlineData("https://www.mp4upload.com/direct/abc123/video.mp4")]
    [InlineData("https://cdn.mp4upload.com/abc123/720p/video.mp4")]
    [InlineData("https://mp4upload.com/video.mkv")]
    public void EsUrlVideoPermitida_HostsDeMp4Upload_DeberiaAceptar(string url)
    {
        UrlSeguridad.EsUrlVideoPermitida(url).Should().BeTrue();
    }

    [Theory]
    [InlineData("https://cdn.example.com/video.mp4")]
    [InlineData("http://www.mp4upload.com/video.mp4")]
    [InlineData("https://mp4upload.com.evil.com/video.mp4")]
    [InlineData("https://user:pass@cdn.mp4upload.com/video.mp4")]
    [InlineData("file:///C:/videos/video.mp4")]
    [InlineData("ftp://mp4upload.com/video.mp4")]
    [InlineData("no-es-una-url")]
    [InlineData("")]
    [InlineData(null)]
    public void EsUrlVideoPermitida_UrlsNoPermitidas_DeberiaRechazar(string? url)
    {
        UrlSeguridad.EsUrlVideoPermitida(url).Should().BeFalse();
    }

    // === EsUrlEmbedPermitida (embeds publicados por la página de episodio) ===

    [Theory]
    [InlineData("https://www.mp4upload.com/embed-abc123.html")]
    [InlineData("https://voe.sx/e/xkwrsnscgvze")]
    [InlineData("https://byselapuix.com/e/ollcejudwkem")]
    [InlineData("https://animeav1.uns.bio/#xpzikv")]
    [InlineData("https://player.zilla-networks.com/play/abc123")]
    [InlineData("https://mega.nz/embed/ntxzURRJ#clave")]
    public void EsUrlEmbedPermitida_ProveedoresConocidos_DeberiaAceptar(string url)
    {
        UrlSeguridad.EsUrlEmbedPermitida(url).Should().BeTrue();
    }

    [Theory]
    [InlineData("https://servidor-desconocido.com/e/abc")]
    [InlineData("http://voe.sx/e/abc")]
    [InlineData("https://voe.sx.evil.com/e/abc")]
    [InlineData("ftp://voe.sx/abc")]
    [InlineData("no-es-una-url")]
    [InlineData("")]
    [InlineData(null)]
    public void EsUrlEmbedPermitida_ProveedoresNoPermitidos_DeberiaRechazar(string? url)
    {
        UrlSeguridad.EsUrlEmbedPermitida(url).Should().BeFalse();
    }

    // === EsUrlManifiestoStreaming (HLS/DASH: fase 2) ===

    [Theory]
    [InlineData("https://cdn.example.com/master.m3u8")]
    [InlineData("https://cdn.example.com/stream/manifest.mpd")]
    public void EsUrlManifiestoStreaming_Manifiestos_DeberiaDetectar(string url)
    {
        UrlSeguridad.EsUrlManifiestoStreaming(url).Should().BeTrue();
    }

    [Theory]
    [InlineData("https://cdn.example.com/video.mp4")]
    [InlineData("https://cdn.example.com/video.mkv?v=2")]
    [InlineData("")]
    [InlineData(null)]
    public void EsUrlManifiestoStreaming_ArchivosDirectos_DeberiaRechazar(string? url)
    {
        UrlSeguridad.EsUrlManifiestoStreaming(url).Should().BeFalse();
    }

    // === EsUrlDescargaHttpSegura (punto de descarga, defensa en profundidad) ===

    [Theory]
    [InlineData("https://cdn.mp4upload.com/abc/720p/video.mp4")]
    [InlineData("https://cdn.otro-cdn.com/stream/master.m3u8")]
    [InlineData("https://host.com/video.mkv?v=2")]
    public void EsUrlDescargaHttpSegura_HttpsAbsoluta_DeberiaAceptar(string url)
    {
        UrlSeguridad.EsUrlDescargaHttpSegura(url).Should().BeTrue();
    }

    [Theory]
    [InlineData("http://cdn.mp4upload.com/video.mp4")]
    [InlineData("https://user:pass@host.com/video.mp4")]
    [InlineData("ftp://host.com/video.mp4")]
    [InlineData("file:///C:/videos/video.mp4")]
    [InlineData("data:text/plain;base64,AAAA")]
    [InlineData("video.mp4")]
    [InlineData("/ruta/local/video.mp4")]
    [InlineData("")]
    [InlineData(null)]
    public void EsUrlDescargaHttpSegura_UrlsInseguras_DeberiaRechazar(string? url)
    {
        UrlSeguridad.EsUrlDescargaHttpSegura(url).Should().BeFalse();
    }

    // === SEC-02: destinos locales/privados ===

    [Theory]
    [InlineData("https://127.0.0.1/video.mp4")]
    [InlineData("https://localhost/video.mp4")]
    [InlineData("https://LOCALHOST:8443/video.mp4")]
    [InlineData("https://10.0.0.7/video.mp4")]
    [InlineData("https://172.16.5.4/video.mp4")]
    [InlineData("https://172.31.255.1/video.mp4")]
    [InlineData("https://192.168.0.10/video.mp4")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://100.64.1.1/video.mp4")]
    [InlineData("https://0.0.0.0/video.mp4")]
    [InlineData("https://[::1]/video.mp4")]
    [InlineData("https://[fe80::1]/video.mp4")]
    [InlineData("https://[fd12:3456::1]/video.mp4")]
    [InlineData("https://[::ffff:192.168.1.1]/video.mp4")]
    [InlineData("https://nas/video.mp4")]
    [InlineData("https://impresora.local/video.mp4")]
    [InlineData("https://servicio.internal/video.mp4")]
    [InlineData("https://router.lan/video.mp4")]
    public void EsUrlDescargaHttpSegura_HostLocalOPrivado_DeberiaRechazar(string url)
    {
        UrlSeguridad.EsUrlDescargaHttpSegura(url).Should().BeFalse();
    }

    [Theory]
    [InlineData("https://8.8.8.8/video.mp4")]
    [InlineData("https://93.184.216.34/video.mp4")]
    [InlineData("https://172.32.0.1/video.mp4")]   // justo fuera de 172.16/12
    [InlineData("https://172.15.0.1/video.mp4")]
    [InlineData("https://100.63.0.1/video.mp4")]   // justo fuera de 100.64/10
    [InlineData("https://[2606:4700:4700::1111]/video.mp4")]
    [InlineData("https://cdn.ejemplo.com/video.mp4")]
    [InlineData("https://mi-local.com/video.mp4")] // "local" en medio del nombre no es el sufijo .local
    public void EsUrlDescargaHttpSegura_HostPublico_DeberiaAceptar(string url)
    {
        UrlSeguridad.EsUrlDescargaHttpSegura(url).Should().BeTrue();
    }

    [Theory]
    [InlineData("192.168.1.1", false)]
    [InlineData("8.8.4.4", true)]
    [InlineData("198.18.0.1", false)]
    [InlineData("198.20.0.1", true)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("240.0.0.1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("2001:4860:4860::8888", true)]
    [InlineData("ff02::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("::ffff:8.8.8.8", true)]
    public void EsIpPublica_DeberiaClasificarLosRangos(string ip, bool esperado)
    {
        UrlSeguridad.EsIpPublica(System.Net.IPAddress.Parse(ip)).Should().Be(esperado);
    }
}
