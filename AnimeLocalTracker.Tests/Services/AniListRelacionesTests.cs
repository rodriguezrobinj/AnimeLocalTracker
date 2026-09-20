#pragma warning disable CA1861 // Datos constantes de prueba: un arreglo por llamada es lo más legible aquí
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Moq;
using Moq.Protected;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>Consulta por lotes de las relaciones (precuela, secuela, spin-off…) a AniList.</summary>
public class AniListRelacionesTests
{
    private readonly Mock<HttpMessageHandler> _handlerMock = new();
    private readonly List<string> _peticiones = new();
    private readonly AniListTrackingService _sut;

    public AniListRelacionesTests()
    {
        _sut = new AniListTrackingService(new HttpClient(_handlerMock.Object), new Mock<IAuthService>().Object);
    }

    private void Responder(HttpStatusCode codigo, string json) =>
        _handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>(async (peticion, ct) =>
            {
                _peticiones.Add(peticion.Content == null ? string.Empty : await peticion.Content.ReadAsStringAsync(ct));
                return new HttpResponseMessage { StatusCode = codigo, Content = new StringContent(json) };
            });

    private const string RespuestaValida = @"{
        ""data"": { ""Page"": { ""media"": [
            { ""id"": 1, ""relations"": { ""edges"": [
                { ""relationType"": ""SEQUEL"", ""node"": { ""id"": 2, ""type"": ""ANIME"" } },
                { ""relationType"": ""SIDE_STORY"", ""node"": { ""id"": 3, ""type"": ""ANIME"" } },
                { ""relationType"": ""ADAPTATION"", ""node"": { ""id"": 900, ""type"": ""MANGA"" } }
            ] } },
            { ""id"": 5, ""relations"": { ""edges"": [] } },
            { ""id"": 6, ""relations"": null }
        ] } }
    }";

    [Fact]
    public async Task DevuelveLasRelacionesConOtrosAnimes_YOmiteMangaYNovela()
    {
        Responder(HttpStatusCode.OK, RespuestaValida);

        var resultado = await _sut.ObtenerRelacionesLoteAsync(new[] { 1, 5, 6 });

        resultado[1].Select(r => (r.AnimeId, r.RelacionadoId, r.Tipo)).Should().Equal((1, 2, "SEQUEL"), (1, 3, "SIDE_STORY"));
    }

    [Fact]
    public async Task UnAnimeSinRelaciones_TambienApareceConListaVacia()
    {
        Responder(HttpStatusCode.OK, RespuestaValida);

        var resultado = await _sut.ObtenerRelacionesLoteAsync(new[] { 1, 5, 6 });

        // La clave presente significa "consultado bien"; sin ella el llamador no sabría si falló
        resultado.Should().ContainKey(5).WhoseValue.Should().BeEmpty();
        resultado.Should().ContainKey(6).WhoseValue.Should().BeEmpty();
    }

    [Fact]
    public async Task PideRelationTypeVersion2_YSoloAnimes()
    {
        Responder(HttpStatusCode.OK, RespuestaValida);

        await _sut.ObtenerRelacionesLoteAsync(new[] { 1 });

        _peticiones.Should().ContainSingle().Which.Should().Contain("relationType(version: 2)").And.Contain("type: ANIME");
    }

    [Fact]
    public async Task ConMasDeCincuentaIds_SeDivideEnLotes()
    {
        Responder(HttpStatusCode.OK, @"{ ""data"": { ""Page"": { ""media"": [] } } }");

        await _sut.ObtenerRelacionesLoteAsync(Enumerable.Range(1, 120));

        _peticiones.Should().HaveCount(3, "50 + 50 + 20");
    }

    [Fact]
    public async Task IdsRepetidosOInvalidos_SeIgnoran()
    {
        Responder(HttpStatusCode.OK, @"{ ""data"": { ""Page"": { ""media"": [] } } }");

        await _sut.ObtenerRelacionesLoteAsync(new[] { 0, -3, 4, 4, 4 });

        _peticiones.Should().ContainSingle();
    }

    [Fact]
    public async Task ListaVacia_NoLlamaALaRed()
    {
        var resultado = await _sut.ObtenerRelacionesLoteAsync(System.Array.Empty<int>());

        resultado.Should().BeEmpty();
        _peticiones.Should().BeEmpty();
    }

    [Fact]
    public async Task ErrorHttp_DevuelveVacioSinLanzar()
    {
        Responder(HttpStatusCode.InternalServerError, "{}");

        var resultado = await _sut.ObtenerRelacionesLoteAsync(new[] { 1 });

        resultado.Should().BeEmpty();
    }

    [Fact]
    public async Task ErroresGraphQL_DevuelvenVacio()
    {
        Responder(HttpStatusCode.OK, @"{ ""errors"": [ { ""message"": ""boom"" } ] }");

        (await _sut.ObtenerRelacionesLoteAsync(new[] { 1 })).Should().BeEmpty();
    }
}
