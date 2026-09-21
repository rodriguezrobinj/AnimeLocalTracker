using System.Linq;
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
    
    // NUEVO: Para recibir el calendario de emisiones
    [JsonPropertyName("airingSchedules")]
    public List<AiringScheduleNode>? AiringSchedules { get; set; }

    [JsonPropertyName("pageInfo")]
    public AniListPageInfo? PageInfo { get; set; }
}

public class AniListPageInfo
{
    [JsonPropertyName("hasNextPage")]
    public bool HasNextPage { get; set; }
}

public class AiringScheduleNode
{
    [JsonPropertyName("episode")]
    public int Episode { get; set; }

    [JsonPropertyName("airingAt")]
    public long AiringAt { get; set; }

    [JsonPropertyName("media")]
    public AniListMedia? Media { get; set; }
}

public class AniListMedia
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("idMal")]
    public int? IdMal { get; set; }

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
    
    // NUEVO: Lista de títulos sinónimos/alternativos
    [JsonPropertyName("synonyms")]
    public List<string> Synonyms { get; set; } = new();

    // NUEVO: Total de episodios que tiene el anime
    [JsonPropertyName("episodes")]
    public int? Episodes { get; set; }

    // NUEVO: Capturamos el calendario de emisión
    [JsonPropertyName("nextAiringEpisode")]
    public AniListNextAiringEpisode? NextAiringEpisode { get; set; }
    
    [JsonPropertyName("startDate")]
    public AniListFuzzyDate? StartDate { get; set; }

    // NUEVO: temporada de estreno (WINTER/SPRING/SUMMER/FALL) para filtrar por temporada/año
    [JsonPropertyName("season")]
    public string? Season { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }
    
    // Datos para las etiquetas de la ficha (solo se piden en la consulta de datos extra)
    [JsonPropertyName("averageScore")]
    public int? AverageScore { get; set; }

    [JsonPropertyName("format")]
    public string? Format { get; set; }

    [JsonPropertyName("duration")]
    public int? Duration { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("studios")]
    public AniListStudios? Studios { get; set; }

    [JsonPropertyName("trailer")]
    public AniListTrailer? Trailer { get; set; }

    // NUEVO: La entrada personal del usuario autenticado
    [JsonPropertyName("mediaListEntry")]
    public AniListMediaList? MediaListEntry { get; set; }

    // ====== PROPIEDADES DE APOYO PARA LA UI (BÚSQUEDA) ======
    
    [JsonIgnore]
    public string FormattedStatus => Status switch
    {
        "RELEASING" => AnimeLocalTracker.Services.LocalizationService.T("Media_EstadoEnEmision"),
        "FINISHED" => AnimeLocalTracker.Services.LocalizationService.T("Media_EstadoFinalizado"),
        "NOT_YET_RELEASED" => AnimeLocalTracker.Services.LocalizationService.T("Media_EstadoProximamente"),
        "CANCELLED" => AnimeLocalTracker.Services.LocalizationService.T("Media_EstadoCancelado"),
        "HIATUS" => AnimeLocalTracker.Services.LocalizationService.T("Media_EstadoPausado"),
        _ => AnimeLocalTracker.Services.LocalizationService.T("Media_EstadoDesconocido")
    };

    [JsonIgnore]
    public string StatusColorBrush => Status switch
    {
        "RELEASING" => "#4CAF50", // Verde
        "FINISHED" => "#2196F3",  // Azul
        "NOT_YET_RELEASED" => "#FF9800", // Naranja
        "CANCELLED" => "#F44336", // Rojo
        "HIATUS" => "#9C27B0", // Morado
        _ => "#757575"
    };

    [JsonIgnore]
    public string FormattedEpisodes => Episodes.HasValue
        ? string.Format(AnimeLocalTracker.Services.LocalizationService.T("Det_EpisodiosFormato"), Episodes)
        : AnimeLocalTracker.Services.LocalizationService.T("Media_EpisodiosDesconocido");

    [JsonIgnore]
    public string FormattedYear => StartDate?.Year?.ToString() ?? AnimeLocalTracker.Services.LocalizationService.T("Media_AnioDesconocido");

    [JsonIgnore]
    public string FormattedSeason => Season switch
    {
        "WINTER" => AnimeLocalTracker.Services.LocalizationService.T("Temporada_Invierno"),
        "SPRING" => AnimeLocalTracker.Services.LocalizationService.T("Temporada_Primavera"),
        "SUMMER" => AnimeLocalTracker.Services.LocalizationService.T("Temporada_Verano"),
        "FALL" => AnimeLocalTracker.Services.LocalizationService.T("Temporada_Otonio"),
        _ => string.Empty
    };

    [JsonIgnore]
    public string FormattedGenres => Genres != null && Genres.Count > 0
        ? string.Join(" • ", Genres.Select(AnimeLocalTracker.Services.LocalizationService.TraducirGenero))
        : AnimeLocalTracker.Services.LocalizationService.T("Media_SinGeneros");
}

public class AniListTitle
{
    [JsonPropertyName("romaji")]
    public string Romaji { get; set; } = string.Empty;

    [JsonPropertyName("english")]
    public string? English { get; set; }

    [JsonPropertyName("native")]
    public string? Native { get; set; }

    [JsonPropertyName("userPreferred")]
    public string? UserPreferred { get; set; }
}

public class AniListCoverImage
{
    [JsonPropertyName("large")]
    public string? Large { get; set; }

    [JsonPropertyName("extraLarge")]
    public string? ExtraLarge { get; set; }
}

public class AniListStudios
{
    [JsonPropertyName("nodes")]
    public List<AniListStudio>? Nodes { get; set; }
}

public class AniListStudio
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public class AniListTrailer
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("site")]
    public string? Site { get; set; }
}

public class AniListNextAiringEpisode
{
    [JsonPropertyName("episode")]
    public int Episode { get; set; }

    /// <summary>Momento de emisión (segundos Unix UTC). 0 si la consulta no lo pidió.</summary>
    [JsonPropertyName("airingAt")]
    public long AiringAt { get; set; }
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