using System.Collections.Generic;
using System.Windows.Media;

namespace AnimeLocalTracker.Models;

/// <summary>Datos ya localizados/agregados para renderizar AnimeWrappedCardView fuera de pantalla.</summary>
public class WrappedCardData
{
    public string TituloCard { get; init; } = "";
    public string NombreUsuario { get; init; } = "";
    public ImageSource? Avatar { get; init; }
    public string HorasVistasTexto { get; init; } = "";
    public string HorasLabel { get; init; } = "";
    public string EpisodiosVistosTexto { get; init; } = "";
    public string EpisodiosLabel { get; init; } = "";
    public string GeneroFavorito { get; init; } = "";
    public string GeneroLabel { get; init; } = "";
    public string RachaMaximaTexto { get; init; } = "";
    public string RachaLabel { get; init; } = "";
    public string TopAnimesLabel { get; init; } = "";
    public List<string> TopAnimesTitulos { get; init; } = new();
    public string Footer { get; init; } = "";
}
