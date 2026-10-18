using System.Threading.Tasks;

namespace AnimeLocalTracker.Services.Webhook;

/// <summary>Avisos (episodio emitido, serie terminada) en un canal de Discord del usuario por webhook.</summary>
public interface IWebhookDiscordService
{
    /// <summary>Empieza a escuchar los avisos de la app (EpisodioEmitidoMensaje, SerieTerminadaMensaje).</summary>
    void Iniciar();

    /// <summary>La URL guardada (cifrada en disco), o null.</summary>
    string? UrlGuardada { get; }

    /// <summary>Guarda la URL cifrada; vacía o null la borra.</summary>
    void GuardarUrl(string? url);

    /// <summary>Manda una tarjeta de prueba. False si la URL no es de un webhook de Discord o Discord no la aceptó. No lanza.</summary>
    Task<bool> ProbarAsync(string url);
}
