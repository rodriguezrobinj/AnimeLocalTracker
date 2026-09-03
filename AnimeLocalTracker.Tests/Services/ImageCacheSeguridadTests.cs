using System;
using System.Linq;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

public class ImageCacheSeguridadTests
{
    [Theory]
    [InlineData("https://s4.anilist.co/file/anilistcdn/media/anime/cover/medium/bx1.jpg", true)]
    [InlineData("https://anilist.co/img/logo.png", true)]
    [InlineData("https://cdn.anilist.co/img/x.jpg", true)]
    [InlineData("https://example.com/test.jpg", false)]
    [InlineData("http://s4.anilist.co/x.jpg", false)]
    [InlineData("https://s4.anilist.co.evil.com/x.jpg", false)]
    [InlineData("https://anilist.co.evil.com/x.jpg", false)]
    [InlineData("file:///c:/temp/x.jpg", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void EsHostPortadaPermitido_DeberiaAceptarSoloLaCdnDeAniListEnHttps(string? url, bool esperado)
    {
        ImageCacheService.EsHostPortadaPermitido(url).Should().Be(esperado);
    }

    [Fact]
    public void EsImagenValida_DeberiaAceptarCabecerasDeFormatosConocidos()
    {
        // JPEG
        ImageCacheService.EsImagenValida(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })
            .Should().BeTrue();
        // PNG
        ImageCacheService.EsImagenValida(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0, 0, 0 })
            .Should().BeTrue();
        // GIF
        ImageCacheService.EsImagenValida(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0, 0, 0, 0, 0, 0, 0, 0 })
            .Should().BeTrue();
        // WebP (RIFF....WEBP)
        ImageCacheService.EsImagenValida(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50, 0, 0 })
            .Should().BeTrue();
    }

    [Fact]
    public void EsImagenValida_DeberiaRechazarBytesQueNoSonImagen()
    {
        ImageCacheService.EsImagenValida(Enumerable.Repeat((byte)0x41, 64).ToArray()).Should().BeFalse();
        ImageCacheService.EsImagenValida(Array.Empty<byte>()).Should().BeFalse();
        ImageCacheService.EsImagenValida(new byte[] { 0xFF, 0xD8 }).Should().BeFalse();
    }
}
