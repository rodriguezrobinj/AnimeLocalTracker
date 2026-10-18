using System;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services.Mal;

/// <summary>Lo que se envía (o se lee) de la lista de MAL para un anime: estado de MAL, episodios vistos y nota 0-10 (0 = sin nota).</summary>
public sealed record EstadoMal(string Estado, int Progreso, int Nota);

/// <summary>El estado local de un anime traducido a MAL (ver docs/plan-sincronizacion-mal.md).</summary>
internal static class MapeoMal
{
    /// <summary>Estado de MAL; null = no se envía (sin estado y sin nada visto: no se añade a la lista de MAL).</summary>
    public static string? Estado(string? estadoAniList, int progreso) => estadoAniList switch
    {
        "CURRENT" or "REPEATING" => "watching",
        "COMPLETED" => "completed",
        "PAUSED" => "on_hold",
        "DROPPED" => "dropped",
        "PLANNING" => "plan_to_watch",
        _ => progreso > 0 ? "watching" : null,
    };

    /// <summary>La app puntúa de 0 a 100; MAL, de 0 a 10 (85 → 9).</summary>
    public static int Nota(float puntaje0a100)
        => Math.Clamp((int)Math.Round(puntaje0a100 / 10, MidpointRounding.AwayFromZero), 0, 10);

    /// <summary>El mayor entre lo visto en local y lo puesto en el editor, sin pasar del total si se conoce.</summary>
    public static int Progreso(int vistos, int progresoEditor, int total)
    {
        int progreso = Math.Max(vistos, progresoEditor);
        return total > 0 ? Math.Min(progreso, total) : progreso;
    }

    /// <summary>Lo que la app querría tener en MAL para este anime; null si no tiene MalId o no hay nada que enviar.</summary>
    public static EstadoMal? Deseado(AnimeItem anime, SeguimientoLocal? seguimiento, int vistos)
    {
        if (anime.MalId is not > 0) return null;

        int progreso = Progreso(vistos, seguimiento?.Progreso ?? 0, anime.TotalEpisodios);
        string? estado = Estado(seguimiento?.Estado ?? anime.EstadoUsuario, progreso);
        if (estado == null) return null;

        // Completado = todos vistos, aunque no estén en el disco (terminado en AniList, archivos borrados): con el progreso local
        // la regla de no bajar nada no dejaría marcarlo completado en MAL.
        if (estado == "completed" && anime.TotalEpisodios > 0) progreso = anime.TotalEpisodios;
        return new EstadoMal(estado, progreso, Nota(seguimiento?.Puntaje ?? 0));
    }
}

/// <summary>"Nunca se baja nada": decide qué escribir en MAL a partir de lo deseado y de lo que MAL ya tiene.</summary>
internal static class ReglaMal
{
    /// <summary>Lo que hay que enviar, o null para no tocar el anime en MAL.</summary>
    public static EstadoMal? QueEnviar(EstadoMal deseado, EstadoMal? enMal)
    {
        if (enMal == null) return deseado;
        if (enMal.Estado == "completed" && deseado.Estado != "completed") return null;
        if (enMal.Progreso > deseado.Progreso) return null;

        // Sin nota en la app no se borra la que el usuario puso en MAL.
        var envio = deseado.Nota == 0 ? deseado with { Nota = enMal.Nota } : deseado;
        return envio == enMal ? null : envio;
    }
}
