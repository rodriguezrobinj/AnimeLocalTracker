using System.Collections.Generic;
using System.Windows.Media;

namespace AnimeLocalTracker.Models;

/// <summary>Un puesto del Top 3 ya con su portada cargada (o null si no se pudo resolver/descargar).</summary>
public sealed class WrappedTopAnimeItem
{
    public int Posicion { get; init; }
    public string Titulo { get; init; } = "";
    public ImageSource? Portada { get; init; }
}

/// <summary>Datos ya localizados/agregados para renderizar AnimeWrappedCardView fuera de pantalla.</summary>
public class WrappedCardData
{
    public string TituloCard { get; init; } = "";
    public string NombreUsuario { get; init; } = "";
    public ImageSource? Avatar { get; init; }
    /// <summary>Rango de Logros ya localizado (p. ej. "Leyenda") — mismo texto que en la pestaña
    /// Estadísticas, reutilizado aquí como sello/badge para que la tarjeta se sienta "ganada".</summary>
    public string RangoOtakuTexto { get; init; } = "";
    public string HorasVistasTexto { get; init; } = "";
    public string HorasLabel { get; init; } = "";
    public string EpisodiosVistosTexto { get; init; } = "";
    public string EpisodiosLabel { get; init; } = "";
    public string GeneroFavorito { get; init; } = "";
    public string GeneroLabel { get; init; } = "";
    public string RachaMaximaTexto { get; init; } = "";
    public string RachaLabel { get; init; } = "";
    public string TopAnimesLabel { get; init; } = "";
    public List<WrappedTopAnimeItem> TopAnimesItems { get; init; } = new();
    public string Footer { get; init; } = "";
}
