using System;
using System.Collections.Generic;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Dueño de la lista de episodios disponibles de la sesión de reproducción actual: consultas de
/// siguiente/anterior y pre-carga best-effort del siguiente episodio cerca del final del actual.
/// </summary>
public interface IEpisodeNavigator : IDisposable
{
    /// <summary>Episodios con archivo local, ordenados por número (los que llegaron por parámetro a CargarVideo).</summary>
    IReadOnlyList<EpisodioItem> EpisodiosDisponibles { get; }

    /// <summary>Reemplaza la lista de episodios disponibles (filtra los sin RutaCompleta y ordena por número).</summary>
    void EstablecerEpisodios(IEnumerable<EpisodioItem>? episodios);

    EpisodioItem? ObtenerSiguiente(int episodioActual);
    EpisodioItem? ObtenerAnterior(int episodioActual);

    /// <summary>
    /// Evalúa si toca disparar la pre-carga en este instante del sondeo de progreso (umbral del 95%
    /// + no precargado ya) y, si corresponde, la dispara en segundo plano (best-effort, nunca lanza).
    /// </summary>
    void ConsiderarPrecarga(double porcentaje, string? rutaSiguiente);

    /// <summary>
    /// Cancela la pre-carga en curso y reinicia el estado para la próxima — llamar tanto al cargar
    /// un episodio nuevo (descarta la pre-carga del anterior) como al liberar el ViewModel (Dispose).
    /// </summary>
    void CancelarPrecargaYReiniciar();
}
