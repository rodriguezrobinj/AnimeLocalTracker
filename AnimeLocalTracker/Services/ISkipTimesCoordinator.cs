using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Orquesta la lógica de skip-times: resolución del MAL ID, carga de segmentos de AniSkip
/// y reglas de evaluación del skip activo.
/// </summary>
public interface ISkipTimesCoordinator
{
    /// <summary>
    /// Resuelve el MAL ID del anime (memoizado) y obtiene los segmentos de skip para el episodio.
    /// Devuelve lista vacía si no hay datos.
    /// </summary>
    Task<IReadOnlyList<AniSkipResult>> CargarSkipTimesAsync(int animeId, int episodio, double duracionSegundos, CancellationToken ct = default);

    /// <summary>
    /// Devuelve el segmento activo en <paramref name="currentSeconds"/>, o null.
    /// <paramref name="margenFinalSegundos"/> acorta el final del intervalo (p.ej. 0.5s en el bucle de tracking).
    /// </summary>
    AniSkipResult? ObtenerSkipActivo(double currentSeconds, IReadOnlyList<AniSkipResult> skipTimes, double margenFinalSegundos = 0);
}
