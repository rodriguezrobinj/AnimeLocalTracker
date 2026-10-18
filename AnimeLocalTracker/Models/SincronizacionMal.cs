using System;
using SQLite;

namespace AnimeLocalTracker.Models;

/// <summary>
/// Lo último que la app dejó en la lista de MyAnimeList para un anime (o lo que MAL tenía cuando decidió no tocarlo). La
/// sincronización solo vuelve a escribir un anime si lo que quiere enviar es distinto de esto. Tabla creada por la migración v25;
/// desconectar MAL la vacía.
/// </summary>
public class SincronizacionMal
{
    [PrimaryKey]
    public int AniListId { get; set; }

    public int MalId { get; set; }

    /// <summary>Estado de MAL: watching, completed, on_hold, dropped, plan_to_watch.</summary>
    public string Estado { get; set; } = string.Empty;

    public int Progreso { get; set; }

    /// <summary>0-10; 0 = sin nota.</summary>
    public int Nota { get; set; }

    /// <summary>UTC (sqlite-net la devuelve con Kind Unspecified: tratarla como UTC).</summary>
    public DateTime EnviadoUtc { get; set; }
}
