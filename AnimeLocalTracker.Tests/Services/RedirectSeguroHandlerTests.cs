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

    private static readonly IPAddress IpPublica = IPAddress.Parse("93.184.216.34");

    /// <summary>Resolutor falso: los tests no deben depender del DNS real.</summary>
    private static Func<string, CancellationToken, Task<IPAddress[]>> ResolverA(params string[] ips)
        => (_, _) => Task.FromResult(Array.ConvertAll(ips, IPAddress.Parse));

    private static HttpClient ClienteCon(params Func<HttpRequestMessage, HttpResponseMessage>[] fabricas)
    {
        return new HttpClient(new RedirectSeguroHandler(ResolverA("93.184.216.34")) { InnerHandler = new StubHandler(fabricas) });
    }

    private static HttpClient ClienteConResolutor(Func<string, CancellationToken, Task<IPAddress[]>> resolutor, params Func<HttpRequestMessage, HttpResponseMessage>[] fabricas)
    {
        return new HttpClient(new RedirectSeguroHandler(resolutor) { InnerHandler = new StubHandler(fabricas) });
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.5")]
    [InlineData("192.168.1.50")]
    [InlineData("169.254.169.254")]
    [InlineData("::1")]
    [InlineData("fd00::1")]
    public async Task SendAsync_ConNombreQueResuelveAIpPrivada_DeberiaBloquearSinLlamarAlServidor(string ip)
    {
        int llamadas = 0;
        using var client = ClienteConResolutor(ResolverA(ip), _ => { llamadas++; return new HttpResponseMessage(HttpStatusCode.OK); });

        using var respuesta = await client.GetAsync("https://parece-publico.example.com/video.mp4");

        respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        llamadas.Should().Be(0, "el bloqueo ocurre ANTES de conectar");
    }

    [Fact]
    public async Task SendAsync_ConNombreConUnaIpPublicaYOtraPrivada_DeberiaBloquear()
    {
        using var client = ClienteConResolutor(ResolverA("93.184.216.34", "10.1.2.3"), _ => new HttpResponseMessage(HttpStatusCode.OK));

        using var respuesta = await client.GetAsync("https://mixto.example.com/archivo");

        respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SendAsync_ConRedireccionQueApuntaALaRedLocal_DeberiaBloquearElSalto()
    {
        // El origen resuelve a IP pública, pero el 302 lleva a un host que resuelve a una IP privada
        Func<string, CancellationToken, Task<IPAddress[]>> resolutor = (host, _) =>
            Task.FromResult(new[] { host.StartsWith("interno") ? IPAddress.Parse("192.168.0.10") : IpPublica });
        using var client = ClienteConResolutor(resolutor,
            _ => Redireccion("https://interno.example.com/admin"),
            _ => new HttpResponseMessage(HttpStatusCode.OK));

        using var respuesta = await client.GetAsync("https://origen.example.com/archivo");

        respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SendAsync_ConIpLiteralPrivada_DeberiaBloquearSinResolverDns()
    {
        bool resolvio = false;
        using var client = ClienteConResolutor((_, _) => { resolvio = true; return Task.FromResult(new[] { IpPublica }); },
            _ => new HttpResponseMessage(HttpStatusCode.OK));

        using var respuesta = await client.GetAsync("https://192.168.1.10/video.mp4");

        respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        resolvio.Should().BeFalse();
    }

    [Fact]
    public async Task SendAsync_SiElDnsFalla_NoDeberiaBloquearAqui_ElErrorLlegaAlConectar()
    {
        using var client = ClienteConResolutor((_, _) => throw new System.Net.Sockets.SocketException(11001),
            _ => new HttpResponseMessage(HttpStatusCode.OK));

        using var respuesta = await client.GetAsync("https://sin-dns.example.com/archivo");

        respuesta.StatusCode.Should().Be(HttpStatusCode.OK);
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
