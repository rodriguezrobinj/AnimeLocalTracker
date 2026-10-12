using System;
using System.Collections.Generic;
using System.Linq;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

public class EpisodeNavigator : IEpisodeNavigator
{
    private List<EpisodioItem> _episodios = new();

    public IReadOnlyList<EpisodioItem> EpisodiosDisponibles => _episodios;

    public void EstablecerEpisodios(IEnumerable<EpisodioItem>? episodios)
    {
        if (episodios == null) return;

        _episodios = episodios
            .Where(e => !string.IsNullOrWhiteSpace(e.RutaCompleta))
            .OrderBy(e => e.NumeroEpisodio)
            .ToList();
    }

    public EpisodioItem? ObtenerSiguiente(int episodioActual)
    {
        return _episodios
            .Where(e => e.NumeroEpisodio > episodioActual && !string.IsNullOrWhiteSpace(e.RutaCompleta))
            .OrderBy(e => e.NumeroEpisodio)
            .FirstOrDefault();
    }

    public EpisodioItem? ObtenerAnterior(int episodioActual)
    {
        return _episodios
            .Where(e => e.NumeroEpisodio < episodioActual && !string.IsNullOrWhiteSpace(e.RutaCompleta))
            .OrderByDescending(e => e.NumeroEpisodio)
            .FirstOrDefault();
    }

    /// <summary>
    /// Números de episodio que quedan ENTRE el actual y el destino (sin incluir ninguno de los dos): los que no están
    /// descargados y el salto se come. Vacío si son consecutivos. Da igual el orden (siguiente o anterior).
    /// </summary>
    public static IReadOnlyList<int> EpisodiosIntermedios(int actual, int destino)
    {
        int desde = Math.Min(actual, destino), hasta = Math.Max(actual, destino);
        return hasta - desde <= 1 ? Array.Empty<int>() : Enumerable.Range(desde + 1, hasta - desde - 1).ToList();
    }
}
