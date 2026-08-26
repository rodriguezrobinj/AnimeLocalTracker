using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Core.Models;
using AnimeLocalTracker.Core.Services;
using FluentAssertions;
using Moq;
using Moq.Protected;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

public class AniSkipServiceTests
{
    private (AniSkipService sut, Mock<HttpMessageHandler> handlerMock) CreateSut(HttpResponseMessage response)
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(response);

        var httpClient = new HttpClient(handlerMock.Object);
        var sut = new AniSkipService(httpClient);
        return (sut, handlerMock);
    }

    [Fact]
    public async Task ObtenerSkipTimesAsync_ConRespuestaExitosa_DeberiaMapearResultadosCorrectamente()
    {
        // Arrange
        string json = @"
        {
            ""found"": true,
            ""results"": [
                {
                    ""interval"": {
                        ""startTime"": 85.5,
                        ""endTime"": 175.5
                    },
                    ""skipType"": ""op"",
                    ""skipId"": ""skip_op_1"",
                    ""episodeLength"": 1420.0
                },
                {
                    ""interval"": {
                        ""startTime"": 1300.0,
                        ""endTime"": 1390.0
                    },
                    ""skipType"": ""ed"",
                    ""skipId"": ""skip_ed_1"",
                    ""episodeLength"": 1420.0
                }
            ],
            ""statusCode"": 200
        }";

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        var (sut, _) = CreateSut(response);

        // Act
        var results = await sut.ObtenerSkipTimesAsync(52991, 1, 1420);

        // Assert
        results.Should().NotBeNull();
        results.Should().HaveCount(2);

        var op = results[0];
        op.SkipType.Should().Be("op");
        op.Interval.StartTime.Should().Be(85.5);
        op.Interval.EndTime.Should().Be(175.5);
        op.EsIntro.Should().BeTrue();
        op.EsEnding.Should().BeFalse();
        op.TextoBoton.Should().Be("Saltar intro");

        var ed = results[1];
        ed.SkipType.Should().Be("ed");
        ed.Interval.StartTime.Should().Be(1300.0);
        ed.Interval.EndTime.Should().Be(1390.0);
        ed.EsIntro.Should().BeFalse();
        ed.EsEnding.Should().BeTrue();
        ed.TextoBoton.Should().Be("Saltar ending");
    }

    [Fact]
    public async Task ObtenerSkipTimesAsync_Con404NotFound_DeberiaRetornarListaVacia()
    {
        // Arrange
        var response = new HttpResponseMessage(HttpStatusCode.NotFound);
        var (sut, _) = CreateSut(response);

        // Act
        var results = await sut.ObtenerSkipTimesAsync(999999, 1, 1400);

        // Assert
        results.Should().NotBeNull();
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task ObtenerMalIdDesdeAniListAsync_DeberiaRetornarIdCorrecto()
    {
        // Arrange
        string json = @"
        {
            ""data"": {
                ""Media"": {
                    ""id"": 154587,
                    ""idMal"": 52991
                }
            }
        }";

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        var (sut, _) = CreateSut(response);

        // Act
        var malId = await sut.ObtenerMalIdDesdeAniListAsync(154587);

        // Assert
        malId.Should().Be(52991);
    }
}
