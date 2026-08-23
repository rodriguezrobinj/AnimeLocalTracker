using System;
using System.Collections.Generic;
using System.Linq;
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

public class AniListBusquedaYCalendarioTests
{
    private readonly Mock<HttpMessageHandler> _handlerMock = new();
    private readonly List<HttpRequestMessage> _peticionesCapturadas = new();

    private AniListTrackingService CreateService(HttpStatusCode statusCode, string jsonRespuesta)
    {
        _handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken ct) =>
            {
                _peticionesCapturadas.Add(req);
                return new HttpResponseMessage
                {
                    StatusCode = statusCode,
                    Content = new StringContent(jsonRespuesta)
                };
            });

        return new AniListTrackingService(new HttpClient(_handlerMock.Object));
    }

    [Fact]
    public async Task BuscarAnimesEnVivoAsync_DeberiaEnviarUserAgent_Siempre()
    {
        // Arrange: AniList (Cloudflare) rechaza con 403 las peticiones sin User-Agent
        var sut = CreateService(HttpStatusCode.OK, "{\"data\":{\"Page\":{\"media\":[]}}}");

        // Act
        await sut.BuscarAnimesEnVivoAsync("ua-probe");

        // Assert
        _peticionesCapturadas.Should().HaveCount(1);
        _peticionesCapturadas[0].Headers.UserAgent.Should().NotBeEmpty(
            "porque sin User-Agent Cloudflare responde 403 y el buscador queda muerto");
    }

    [Fact]
    public async Task ObtenerCalendarioEmisionAsync_DeberiaEnviarUserAgent_Siempre()
    {
        // Arrange
        var sut = CreateService(HttpStatusCode.OK, "{\"data\":{\"Page\":{\"airingSchedules\":[]}}}");

        // Act
        await sut.ObtenerCalendarioEmisionAsync(new List<int> { 1 }, 0, 100);

        // Assert
        _peticionesCapturadas.Should().HaveCount(1);
        _peticionesCapturadas[0].Headers.UserAgent.Should().NotBeEmpty();
    }

    [Fact]
    public async Task BuscarAnimesEnVivoAsync_ConRespuestaValida_DeberiaMapearResultados()
    {
        // Arrange
        string json = """
        {
          "data": {
            "Page": {
              "media": [
                {
                  "id": 154587,
                  "idMal": 52991,
                  "title": { "romaji": "Sousou no Frieren", "english": "Frieren: Beyond Journey's End", "userPreferred": "Sousou no Frieren" },
                  "synonyms": ["Frieren"],
                  "coverImage": { "extraLarge": "https://s4.anilist.co/file/x.png" },
                  "status": "FINISHED",
                  "description": "La maga Frieren...",
                  "genres": ["Aventura", "Fantasía"],
                  "episodes": 28,
                  "startDate": { "year": 2023, "month": 9, "day": 29 },
                  "nextAiringEpisode": null
                }
              ]
            }
          }
        }
        """;
        var sut = CreateService(HttpStatusCode.OK, json);

        // Act
        var resultados = await sut.BuscarAnimesEnVivoAsync("frieren");

        // Assert
        resultados.Should().HaveCount(1);
        var media = resultados[0];
        media.Id.Should().Be(154587);
        media.Title.Romaji.Should().Be("Sousou no Frieren");
        media.Title.English.Should().Be("Frieren: Beyond Journey's End");
        media.Status.Should().Be("FINISHED");
        media.Episodes.Should().Be(28);
        media.StartDate!.Year.Should().Be(2023);
        media.FormattedStatus.Should().Be("Finalizado");
        media.FormattedGenres.Should().Contain("Aventura");
    }

    [Fact]
    public async Task BuscarAnimesEnVivoAsync_ConErrorGraphQL_DeberiaDevolverVacioYNoExplotar()
    {
        // Arrange: HTTP 200 pero con errores GraphQL (p.ej. query inválida o rate-limit lógico)
        string json = """{"errors":[{"message":"Too Many Requests."}],"data":null}""";
        var sut = CreateService(HttpStatusCode.OK, json);

        // Act
        var resultados = await sut.BuscarAnimesEnVivoAsync("graphql-error-probe");

        // Assert
        resultados.Should().BeEmpty();
    }

    [Fact]
    public async Task BuscarAnimesEnVivoAsync_Con403Cloudflare_DeberiaDevolverVacio()
    {
        // Arrange
        var sut = CreateService(HttpStatusCode.Forbidden, "<html>Forbidden</html>");

        // Act
        var resultados = await sut.BuscarAnimesEnVivoAsync("cloudflare-probe");

        // Assert
        resultados.Should().BeEmpty();
    }

    [Fact]
    public async Task ObtenerCalendarioEmisionAsync_ConRespuestaValida_DeberiaMapearEmisiones()
    {
        // Arrange: airingAt = sábado 2026-08-22 21:00 UTC -> 1724350800
        long airingAt = DateTimeOffset.Parse("2026-08-22T21:00:00Z").ToUnixTimeSeconds();
        string json = $$"""
        {
          "data": {
            "Page": {
              "airingSchedules": [
                {
                  "episode": 1172,
                  "airingAt": {{airingAt}},
                  "media": {
                    "id": 21,
                    "title": { "romaji": "One Piece" },
                    "coverImage": { "extraLarge": "https://s4.anilist.co/onepiece.png" }
                  }
                }
              ]
            }
          }
        }
        """;
        var sut = CreateService(HttpStatusCode.OK, json);

        // Act
        var emisiones = await sut.ObtenerCalendarioEmisionAsync(new List<int> { 21 }, 0, long.MaxValue);

        // Assert
        emisiones.Should().HaveCount(1);
        var ep = emisiones[0];
        ep.AniListId.Should().Be(21);
        ep.Titulo.Should().Be("One Piece");
        ep.NumeroEpisodio.Should().Be(1172);
        ep.UrlPortada.Should().Be("https://s4.anilist.co/onepiece.png");
        ep.FechaEmision.Should().Be(DateTimeOffset.Parse("2026-08-22T21:00:00Z").DateTime);
        ep.DiaSemana.Should().Be(DateTimeOffset.Parse("2026-08-22T21:00:00Z").LocalDateTime.DayOfWeek);
    }

    [Fact]
    public async Task ObtenerCalendarioEmisionAsync_ConErrorGraphQL_DeberiaDevolverVacio()
    {
        // Arrange
        string json = """{"errors":[{"message":"Invalid variable"}]}""";
        var sut = CreateService(HttpStatusCode.OK, json);

        // Act
        var emisiones = await sut.ObtenerCalendarioEmisionAsync(new List<int> { 21 }, 0, 100);

        // Assert
        emisiones.Should().BeEmpty();
    }

    [Fact]
    public async Task ObtenerCalendarioEmisionAsync_DeberiaUsarCache_EnLlamadasIdenticas()
    {
        // Arrange
        string json = """{"data":{"Page":{"airingSchedules":[]}}}""";
        var sut = CreateService(HttpStatusCode.OK, json);
        var ids = new List<int> { 21, 154587 };

        // Act
        await sut.ObtenerCalendarioEmisionAsync(ids, 1000, 2000);
        await sut.ObtenerCalendarioEmisionAsync(new List<int> { 154587, 21 }, 1000, 2000); // mismo rango, otro orden

        // Assert: la segunda llamada sale de caché (ids ordenados normalizan la clave)
        _peticionesCapturadas.Should().HaveCount(1);
    }
}
