using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AnimeLocalTracker.Models;

// === DTOs de deserialización de la API pública de AnimeThemes.moe (api.animethemes.moe) ===

public sealed class AnimeThemesResourceResponse
{
    [JsonPropertyName("resources")]
    public List<AnimeThemesResourceItemDto> Resource { get; set; } = new();
}

public sealed class AnimeThemesResourceItemDto
{
    [JsonPropertyName("anime")]
    public List<AnimeThemesAnimeDto> Anime { get; set; } = new();
}

public sealed class AnimeThemesAnimeResponse
{
    [JsonPropertyName("anime")]
    public AnimeThemesAnimeDto? Anime { get; set; }
}

public sealed class AnimeThemesAnimeDto
{
    [JsonPropertyName("slug")]
    public string Slug { get; set; } = string.Empty;

    [JsonPropertyName("animethemes")]
    public List<AnimeThemeDto> AnimeThemes { get; set; } = new();
}

public sealed class AnimeThemeDto
{
    [JsonPropertyName("slug")]
    public string Slug { get; set; } = string.Empty; // "OP1", "ED1-TV"

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty; // "OP" / "ED"

    [JsonPropertyName("song")]
    public AnimeThemeSongDto? Song { get; set; }

    [JsonPropertyName("animethemeentries")]
    public List<AnimeThemeEntryDto> AnimeThemeEntries { get; set; } = new();
}

public sealed class AnimeThemeSongDto
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("artists")]
    public List<AnimeThemeArtistDto> Artists { get; set; } = new();
}

public sealed class AnimeThemeArtistDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

public sealed class AnimeThemeEntryDto
{
    [JsonPropertyName("version")]
    public int? Version { get; set; }

    [JsonPropertyName("episodes")]
    public string? Episodes { get; set; }

    [JsonPropertyName("spoiler")]
    public bool Spoiler { get; set; }

    [JsonPropertyName("videos")]
    public List<AnimeThemeVideoDto> Videos { get; set; } = new();
}

public sealed class AnimeThemeVideoDto
{
    [JsonPropertyName("basename")]
    public string? Basename { get; set; }

    [JsonPropertyName("audio")]
    public AnimeThemeAudioDto? Audio { get; set; }
}

public sealed class AnimeThemeAudioDto
{
    [JsonPropertyName("basename")]
    public string Basename { get; set; } = string.Empty;

    [JsonPropertyName("link")]
    public string Link { get; set; } = string.Empty;
}

// === Modelo de dominio, ya aplanado (una fila por versión/rango de episodios) ===

/// <summary>
/// Un opening/ending concreto de un anime (una versión de un AnimeTheme de AnimeThemes.moe),
/// con el link directo a su pista de audio (.ogg) — nunca al .webm de video.
/// </summary>
public sealed class AnimeThemeInfo
{
    public required string Slug { get; init; }        // "OP1", "ED1-TV"
    public required string Tipo { get; init; }        // "OP" / "ED"
    public int Version { get; init; } = 1;
    public string TituloCancion { get; init; } = string.Empty;
    public string Artistas { get; init; } = string.Empty;
    /// <summary>Rango de episodios donde aplica esta versión (p. ej. "1-16", "1-14, 16"). Null = todos.</summary>
    public string? RangoEpisodios { get; init; }
    public bool EsSpoiler { get; init; }
    public required string AudioUrlOgg { get; init; }

    public bool AplicaAlEpisodio(int episodio) => EpisodioEnRango(RangoEpisodios, episodio);

    /// <summary>
    /// Lógica pura de pertenencia episodio↔rango (ARQ-01): la reutilizan tanto <see cref="AnimeThemeInfo"/>
    /// (con datos frescos de la API) como el nombre de archivo local ya descargado (sin red ni API),
    /// para que SkipTimesCoordinator pueda decidir sin depender de ninguna de las dos fuentes en concreto.
    /// </summary>
    public static bool EpisodioEnRango(string? rango, int episodio)
    {
        if (string.IsNullOrWhiteSpace(rango)) return true;

        foreach (var parte in rango.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var limites = parte.Split('-', StringSplitOptions.TrimEntries);
            if (limites.Length == 1 && int.TryParse(limites[0], out int unico))
            {
                if (episodio == unico) return true;
            }
            else if (limites.Length == 2 && int.TryParse(limites[0], out int desde) && int.TryParse(limites[1], out int hasta))
            {
                if (episodio >= desde && episodio <= hasta) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Nombre de archivo local determinista: codifica tipo+slug+versión+rango en el propio nombre
    /// para que el mp3 ya descargado se pueda usar como fuente de saltos de OP/ED sin base de datos
    /// ni llamada de red — basta con leer el nombre del archivo (ver AnimeThemesDownloadService).
    /// </summary>
    public string NombreArchivoLocal()
    {
        string rango = string.IsNullOrWhiteSpace(RangoEpisodios)
            ? "todos"
            : RangoEpisodios.Replace(" ", "").Replace(",", "_");
        return $"{Tipo}_{Slug}_v{Version}_ep{rango}.mp3";
    }
}

/// <summary>Un tema de AnimeThemes ya descargado y convertido a mp3, reconstruido a partir del
/// nombre de su archivo local (ver <see cref="AnimeThemeInfo.NombreArchivoLocal"/>).</summary>
public sealed record TemaLocalDisponible(string Tipo, string Slug, int Version, string? RangoEpisodios, string RutaArchivo)
{
    public bool AplicaAlEpisodio(int episodio) => AnimeThemeInfo.EpisodioEnRango(RangoEpisodios, episodio);
}
