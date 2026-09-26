using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace AnimeLocalTracker.Models;

/// <summary>Un puesto del Top 3 ya con su portada cargada y estadísticas individuales de visionado.</summary>
public sealed class WrappedTopAnimeItem
{
    public int Posicion { get; init; }
    public string Titulo { get; init; } = "";
    public ImageSource? Portada { get; init; }
    public int EpisodiosVistos { get; init; }
    public string DetalleTexto { get; init; } = "";
    public string TiempoTexto { get; init; } = "";
}

/// <summary>Un género del desglose del Top 3 de la tarjeta Wrapped.</summary>
public sealed class WrappedGeneroItem
{
    public string Nombre { get; init; } = "";
    public int Cantidad { get; init; }
    public int Porcentaje { get; init; }
    public string PorcentajeTexto => $"{Porcentaje}%";
    public string ColorHex { get; init; } = "#EC4899";
    public Brush ColorBrush => (Brush)new BrushConverter().ConvertFromString(ColorHex)!;
}

/// <summary>Datos ya localizados y enriquecidos para renderizar AnimeWrappedCardView con estética premium.</summary>
public class WrappedCardData
{
    public string TituloCard { get; init; } = "MI AÑO EN ANIME";
    public string AnioTexto { get; init; } = DateTime.Now.Year.ToString();
    public string NombreUsuario { get; init; } = "";
    public ImageSource? Avatar { get; init; }

    // Logros y Rango
    public string RangoOtakuTexto { get; init; } = "";
    public string PuntosLogrosTexto { get; init; } = "";

    // Arquetipo Otaku / Personalidad
    public string ArquetipoTitulo { get; init; } = "DEVORADOR DE SHONEN";
    public string ArquetipoDescripcion { get; init; } = "Adrenalina pura, superación personal y batallas épicas inolvidables.";

    // Métricas clave
    public string HorasVistasTexto { get; init; } = "";
    public string HorasLabel { get; init; } = "TIEMPO INVERTIDO";
    public string EpisodiosVistosTexto { get; init; } = "";
    public string EpisodiosLabel { get; init; } = "EPISODIOS VISTOS";
    public string GeneroFavorito { get; init; } = "";
    public string GeneroLabel { get; init; } = "GÉNERO PREDILECTO";
    public string RachaMaximaTexto { get; init; } = "";
    public string RachaActualTexto { get; init; } = "";
    public string RachaLabel { get; init; } = "RACHA MÁXIMA";

    // Top Géneros
    public List<WrappedGeneroItem> GenerosTop { get; init; } = new();
    public bool HasGeneros => GenerosTop.Count > 0;

    // Top Animes (Podio)
    public string TopAnimesLabel { get; init; } = "TU PODIO DE HONOR";
    public List<WrappedTopAnimeItem> TopAnimesItems { get; init; } = new();

    public WrappedTopAnimeItem? Top1 => TopAnimesItems.Count > 0 ? TopAnimesItems[0] : null;
    public WrappedTopAnimeItem? Top2 => TopAnimesItems.Count > 1 ? TopAnimesItems[1] : null;
    public WrappedTopAnimeItem? Top3 => TopAnimesItems.Count > 2 ? TopAnimesItems[2] : null;

    public bool HasTop1 => Top1 != null;
    public bool HasTop2 => Top2 != null;
    public bool HasTop3 => Top3 != null;

    public string Footer { get; init; } = "";
}
