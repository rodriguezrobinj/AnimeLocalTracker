using System.Text.Json.Serialization;

namespace AnimeLocalTracker.Models;

// Estas clases representan exactamente la estructura JSON que devuelve AniList
public class AniListResponse
{
    [JsonPropertyName("data")]
    public AniListData Data { get; set; } = new();
}

public class AniListData
{
    // Esta se usa para las búsquedas generales (devuelve varios animes)
    [JsonPropertyName("Page")]
    public AniListPage? Page { get; set; }

    // NUEVO: Esta se usa para la búsqueda exacta por ID (devuelve solo un anime)
    [JsonPropertyName("Media")]
    public AniListMedia? Media { get; set; }
    
    // NUEVO: Para recibir tu progreso personal desde la nube
    [JsonPropertyName("MediaList")]
    public AniListMediaList? MediaList { get; set; }
    
    // NUEVO: Para recibir tu perfil público
    [JsonPropertyName("Viewer")]
    public AniListUser? Viewer { get; set; }
}

public class AniListPage
{
    //[JsonPropertyName("media")]
    //public List<AniListMedia> Media { get; set; } = new();
    
    [JsonPropertyName("media")]
    public List<AniListMedia>? Media { get; set; }
}

public class AniListMedia
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("title")]
    public AniListTitle Title { get; set; } = new();

    [JsonPropertyName("coverImage")]
    public AniListCoverImage CoverImage { get; set; } = new();

    // NUEVO: Para recibir la sinopsis (asHtml: false)
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    // NUEVO: Para recibir los géneros como una lista de textos
    [JsonPropertyName("genres")]
    public List<string> Genres { get; set; } = new();
    
    // NUEVO: Total de episodios que tiene el anime
    [JsonPropertyName("episodes")]
    public int? Episodes { get; set; }

    // NUEVO: Capturamos el calendario de emisión
    [JsonPropertyName("nextAiringEpisode")]
    public AniListNextAiringEpisode? NextAiringEpisode { get; set; }
    
    [JsonPropertyName("status")]
    public string? Status { get; set; }
    
    // NUEVO: La entrada personal del usuario autenticado
    [JsonPropertyName("mediaListEntry")]
    public AniListMediaList? MediaListEntry { get; set; }
}

public class AniListTitle
{
    [JsonPropertyName("romaji")]
    public string Romaji { get; set; } = string.Empty;
}

public class AniListCoverImage
{
    [JsonPropertyName("large")]
    public string? Large { get; set; }

    [JsonPropertyName("extraLarge")]
    public string? ExtraLarge { get; set; }
}

public class AniListNextAiringEpisode
{
    [JsonPropertyName("episode")]
    public int Episode { get; set; }
}

public class AniListFuzzyDate
{
    [JsonPropertyName("year")]
    public int? Year { get; set; }

    [JsonPropertyName("month")]
    public int? Month { get; set; }

    [JsonPropertyName("day")]
    public int? Day { get; set; }
}

public class AniListMediaList
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("score")]
    public float Score { get; set; }

    [JsonPropertyName("progress")]
    public int Progress { get; set; }

    [JsonPropertyName("startedAt")]
    public AniListFuzzyDate? StartedAt { get; set; }

    [JsonPropertyName("completedAt")]
    public AniListFuzzyDate? CompletedAt { get; set; }
}

public class AniListAvatar
{
    [JsonPropertyName("large")]
    public string? Large { get; set; }
}

public class AniListUser
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("avatar")]
    public AniListAvatar? Avatar { get; set; }
}