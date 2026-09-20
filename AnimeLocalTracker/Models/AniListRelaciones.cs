using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AnimeLocalTracker.Models;

/// <summary>Respuesta de la consulta de relaciones (solo lo que hace falta para agrupar franquicias).</summary>
internal sealed class AniListRelacionesRespuesta
{
    [JsonPropertyName("data")]
    public AniListRelacionesData? Data { get; set; }
}

internal sealed class AniListRelacionesData
{
    [JsonPropertyName("Page")]
    public AniListRelacionesPagina? Page { get; set; }
}

internal sealed class AniListRelacionesPagina
{
    [JsonPropertyName("media")]
    public List<AniListRelacionesMedia>? Media { get; set; }
}

internal sealed class AniListRelacionesMedia
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("relations")]
    public AniListRelacionesConexion? Relations { get; set; }
}

internal sealed class AniListRelacionesConexion
{
    [JsonPropertyName("edges")]
    public List<AniListRelacionesArista>? Edges { get; set; }
}

internal sealed class AniListRelacionesArista
{
    [JsonPropertyName("relationType")]
    public string? RelationType { get; set; }

    [JsonPropertyName("node")]
    public AniListRelacionesNodo? Node { get; set; }
}

internal sealed class AniListRelacionesNodo
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>ANIME o MANGA: solo interesan los animes (las adaptaciones de manga/novela no cuentan).</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }
}
