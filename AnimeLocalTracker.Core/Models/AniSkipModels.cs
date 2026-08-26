using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AnimeLocalTracker.Core.Models;

public class AniSkipResponse
{
    [JsonPropertyName("found")]
    public bool Found { get; set; }

    [JsonPropertyName("results")]
    public List<AniSkipResult> Results { get; set; } = new();

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("statusCode")]
    public int StatusCode { get; set; }
}

public class AniSkipResult
{
    [JsonPropertyName("interval")]
    public AniSkipInterval Interval { get; set; } = new();

    [JsonPropertyName("skipType")]
    public string SkipType { get; set; } = string.Empty; // "op", "ed", "mixed-op", "mixed-ed", "recap"

    [JsonPropertyName("skipId")]
    public string? SkipId { get; set; }

    [JsonPropertyName("episodeLength")]
    public double EpisodeLength { get; set; }

    [JsonIgnore]
    public bool EsIntro => SkipType == "op" || SkipType == "mixed-op";

    [JsonIgnore]
    public bool EsEnding => SkipType == "ed" || SkipType == "mixed-ed";

    [JsonIgnore]
    public bool EsRecap => SkipType == "recap";

    [JsonIgnore]
    public string TextoBoton => SkipType switch
    {
        "op" or "mixed-op" => "Saltar intro",
        "ed" or "mixed-ed" => "Saltar ending",
        "recap" => "Saltar resumen",
        _ => "Saltar segmento"
    };

    [JsonIgnore]
    public string IconoBoton => SkipType switch
    {
        "ed" or "mixed-ed" => "SkipNext",
        "recap" => "FastForward",
        _ => "FastForward"
    };
}

public class AniSkipInterval
{
    [JsonPropertyName("startTime")]
    public double StartTime { get; set; }

    [JsonPropertyName("endTime")]
    public double EndTime { get; set; }
}
