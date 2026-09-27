using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// Solo cubre las salidas tempranas (sin lanzar ffmpeg de verdad, que necesitaría un video/pista real): archivo
/// inexistente, ni pista incrustada ni externa indicada. El camino feliz (ffmpeg de verdad + SubtitulosSrtParser)
/// se probó a mano con un anime real — ver memoria del proyecto.
/// </summary>
public class SubtitleCuesExtractorServiceTests
{
    private readonly SubtitleCuesExtractorService _sut = new();

    [Fact]
    public async Task ArchivoInexistente_DevuelveListaVacia()
    {
        var resultado = await _sut.ExtraerAsync(@"C:\no\existe\video.mkv", 2, null, CancellationToken.None);

        resultado.Should().BeEmpty();
    }

    [Fact]
    public async Task RutaVacia_DevuelveListaVacia()
    {
        var resultado = await _sut.ExtraerAsync(string.Empty, 2, null, CancellationToken.None);

        resultado.Should().BeEmpty();
    }

    [Fact]
    public async Task SinIndiceEmbebidoYSinRutaExterna_DevuelveListaVacia()
    {
        // Ni pista incrustada (streamIndexEmbebido null) ni pista externa: no hay nada que extraer.
        var resultado = await _sut.ExtraerAsync(@"C:\algun\video.mkv", null, null, CancellationToken.None);

        resultado.Should().BeEmpty();
    }
}
