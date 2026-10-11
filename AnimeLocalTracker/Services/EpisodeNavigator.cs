using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

public class EpisodeNavigator : IEpisodeNavigator
{
    // CA1001: dueño de un CancellationTokenSource descartable.
    private bool _disposed;


    private List<EpisodioItem> _episodios = new();
    private string? _rutaPrecargada;

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

    /// <summary>
    /// ARQ-01: decisión pura (sin Player) de si toca disparar la pre-carga del siguiente episodio en
    /// este instante del sondeo de progreso — al menos 95% visto, hay un siguiente episodio con
    /// archivo, y no es el mismo que ya se precargó (como máximo una vez por episodio).
    /// </summary>
    public static bool DebePrecargarSiguienteEpisodio(double porcentaje, string? rutaSiguiente, string? rutaYaPrecargada) =>
        porcentaje >= 0.95 && !string.IsNullOrWhiteSpace(rutaSiguiente) && rutaSiguiente != rutaYaPrecargada;

    // Vida propia (no ligado al ct del bucle de tracking): se reinicia una vez por episodio
    // (CancelarPrecargaYReiniciar, llamado desde CargarVideoAsync) y se cancela en Dispose —
    // igual ciclo de vida que tenía _precargaCts en ReproductorViewModel antes de esta extracción.
    private CancellationTokenSource _precargaCts = new();

    public void ConsiderarPrecarga(double porcentaje, string? rutaSiguiente, Action<string, CancellationToken> precalentar)
    {
        if (!DebePrecargarSiguienteEpisodio(porcentaje, rutaSiguiente, _rutaPrecargada)) return;

        _rutaPrecargada = rutaSiguiente;
        precalentar(rutaSiguiente!, _precargaCts.Token);
    }

    public void CancelarPrecargaYReiniciar()
    {
        _precargaCts.Cancel();
        _precargaCts.Dispose();
        _precargaCts = new CancellationTokenSource();
        _rutaPrecargada = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);
        _precargaCts.Cancel();
        _precargaCts.Dispose();
    }
}
