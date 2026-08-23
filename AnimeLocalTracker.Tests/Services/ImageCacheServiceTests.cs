using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Moq;
using Moq.Protected;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

public class ImageCacheServiceTests
{
    [Fact]
    public void ObtenerPortada_DeberiaCargarPortadasExistentes()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var coversDir = Path.Combine(appData, "AnimeLocalTracker", "Covers");
        
        var mockFactory = new Mock<IHttpClientFactory>();
        mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient());

        var service = new ImageCacheService(mockFactory.Object);

        if (!Directory.Exists(coversDir)) return;

        var coverFiles = Directory.GetFiles(coversDir, "*.jpg");
        foreach (var file in coverFiles)
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            if (int.TryParse(fileName, out int animeId))
            {
                var img = service.ObtenerPortada(animeId, "https://example.com/test.jpg");
                img.Should().NotBeNull($"Cover for animeId={animeId} should load successfully");
            }
        }
    }

    [Fact]
    public async Task ObtenerPortadaAsync_DeberiaDecodificarYGuardarCorrectamente()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var file185801 = Path.Combine(appData, "AnimeLocalTracker", "Covers", "185801.jpg");
        if (!File.Exists(file185801)) return;

        var bytes = await File.ReadAllBytesAsync(file185801);
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = System.Net.HttpStatusCode.OK,
                Content = new ByteArrayContent(bytes)
            });
        var client = new HttpClient(handlerMock.Object);
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(client);

        var service = new ImageCacheService(factoryMock.Object);
        var result = await service.ObtenerPortadaAsync(888888, "https://example.com/test-cover.png");
        
        result.Should().NotBeNull();
        
        // Comprobar hit en caché de memoria
        var cached = service.ObtenerPortada(888888, "https://example.com/test-cover.png");
        cached.Should().BeSameAs(result);
    }
}
