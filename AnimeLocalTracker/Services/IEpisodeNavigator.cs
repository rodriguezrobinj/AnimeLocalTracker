using System.Collections.Generic;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Dueño de la lista de episodios disponibles de la sesión de reproducción actual: consultas de
/// siguiente/anterior.
/// </summary>
public interface IEpisodeNavigator
{
    /// <summary>Episodios con archivo local, ordenados por número (los que llegaron por parámetro a CargarVideo).</summary>
    IReadOnlyList<EpisodioItem> EpisodiosDisponibles { get; }

    /// <summary>Reemplaza la lista de episodios disponibles (filtra los sin RutaCompleta y ordena por número).</summary>
    void EstablecerEpisodios(IEnumerable<EpisodioItem>? episodios);

    EpisodioItem? ObtenerSiguiente(int episodioActual);
    EpisodioItem? ObtenerAnterior(int episodioActual);
}
