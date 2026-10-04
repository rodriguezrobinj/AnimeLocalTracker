using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Moq;
using Moq.Protected;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>Portadas: carpeta temporal inyectada, nunca la real de datos del usuario.</summary>
public sealed class ImageCacheServiceTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), $"covers_{Guid.NewGuid():N}");

    public ImageCacheServiceTests() => Directory.CreateDirectory(_carpeta);

    public void Dispose()
    {
        try { Directory.Delete(_carpeta, recursive: true); } catch { /* limpieza best-effort */ }
    }

    private static byte[] PngValido()
    {
        var bitmap = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null, new byte[16 * 16 * 4], 16 * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private ImageCacheService CrearServicio(HttpClient cliente)
    {
        var fabrica = new Mock<IHttpClientFactory>();
        fabrica.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(cliente);
        return new ImageCacheService(fabrica.Object, _carpeta);
    }

    [Fact]
    public void ObtenerPortada_DeberiaCargarPortadasExistentes()
    {
        var bytes = PngValido();
        int[] ids = [101, 202];
        foreach (var id in ids) File.WriteAllBytes(Path.Combine(_carpeta, $"{id}.jpg"), bytes);

        var service = CrearServicio(new HttpClient());

        foreach (var id in ids)
            service.ObtenerPortada(id, "https://example.com/test.jpg")
                .Should().NotBeNull($"la portada del anime {id} existe en disco y debe cargarse");
    }

    [Fact]
    public async Task ObtenerPortadaAsync_DeberiaDecodificarYGuardarCorrectamente()
    {
        var bytes = PngValido();
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage
            {
                StatusCode = System.Net.HttpStatusCode.OK,
                Content = new ByteArrayContent(bytes)
            });

        var service = CrearServicio(new HttpClient(handlerMock.Object));
        var result = await service.ObtenerPortadaAsync(888888, "https://s4.anilist.co/file/anilistcdn/media/anime/cover/medium/bx185801-test.png");

        result.Should().NotBeNull();
        File.Exists(Path.Combine(_carpeta, "888888.jpg")).Should().BeTrue("la portada descargada se guarda en la carpeta inyectada");

        // Comprobar hit en caché de memoria
        var cached = service.ObtenerPortada(888888, "https://example.com/test-cover.png");
        cached.Should().BeSameAs(result);
    }
}
