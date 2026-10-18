using System;
using System.Text.Json.Nodes;

namespace AnimeLocalTracker.Services.Discord;

/// <summary>
/// El JSON de la actividad "Viendo" que se manda a Discord (comando SET_ACTIVITY del IPC local). Discord dibuja la barra de
/// tiempo a partir de <c>timestamps</c>; en pausa se quitan para que no siga avanzando.
/// </summary>
internal static class ActividadDiscord
{
    /// <summary>Nombre del recurso de imagen subido en el Developer Portal: portada de reserva.</summary>
    internal const string ImagenReserva = "logo";

    public static JsonObject Construir(EstadoPresencia e, string? urlPortada, DateTimeOffset ahora)
    {
        string episodio = string.Format(LocalizationService.T("Act_EpisodioFormato"), e.Episodio);
        string estado = e.Reproduciendo ? episodio : string.Format(LocalizationService.T("Discord_EnPausaFormato"), episodio);
        bool portadaValida = urlPortada?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true;

        var actividad = new JsonObject
        {
            ["type"] = 3, // Viendo
            ["details"] = Ajustar(e.Titulo, 128),
            ["state"] = Ajustar(estado, 128),
            ["assets"] = new JsonObject
            {
                ["large_image"] = portadaValida ? urlPortada : ImagenReserva,
                ["large_text"] = Ajustar(e.Titulo, 128),
            },
        };

        if (e.Reproduciendo)
        {
            long inicio = ahora.ToUnixTimeSeconds() - (long)e.PosicionSeg;
            var tiempos = new JsonObject { ["start"] = inicio };
            if (e.DuracionSeg > 0) tiempos["end"] = inicio + (long)e.DuracionSeg;
            actividad["timestamps"] = tiempos;
        }

        if (e.AnimeId > 0)
        {
            actividad["buttons"] = new JsonArray(new JsonObject
            {
                ["label"] = Ajustar(LocalizationService.T("Discord_VerEnAniList"), 32),
                ["url"] = $"https://anilist.co/anime/{e.AnimeId}",
            });
        }

        return actividad;
    }

    /// <summary>Comando completo; <paramref name="actividad"/> null borra el estado.</summary>
    public static string ComandoSetActivity(JsonObject? actividad, int pid) => new JsonObject
    {
        ["cmd"] = "SET_ACTIVITY",
        ["args"] = new JsonObject { ["pid"] = pid, ["activity"] = actividad },
        ["nonce"] = Guid.NewGuid().ToString(),
    }.ToJsonString();

    /// <summary>Discord rechaza textos de menos de 2 caracteres o de más del límite del campo.</summary>
    private static string Ajustar(string texto, int maximo)
    {
        texto ??= string.Empty;
        if (texto.Length > maximo) return texto[..(maximo - 1)] + "…";
        // ponytail: relleno con el blanco braille (U+2800), el habitual en Rich Presence; sin verificar con un título real de 1 letra.
        return texto.Length < 2 ? texto.PadRight(2, '⠀') : texto;
    }
}
