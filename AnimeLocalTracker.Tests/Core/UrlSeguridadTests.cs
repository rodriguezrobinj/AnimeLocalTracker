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
}
