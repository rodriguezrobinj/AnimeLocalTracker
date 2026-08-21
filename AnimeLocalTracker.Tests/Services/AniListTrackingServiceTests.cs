using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Moq;
using Moq.Protected;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

public class AniListTrackingServiceTests
{
    private readonly Mock<HttpMessageHandler> _httpMessageHandlerMock;
    private readonly AniListTrackingService _sut; // System Under Test

    public AniListTrackingServiceTests()
    {
        _httpMessageHandlerMock = new Mock<HttpMessageHandler>();
        var httpClient = new HttpClient(_httpMessageHandlerMock.Object);
        _sut = new AniListTrackingService(httpClient);
    }

    [Fact]
    public async Task ObtenerAnimePorIdAsync_DeberiaDevolverAnime_CuandoRespuestaEsCorrecta()
    {
        // Arrange
        int expectedId = 16498;
        var mockResponse = new
        {
            data = new
            {
                Media = new AniListMedia
                {
                    Id = expectedId,
                    Title = new AniListTitle { Romaji = "Shingeki no Kyojin" },
                    Episodes = 25,
                    Status = "FINISHED"
                }
            }
        };

        var responseMessage = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent(JsonSerializer.Serialize(mockResponse))
        };

        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(responseMessage);

        // Act
        var result = await _sut.ObtenerAnimePorIdAsync(expectedId);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(expectedId);
        result.Title.Should().NotBeNull();
        result.Title!.Romaji.Should().Be("Shingeki no Kyojin");
        result.Episodes.Should().Be(25);
    }

    [Fact]
    public async Task ObtenerAnimePorIdAsync_DeberiaUsarCache_EnLlamadasConsecutivas()
    {
        // Arrange
        int expectedId = 999123;
        var mockResponse = new
        {
            data = new
            {
                Media = new AniListMedia
                {
                    Id = expectedId,
                    Title = new AniListTitle { Romaji = "Sousou no Frieren" },
                    Episodes = 28,
                    Status = "FINISHED"
                }
            }
        };

        var responseMessage = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent(JsonSerializer.Serialize(mockResponse))
        };

        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(responseMessage);

        // Act
        var firstResult = await _sut.ObtenerAnimePorIdAsync(expectedId);
        var secondResult = await _sut.ObtenerAnimePorIdAsync(expectedId);

        // Assert
        firstResult.Should().NotBeNull();
        secondResult.Should().NotBeNull();
        secondResult!.Title!.Romaji.Should().Be("Sousou no Frieren");

        // Verify that SendAsync was called only ONCE due to caching
        _httpMessageHandlerMock
            .Protected()
            .Verify(
                "SendAsync",
                Times.Once(),
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            );
    }
}
