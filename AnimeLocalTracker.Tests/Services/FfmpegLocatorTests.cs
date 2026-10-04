using System;
using System.IO;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>En un build publicado la app solo ejecuta el ffmpeg que trae; el del sistema es un recurso de desarrollo.</summary>
public sealed class FfmpegLocatorTests : IDisposable
{
    private readonly string _instalacion = Path.Combine(Path.GetTempPath(), "ALT_ffmpeg_" + Guid.NewGuid().ToString("N"));

    public FfmpegLocatorTests() => Directory.CreateDirectory(_instalacion);

    public void Dispose()
    {
        try { Directory.Delete(_instalacion, recursive: true); } catch { /* limpieza best-effort */ }
    }

    private string RutaEmbebida(string nombre) => Path.Combine(_instalacion, "FFmpeg", nombre + ".exe");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConElEmbebidoPresente_SiempreSeUsaEse(bool desarrollo)
    {
        Directory.CreateDirectory(Path.Combine(_instalacion, "FFmpeg"));
        File.WriteAllText(RutaEmbebida("ffmpeg"), "x");

        FfmpegLocator.Resolver("ffmpeg", _instalacion, desarrollo).Should().Be(RutaEmbebida("ffmpeg"));
    }

    [Fact]
    public void SinElEmbebido_EnBuildPublicado_NoSeRecurreAlDelSistema()
    {
        // Una ruta completa que no existe falla al lanzarse; el nombre a secas ejecutaría el primer "ffprobe" que Windows
        // encontrara en la carpeta actual o en el PATH.
        FfmpegLocator.Resolver("ffprobe", _instalacion, desarrollo: false).Should().Be(RutaEmbebida("ffprobe"));
    }

    [Fact]
    public void SinElEmbebido_EnDesarrollo_SeUsaElDelSistema()
    {
        FfmpegLocator.Resolver("ffprobe", _instalacion, desarrollo: true).Should().Be("ffprobe");
    }
}
