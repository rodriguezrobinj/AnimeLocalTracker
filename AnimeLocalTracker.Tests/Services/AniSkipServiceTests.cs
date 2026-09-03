using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Moq;
using Moq.Protected;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// AniSkipService con HttpClient mockeado (nunca toca red): parsing de la API,
/// caché en memoria y casos límite (404, found=false, errores).
/// NOTA: las cachés son estáticas — cada test usa IDs distintos para no interferir.
/// </summary>
public class AniSkipServiceTests
{
    private static AniSkipService CrearServicio(Mock<HttpMessageHandler> handler)
        => new(new HttpClient(handler.Object));

    private static Mock<HttpMessageHandler> CrearHandler(HttpResponseMessage respuesta)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(respuesta);
        return handler;
    }

    private static string JsonConResultados(double start, double end, string skipType = "op")
    {
        // CA1311: ToString() de doubles usa la cultura del sistema (es-ES → coma decimal),
        // lo que genera JSON inválido. InvariantCulture obligatorio.
        string s = start.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        string e = end.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        return $$"""
        {"found":true,"results":[
          {"interval":{"startTime":{{s}},"endTime":{{e}}},"skipType":"{{skipType}}","skipId":"x1","episodeLength":1428}
        ]}
        """;
    }

    [Fact]
    public async Task ObtenerSkipTimesAsync_MalIdOEpisodioInvalidos_DeberiaDevolverVacioSinRed()
    {
        // Arrange
        var handler = CrearHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var sut = CrearServicio(handler);

        // Act
        var r1 = await sut.ObtenerSkipTimesAsync(0, 5);
        var r2 = await sut.ObtenerSkipTimesAsync(123, 0);

        // Assert: nunca se hizo la llamada HTTP
        r1.Should().BeEmpty();
        r2.Should().BeEmpty();
        handler.Protected().Verify("SendAsync", Times.Never(),
            ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task ObtenerSkipTimesAsync_Respuesta404_DeberiaDevolverVacioYCachear()
    {
        // Arrange
        var handler = CrearHandler(new HttpResponseMessage(HttpStatusCode.NotFound));
        var sut = CrearServicio(handler);

        // Act: dos llamadas seguidas (la segunda debe venir de la caché)
        var r1 = await sut.ObtenerSkipTimesAsync(101, 1);
        var r2 = await sut.ObtenerSkipTimesAsync(101, 1);

        // Assert: ambas vacías y solo 1 llamada HTTP
        r1.Should().BeEmpty();
        r2.Should().BeEmpty();
        handler.Protected().Verify("SendAsync", Times.Once(),
            ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task ObtenerSkipTimesAsync_RespuestaValida_DeberiaDevolverResultadosYCachear()
    {
        // Arrange
        var handler = CrearHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonConResultados(75.5, 90.0, "op"))
        });
        var sut = CrearServicio(handler);

        // Act
        var r1 = await sut.ObtenerSkipTimesAsync(102, 2);
        var r2 = await sut.ObtenerSkipTimesAsync(102, 2);

        // Assert: resultados parseados y la segunda llamada sale de la caché
        r1.Should().HaveCount(1);
        r1[0].Interval.StartTime.Should().Be(75.5);
        r1[0].Interval.EndTime.Should().Be(90.0);
        r1[0].SkipType.Should().Be("op");
        r1[0].EsIntro.Should().BeTrue();
        r2.Should().HaveCount(1);
        handler.Protected().Verify("SendAsync", Times.Once(),
            ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task ObtenerSkipTimesAsync_FoundFalse_DeberiaDevolverVacio()
    {
        // Arrange
        var handler = CrearHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"found":false,"results":[],"message":"No skip data found"}""")
        });
        var sut = CrearServicio(handler);

        // Act
        var r = await sut.ObtenerSkipTimesAsync(103, 1);

        // Assert
        r.Should().BeEmpty();
    }

    [Fact]
    public async Task ObtenerSkipTimesAsync_ErrorDeRed_DeberiaDevolverVacioSinLanzar()
    {
        // Arrange: el handler lanza (timeout simulado)
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new TaskCanceledException("timeout"));
        var sut = CrearServicio(handler);

        // Act & Assert
        var r = await sut.ObtenerSkipTimesAsync(104, 1);
        r.Should().BeEmpty();
    }

    [Fact]
    public async Task ObtenerMalIdDesdeAniListAsync_ConIdMal_DeberiaDevolverloYCachear()
    {
        // Arrange
        var handler = CrearHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"data":{"Media":{"id":105,"idMal":42033}}}""")
        });
        var sut = CrearServicio(handler);

        // Act
        var r1 = await sut.ObtenerMalIdDesdeAniListAsync(105);
        var r2 = await sut.ObtenerMalIdDesdeAniListAsync(105);

        // Assert: 42033 y solo 1 llamada HTTP (caché)
        r1.Should().Be(42033);
        r2.Should().Be(42033);
        handler.Protected().Verify("SendAsync", Times.Once(),
            ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task ObtenerMalIdDesdeAniListAsync_AniListIdInvalido_DeberiaDevolverNullSinRed()
    {
        // Arrange
        var handler = CrearHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var sut = CrearServicio(handler);

        // Act
        var r = await sut.ObtenerMalIdDesdeAniListAsync(0);

        // Assert
        r.Should().BeNull();
        handler.Protected().Verify("SendAsync", Times.Never(),
            ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
    }
}
