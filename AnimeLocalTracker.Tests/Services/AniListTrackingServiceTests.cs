using System;
using System.Collections.Generic;
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
    private readonly Mock<IAuthService> _authServiceMock;
    private readonly AniListTrackingService _sut; // System Under Test

    public AniListTrackingServiceTests()
    {
        _httpMessageHandlerMock = new Mock<HttpMessageHandler>();
        _authServiceMock = new Mock<IAuthService>();
        var httpClient = new HttpClient(_httpMessageHandlerMock.Object);
        _sut = new AniListTrackingService(httpClient, _authServiceMock.Object);
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

    [Fact]
    public async Task ObtenerAnimePorIdAsync_DeberiaAdjuntarBearerToken_CuandoUsuarioEstaAutenticado()
    {
        // Arrange
        int expectedId = 55544;
        string token = "test_token_12345";
        _authServiceMock.Setup(a => a.ObtenerTokenGuardado()).Returns(token);

        HttpRequestMessage? capturedRequest = null;

        var mockResponse = new
        {
            data = new
            {
                Media = new AniListMedia
                {
                    Id = expectedId,
                    Title = new AniListTitle { Romaji = "Bleach" },
                    Episodes = 366,
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
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(responseMessage);

        // Act
        var result = await _sut.ObtenerAnimePorIdAsync(expectedId);

        // Assert
        result.Should().NotBeNull();
        capturedRequest.Should().NotBeNull();
        capturedRequest!.Headers.Authorization.Should().NotBeNull();
        capturedRequest.Headers.Authorization!.Scheme.Should().Be("Bearer");
        capturedRequest.Headers.Authorization.Parameter.Should().Be(token);
    }

    [Fact]
    public async Task BuscarAnimesEnVivoAsync_DeberiaAdjuntarBearerToken_CuandoUsuarioEstaAutenticado()
    {
        // Arrange
        string query = "naruto";
        string token = "test_bearer_token";
        _authServiceMock.Setup(a => a.ObtenerTokenGuardado()).Returns(token);

        HttpRequestMessage? capturedRequest = null;

        var mockResponse = new
        {
            data = new
            {
                Page = new AniListPage
                {
                    Media = new List<AniListMedia>
                    {
                        new() { Id = 20, Title = new AniListTitle { Romaji = "Naruto" } }
                    }
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
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(responseMessage);

        // Act
        var results = await _sut.BuscarAnimesEnVivoAsync(query);

        // Assert
        results.Should().HaveCount(1);
        capturedRequest.Should().NotBeNull();
        capturedRequest!.Headers.Authorization.Should().NotBeNull();
        capturedRequest.Headers.Authorization!.Parameter.Should().Be(token);
    }

    [Fact]
    public async Task ObtenerCalendarioEmisionAsync_DeberiaFiltrarIdsInvalidos_YNoLlamarHttpSiVacio()
    {
        // Arrange
        var invalidIds = new List<int> { 0, -1, -5 };

        // Act
        var result = await _sut.ObtenerCalendarioEmisionAsync(invalidIds, 1700000000, 1700604800);

        // Assert
        result.Should().BeEmpty();
        _httpMessageHandlerMock.Protected().Verify("SendAsync", Times.Never(), ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task ActualizarProgresoAsync_Con401_DeberiaCerrarSesionLocal()
    {
        // Arrange: token revocado → AniList responde 401
        _authServiceMock.Setup(a => a.EstaAutenticado()).Returns(true);
        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage { StatusCode = HttpStatusCode.Unauthorized });

        // Act
        bool resultado = await _sut.ActualizarProgresoAsync(16498, 5, "token-invalido");

        // Assert: la mutación falla y la sesión local se cierra para forzar re-login
        resultado.Should().BeFalse();
        _authServiceMock.Verify(a => a.CerrarSesion(), Times.Once);
    }

    [Fact]
    public async Task ActualizarProgresoAsync_Con401_YaSesionCerrada_NoDeberiaRepetirCerrarSesion()
    {
        // Arrange: ya no está autenticado (sesión cerrada en un 401 anterior)
        _authServiceMock.Setup(a => a.EstaAutenticado()).Returns(false);
        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage { StatusCode = HttpStatusCode.Unauthorized });

        // Act
        bool resultado = await _sut.ActualizarProgresoAsync(16498, 5, "token-invalido");

        // Assert
        resultado.Should().BeFalse();
        _authServiceMock.Verify(a => a.CerrarSesion(), Times.Never);
    }
}
