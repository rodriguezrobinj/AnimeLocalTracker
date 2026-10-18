using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using AnimeLocalTracker.Messages;
using CommunityToolkit.Mvvm.Messaging;

namespace AnimeLocalTracker.Services.Webhook;

/// <summary>
/// Escucha los avisos de la app y los publica en el canal de Discord del usuario. Los mensajes pueden llegar desde hilos de
/// fondo: aquí no se toca la interfaz. Sin conexión o con Discord caído se registra y se descarta (sin cola). La URL nunca va
/// al registro: es secreta.
/// </summary>
public sealed class WebhookDiscordService : IWebhookDiscordService, IRecipient<EpisodioEmitidoMensaje>, IRecipient<SerieTerminadaMensaje>
{
    private const string Origen = "Webhook";
    internal const string NombreCliente = "Webhook";

    private readonly ISettingsService _ajustes;
    private readonly IHttpClientFactory _http;
    private readonly AlmacenWebhook _almacen;

    public WebhookDiscordService(ISettingsService ajustes, IHttpClientFactory http) : this(ajustes, http, new AlmacenWebhook()) { }

    internal WebhookDiscordService(ISettingsService ajustes, IHttpClientFactory http, AlmacenWebhook almacen)
    {
        _ajustes = ajustes;
        _http = http;
        _almacen = almacen;
    }

    /// <summary>El último envío lanzado por un aviso (para que las pruebas puedan esperarlo).</summary>
    internal Task EnvioEnCurso { get; private set; } = Task.CompletedTask;

    public string? UrlGuardada => _almacen.Leer();

    public void GuardarUrl(string? url) => _almacen.Guardar(url);

    public void Iniciar()
    {
        WeakReferenceMessenger.Default.Register<EpisodioEmitidoMensaje>(this);
        WeakReferenceMessenger.Default.Register<SerieTerminadaMensaje>(this);
    }

    public void Receive(EpisodioEmitidoMensaje message)
    {
        if (_ajustes.ObtenerConfiguracion()?.WebhookAvisarEmision != true) return;
        Publicar(EmbedWebhook.EpisodioEmitido(message.AnimeId, message.Titulo, message.UrlPortada, message.Desde, message.Hasta, DateTimeOffset.UtcNow));
    }

    public void Receive(SerieTerminadaMensaje message)
    {
        if (_ajustes.ObtenerConfiguracion()?.WebhookAvisarSerieTerminada != true) return;
        Publicar(EmbedWebhook.SerieTerminada(message.AnimeId, message.Titulo, message.UrlPortada, message.TotalEpisodios, DateTimeOffset.UtcNow));
    }

    public Task<bool> ProbarAsync(string url)
        => EmbedWebhook.EsUrlValida(url) ? EnviarAsync(url.Trim(), EmbedWebhook.Prueba(DateTimeOffset.UtcNow)) : Task.FromResult(false);

    private void Publicar(string json)
    {
        string? url = _almacen.Leer();
        if (!EmbedWebhook.EsUrlValida(url)) return;
        EnvioEnCurso = EnviarAsync(url!, json);
    }

    private async Task<bool> EnviarAsync(string url, string json)
    {
        try
        {
            using var cliente = _http.CreateClient(NombreCliente);
            using var contenido = new StringContent(json, Encoding.UTF8, "application/json");
            using var respuesta = await cliente.PostAsync(url, contenido).ConfigureAwait(false);
            if (respuesta.IsSuccessStatusCode) return true;

            AppLogger.Warn(Origen, $"Discord no aceptó el aviso ({(int)respuesta.StatusCode} {respuesta.ReasonPhrase}).");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            AppLogger.Warn(Origen, $"No se pudo enviar el aviso a Discord: {ex.Message}");
        }
        return false;
    }
}
