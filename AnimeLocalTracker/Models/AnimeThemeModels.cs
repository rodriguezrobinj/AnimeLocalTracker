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

    [JsonPropertyName("name")]
    public string? Name { get; set; }

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

    // Forma antigua de la API. Desde sept-2026 "song.artists" responde HTTP 500: los artistas
    // vienen en "performances[].artist". Se conserva por si la API vuelve a exponerlo.
    [JsonPropertyName("artists")]
    public List<AnimeThemeArtistDto> Artists { get; set; } = new();

    [JsonPropertyName("performances")]
    public List<AnimeThemePerformanceDto> Performances { get; set; } = new();
}

public sealed class AnimeThemePerformanceDto
{
    [JsonPropertyName("artist")]
    public AnimeThemeArtistDto? Artist { get; set; }
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

    /// <summary>Aclaraciones de AnimeThemes sobre esta versión (p. ej. "OP as ED", "BD version").</summary>
    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

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

    [JsonPropertyName("size")]
    public long? Size { get; set; }
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

    /// <summary>Peso del .ogg en AnimeThemes (el mp3 convertido pesa casi lo mismo). Null si no se sabe.</summary>
    public long? TamanoBytes { get; init; }

    /// <summary>Aclaraciones de AnimeThemes sobre esta versión (p. ej. "OP as ED"). Null si no hay.</summary>
    public string? Notas { get; init; }

    /// <summary>Nombre del anime en AnimeThemes (se usa como "álbum" en las etiquetas del mp3). Null si no se sabe.</summary>
    public string? NombreAnime { get; init; }

    /// <summary>
    /// Primer episodio en que aparece según su rango (null = todos o no se sabe). Sirve para decidir si un tema marcado
    /// como spoiler corresponde a una parte del anime que el usuario aún no ha visto.
    /// </summary>
    public int? PrimerEpisodio()
    {
        if (string.IsNullOrWhiteSpace(RangoEpisodios)) return null;
        int? minimo = null;
        foreach (var parte in RangoEpisodios.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(parte.Split('-', StringSplitOptions.TrimEntries)[0], out int desde))
                minimo = minimo is { } m ? Math.Min(m, desde) : desde;
        }
        return minimo;
    }

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
            else if (limites.Length == 2 && int.TryParse(limites[0], out int desde))
            {
                // "1156-" = rango abierto: AnimeThemes lo usa mientras el anime sigue en emisión (aún no se sabe el último).
                if (limites[1].Length == 0)
                {
                    if (episodio >= desde) return true;
                }
                else if (int.TryParse(limites[1], out int hasta) && episodio >= desde && episodio <= hasta)
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Identidad del tema que NO cambia con el tiempo: tipo + slug + versión. El rango de episodios sí cambia (un anime en
    /// emisión pasa de "1-" a "1-12" al terminar), así que no sirve para reconocer un archivo ya descargado.
    /// </summary>
    public string ClaveEstable() => ClaveEstable(Tipo, Slug, Version);

    public static string ClaveEstable(string tipo, string slug, int version) =>
        $"{tipo.ToUpperInvariant()}|{slug.ToUpperInvariant()}|{version}";

    /// <summary>
    /// Nombre de archivo local determinista: codifica tipo+slug+versión+rango en el propio nombre
    /// para que el mp3 ya descargado se pueda usar como fuente de saltos de OP/ED sin base de datos
    /// ni llamada de red — basta con leer el nombre del archivo (ver AnimeThemesDownloadService).
    /// </summary>
    /// <summary>
    /// Nombre con el que se guarda el mp3 en la carpeta de música: legible fuera de la app ("OP1 - We Are!.mp3"; con "v2" si
    /// hay varias versiones). El slug y la versión van delante para poder reconocerlo sin base de datos (ver
    /// AnimeThemesDownloadService.TryParseNombreLegible); el rango NO va en el nombre (cambia con el tiempo: lo da el catálogo).
    /// </summary>
    public string NombreArchivoLegible()
    {
        string prefijo = Version > 1 ? $"{Slug} v{Version}" : Slug;
        string titulo = SanearParaArchivo(TituloCancion, 90);
        return (titulo.Length == 0 ? prefijo : $"{prefijo} - {titulo}") + ".mp3";
    }

    /// <summary>Quita lo que Windows no admite en un nombre de archivo ("Re:Zero" → "Re Zero") y lo recorta.</summary>
    internal static string SanearParaArchivo(string? texto, int maximo)
    {
        if (string.IsNullOrWhiteSpace(texto)) return string.Empty;

        var invalidos = System.IO.Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(texto.Length);
        foreach (char c in texto) sb.Append(Array.IndexOf(invalidos, c) >= 0 || char.IsControl(c) ? ' ' : c);

        string limpio = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        if (limpio.Length > maximo) limpio = limpio[..maximo].TrimEnd();
        return limpio.TrimEnd('.', ' ');
    }

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

    public string ClaveEstable() => AnimeThemeInfo.ClaveEstable(Tipo, Slug, Version);
}
