namespace AnimeLocalTracker.Services.Discord;

/// <summary>Estado "Viendo" del perfil de Discord mientras se reproduce un episodio (Ajustes → DiscordRichPresence).</summary>
public interface IDiscordPresenceService
{
    /// <summary>Lo llama el reproductor en cada tic: solo guarda el estado y vuelve; el envío a Discord va en segundo plano.</summary>
    void Actualizar(EstadoPresencia estado);

    /// <summary>Borra el estado (se cerró el reproductor).</summary>
    void Limpiar();

    /// <summary>Tecla de pánico: borra el estado y no lo vuelve a publicar hasta que se abra otro episodio.</summary>
    void Suprimir();
}
