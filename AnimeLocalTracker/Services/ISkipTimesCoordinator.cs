using System;
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
    /// Fuentes en orden: AniSkip API → detección local por escenas (Python/ffmpeg) si se pasa <paramref name="rutaVideoLocal"/>.
    /// Devuelve lista vacía si no hay datos.
    /// </summary>
    Task<IReadOnlyList<AniSkipResult>> CargarSkipTimesAsync(int animeId, int episodio, double duracionSegundos, string? rutaVideoLocal = null, CancellationToken ct = default);

    /// <summary>
    /// Igual que la anterior pero avisa de los tramos parciales en cuanto los hay: tras el análisis por audio y tras completar con AniSkip
    /// (la barra de progreso y el botón de saltar no esperan a que termine todo). El resultado devuelto es el final. Las notificaciones
    /// son instantáneas de la lista hasta ese momento; <paramref name="progreso"/> puede ser null.
    /// </summary>
    Task<IReadOnlyList<AniSkipResult>> CargarSkipTimesAsync(int animeId, int episodio, double duracionSegundos, string? rutaVideoLocal,
        IProgress<IReadOnlyList<AniSkipResult>>? progreso, CancellationToken ct);

    /// <summary>
    /// Deja analizado (y guardado) un episodio que aún no se está viendo, normalmente el siguiente: al abrirlo, las marcas salen al
    /// instante. Si mientras tanto se abre ese episodio, su carga se une a este análisis en vez de repetirlo.
    /// </summary>
    Task<IReadOnlyList<AniSkipResult>> PreanalizarAsync(int animeId, int episodio, string rutaVideoLocal, CancellationToken ct);

    /// <summary>
    /// Devuelve el segmento activo en <paramref name="currentSeconds"/>, o null.
    /// <paramref name="margenFinalSegundos"/> acorta el final del intervalo (p.ej. 0.5s en el bucle de tracking).
    /// </summary>
    AniSkipResult? ObtenerSkipActivo(double currentSeconds, IReadOnlyList<AniSkipResult> skipTimes, double margenFinalSegundos = 0);
}
