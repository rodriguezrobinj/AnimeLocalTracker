using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

public class RedirectSeguroHandlerTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _fabricas;

        public StubHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] fabricas)
        {
            _fabricas = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>(fabricas);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var fabrica = _fabricas.Count > 1 ? _fabricas.Dequeue() : _fabricas.Peek();
            return Task.FromResult(fabrica(request));
        }
    }

    private static HttpResponseMessage Redireccion(string location)
    {
        var respuesta = new HttpResponseMessage(HttpStatusCode.Found);
        respuesta.Headers.Location = new Uri(location);
        return respuesta;
    }

    private static HttpClient ClienteCon(params Func<HttpRequestMessage, HttpResponseMessage>[] fabricas)
    {
        return new HttpClient(new RedirectSeguroHandler { InnerHandler = new StubHandler(fabricas) });
    }

    [Fact]
    public async Task SendAsync_ConRedireccionAHttps_DeberiaSeguirElSalto()
    {
        // Arrange: 302 a https válido y luego 200
        using var client = ClienteCon(
            _ => Redireccion("https://cdn.example.com/video.mp4"),
            _ => new HttpResponseMessage(HttpStatusCode.OK));

        // Act
        using var respuesta = await client.GetAsync("https://origen.example.com/archivo");

        // Assert
        respuesta.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SendAsync_ConRedireccionAHttp_DeberiaBloquearElSalto()
    {
        // Arrange: 302 hacia http (degradación de TLS)
        using var client = ClienteCon(_ => Redireccion("http://cdn.example.com/video.mp4"));

        // Act
        using var respuesta = await client.GetAsync("https://origen.example.com/archivo");

        // Assert
        respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SendAsync_ConRedireccionConCredencialesEmbebidas_DeberiaBloquearElSalto()
    {
        // Arrange: Location con userinfo (user:pass@host)
        using var client = ClienteCon(_ => Redireccion("https://usuario:clave@cdn.example.com/video.mp4"));

        // Act
        using var respuesta = await client.GetAsync("https://origen.example.com/archivo");

        // Assert
        respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SendAsync_ConCadenaDeRedireccionesInfinita_DeberiaCortarEnElLimite()
    {
        // Arrange: el servidor redirige siempre a sí mismo
        using var client = ClienteCon(_ => Redireccion("https://origen.example.com/archivo"));

        // Act
        using var respuesta = await client.GetAsync("https://origen.example.com/archivo");

        // Assert: el corte no debe colgar ni exceder los 5 saltos
        respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
