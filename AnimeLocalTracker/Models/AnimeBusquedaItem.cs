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
        !string.IsNullOrWhiteSpace(Media?.Title?.Romaji) ? Media!.Title!.Romaji :
        !string.IsNullOrWhiteSpace(Media?.Title?.UserPreferred) ? Media!.Title!.UserPreferred! :
        !string.IsNullOrWhiteSpace(Media?.Title?.English) ? Media!.Title!.English! :
        Services.LocalizationService.T("Media_TituloDesconocido");

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
            ? string.Format(Services.LocalizationService.T("Add_EpisodiosBadgeFormato"), Media.Episodes)
            : (Media?.NextAiringEpisode != null
                ? string.Format(Services.LocalizationService.T("Add_ProximoEpisodioBadge"), Media.NextAiringEpisode.Episode - 1)
                : Services.LocalizationService.T("Add_EpisodiosDesconocidoBadge"));

    public string AñoTexto => Media?.StartDate?.Year?.ToString() ?? "";

    public bool TieneAño => Media?.StartDate?.Year != null;

    public string TemporadaTexto => Media?.FormattedSeason ?? "";

    public bool TieneTemporada => !string.IsNullOrEmpty(TemporadaTexto);

    public string EstadoTexto => Media?.FormattedStatus ?? Services.LocalizationService.T("Media_EstadoDesconocido");

    public string EstadoColor => Media?.StatusColorBrush ?? "#2196F3";

    public string PortadaUrl => Media?.CoverImage?.ExtraLarge ?? string.Empty;
}
