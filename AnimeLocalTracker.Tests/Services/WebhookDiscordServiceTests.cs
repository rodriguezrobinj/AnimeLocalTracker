using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Webhook;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

public sealed class WebhookDiscordServiceTests : IDisposable
{
    private const string Url = "https://discord.com/api/webhooks/123/secreto";
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), "webhook-servicio-" + Guid.NewGuid().ToString("N"));
    private readonly AppSettings _config = new();
    private readonly ManejadorFalso _manejador = new();

    public void Dispose()
    {
        try { Directory.Delete(_carpeta, recursive: true); } catch (IOException) { }
    }

    private WebhookDiscordService Servicio(string? urlGuardada = Url)
    {
        var ajustes = new Mock<ISettingsService>();
        ajustes.Setup(a => a.ObtenerConfiguracion()).Returns(_config);
        var fabrica = new Mock<IHttpClientFactory>();
        fabrica.Setup(f => f.CreateClient("Webhook")).Returns(() => new HttpClient(_manejador, disposeHandler: false));
        var almacen = new AlmacenWebhook(Path.Combine(_carpeta, "discord-webhook.bin"));
        almacen.Guardar(urlGuardada);
        return new WebhookDiscordService(ajustes.Object, fabrica.Object, almacen);
    }

    [Fact]
    public async Task EpisodioEmitido_ConElAvisoActivado_PublicaLaTarjeta()
    {
        var servicio = Servicio();

        servicio.Receive(new EpisodioEmitidoMensaje(154587, "Frieren", null, 15, 15));
        await servicio.EnvioEnCurso;

        _manejador.Peticiones.Should().ContainSingle();
        var (metodo, url, cuerpo) = _manejador.Peticiones[0];
        metodo.Should().Be(HttpMethod.Post);
        url.Should().Be(Url);
        cuerpo.Should().Contain("Frieren").And.Contain("https://anilist.co/anime/154587");
    }

    [Fact]
    public async Task SerieTerminada_ConElAvisoActivado_PublicaLaTarjeta()
    {
        var servicio = Servicio();

        servicio.Receive(new SerieTerminadaMensaje(154587, "Frieren", null, 28));
        await servicio.EnvioEnCurso;

        _manejador.Peticiones.Should().ContainSingle().Which.Cuerpo.Should().Contain("Frieren");
    }

    [Fact]
    public async Task AvisoApagado_NoPublicaNada()
    {
        _config.WebhookAvisarEmision = false;
        _config.WebhookAvisarSerieTerminada = false;
        var servicio = Servicio();

        servicio.Receive(new EpisodioEmitidoMensaje(1, "Frieren", null, 15, 15));
        servicio.Receive(new SerieTerminadaMensaje(1, "Frieren", null, 28));
        await servicio.EnvioEnCurso;

        _manejador.Peticiones.Should().BeEmpty();
    }

    [Fact]
    public async Task SinUrlGuardada_NoPublicaNada()
    {
        var servicio = Servicio(urlGuardada: null);

        servicio.Receive(new EpisodioEmitidoMensaje(1, "Frieren", null, 15, 15));
        await servicio.EnvioEnCurso;

        _manejador.Peticiones.Should().BeEmpty();
    }

    [Fact]
    public async Task Probar_ErrorDeDiscord_DevuelveFalseSinLanzar()
    {
        _manejador.Respuesta = HttpStatusCode.NotFound;

        (await Servicio().ProbarAsync(Url)).Should().BeFalse();
    }

    [Fact]
    public async Task Probar_SinConexion_DevuelveFalseSinLanzar()
    {
        _manejador.Excepcion = new HttpRequestException("Sin conexión a internet.");

        (await Servicio().ProbarAsync(Url)).Should().BeFalse();
    }

    [Fact]
    public async Task Probar_UrlQueNoEsDeDiscord_NoEnvia()
    {
        (await Servicio().ProbarAsync("https://evil.net/api/webhooks/1/x")).Should().BeFalse();

        _manejador.Peticiones.Should().BeEmpty();
    }

    [Fact]
    public async Task Probar_Correcto_DevuelveTrue()
        => (await Servicio().ProbarAsync(Url)).Should().BeTrue();

    [Fact]
    public void GuardarUrl_YLeerla()
    {
        var servicio = Servicio(urlGuardada: null);

        servicio.GuardarUrl(Url);

        servicio.UrlGuardada.Should().Be(Url);
    }

    private sealed class ManejadorFalso : HttpMessageHandler
    {
        public List<(HttpMethod Metodo, string Url, string Cuerpo)> Peticiones { get; } = new();
        public HttpStatusCode Respuesta { get; set; } = HttpStatusCode.NoContent;
        public Exception? Excepcion { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Excepcion != null) throw Excepcion;
            Peticiones.Add((request.Method, request.RequestUri!.ToString(), await request.Content!.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(Respuesta);
        }
    }
}
