using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AnimeLocalTracker.Models;

public partial class AnimeBusquedaItem : ObservableObject
{
    public AniListMedia Media { get; set; } = null!;

    [ObservableProperty]
    private bool _estaEnBiblioteca;

    [ObservableProperty]
    private bool _estaGuardando;

    public string TituloPrincipal => 
        Media?.Title?.Romaji ?? Media?.Title?.UserPreferred ?? Media?.Title?.English ?? "Sin título";

    public string TituloSecundario => 
        !string.IsNullOrEmpty(Media?.Title?.English) && Media.Title.English != TituloPrincipal 
            ? Media.Title.English 
            : (Media?.Title?.Native ?? "");

    public string GenerosTexto => 
        Media?.Genres != null && Media.Genres.Count > 0 
            ? string.Join(" • ", Media.Genres.Take(3)) 
            : "";

    public string EpisodiosTexto => 
        Media?.Episodes != null 
            ? $"{Media.Episodes} eps" 
            : (Media?.NextAiringEpisode != null ? $"Ep {Media.NextAiringEpisode.Episode - 1}+" : "Eps desc.");

    public string AñoTexto => Media?.StartDate?.Year?.ToString() ?? "";

    public bool TieneAño => Media?.StartDate?.Year != null;

    public string EstadoTexto => Media?.FormattedStatus ?? "Desconocido";

    public string EstadoColor => Media?.StatusColorBrush ?? "#2196F3";

    public string PortadaUrl => Media?.CoverImage?.ExtraLarge ?? string.Empty;
}
