using System;
using System.Text.Json.Nodes;

namespace AnimeLocalTracker.Services.Webhook;

/// <summary>
/// El JSON que se manda al webhook de Discord: una tarjeta (embed) con título, descripción, color, portada y enlace a AniList.
/// Se construye con <see cref="JsonObject"/>: comillas, barras o emojis del título no rompen el JSON.
/// </summary>
internal static class EmbedWebhook
{
    private const int Ambar = 0xF59E0B;
    private const int Verde = 0x34D399;
    private const int Azul = 0x3B82F6;
    private const int MaxTitulo = 256; // límite de Discord para el título de un embed

    public static string EpisodioEmitido(int animeId, string titulo, string? portada, int desde, int hasta, DateTimeOffset ahora)
    {
        string episodios = desde == hasta
            ? string.Format(LocalizationService.T("Act_EpisodioFormato"), hasta)
            : string.Format(LocalizationService.T("Webhook_EpisodiosFormato"), desde, hasta);
        return Mensaje($"{titulo} · {episodios}", LocalizationService.T("Webhook_YaSeEmitio"), Ambar, animeId, portada, ahora);
    }

    public static string SerieTerminada(int animeId, string titulo, string? portada, int totalEpisodios, DateTimeOffset ahora)
        => Mensaje(string.Format(LocalizationService.T("Webhook_TerminasteFormato"), titulo),
            string.Format(LocalizationService.T("Webhook_EpisodiosTotalFormato"), totalEpisodios), Verde, animeId, portada, ahora);

    public static string Prueba(DateTimeOffset ahora)
        => Mensaje(LocalizationService.T("Webhook_PruebaTitulo"), LocalizationService.T("Webhook_PruebaTexto"), Azul, 0, null, ahora);

    /// <summary>
    /// Solo webhooks de Discord por https. La URL la pega el usuario y por ella salen títulos de su biblioteca: un dominio
    /// parecido ("discord.com.evil.net") no debe pasar.
    /// </summary>
    public static bool EsUrlValida(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo)) return false;

        bool dominio = uri.Host is "discord.com" or "discordapp.com" or "ptb.discord.com" or "canary.discord.com";
        const string ruta = "/api/webhooks/";
        return dominio && uri.AbsolutePath.StartsWith(ruta, StringComparison.Ordinal) && uri.AbsolutePath.Length > ruta.Length;
    }

    private static string Mensaje(string titulo, string descripcion, int color, int animeId, string? portada, DateTimeOffset ahora)
    {
        var embed = new JsonObject
        {
            ["title"] = titulo.Length > MaxTitulo ? titulo[..(MaxTitulo - 1)] + "…" : titulo,
            ["description"] = descripcion,
            ["color"] = color,
            ["timestamp"] = ahora.ToUniversalTime().ToString("o"),
        };
        if (animeId > 0) embed["url"] = $"https://anilist.co/anime/{animeId}";
        if (portada?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true) embed["thumbnail"] = new JsonObject { ["url"] = portada };

        return new JsonObject { ["username"] = "AnimeLocalTracker", ["embeds"] = new JsonArray(embed) }.ToJsonString();
    }
}
