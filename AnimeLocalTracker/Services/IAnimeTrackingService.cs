using System.Collections.Generic;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

public interface IAnimeTrackingService
{
    // Ahora devuelve una List<> en lugar de un solo objeto
    Task<List<AniListMedia>> BuscarAnimePorTituloAsync(string titulo); 
    Task<bool> ActualizarProgresoAsync(int mediaId, int episodio, string token);
    Task<AniListMedia?> ObtenerAnimePorIdAsync(int id);
    Task<Dictionary<int, AniListMedia>> ObtenerAnimesPorIdsLoteAsync(IEnumerable<int> ids, string? token = null);

    /// <summary>
    /// Relaciones (precuela, secuela, spin-off…) de cada anime, en lotes de 50. La clave está presente para
    /// todo anime cuyo lote se consultó bien — aunque no tenga relaciones —, y ausente si el lote falló.
    /// Solo devuelve relaciones con otros ANIME (no con el manga/novela original).
    /// </summary>
    Task<Dictionary<int, List<RelacionAnime>>> ObtenerRelacionesLoteAsync(IEnumerable<int> ids);
    // Obtener los datos actuales de tu cuenta
    Task<AniListMediaList?> ObtenerSeguimientoUsuarioAsync(int mediaId, string token);
    // Guardar el panel completo de datos
    Task<bool> GuardarSeguimientoUsuarioAsync(int mediaId, string estado, int progreso, float puntaje, System.DateTime? fechaInicio, System.DateTime? fechaFin, string token);
    /// <summary>
    /// Guarda SOLO las fechas indicadas (inicio y/o fin) y, si se pide, el estado COMPLETED; no toca progreso ni
    /// puntuación. Un valor null significa "no modificar" (nunca borra la fecha que AniList ya tenga).
    /// </summary>
    Task<bool> GuardarFechasSeguimientoAsync(int mediaId, System.DateTime? fechaInicio, System.DateTime? fechaFin, string token, bool marcarCompletado);
    /// <summary>
    /// Próximo episodio programado de un anime (consulta mínima y SIN caché en memoria: la usa la caché local de la
    /// cuenta atrás). Exito=false si la consulta falló; con Exito=true y Proximo=null, AniList no tiene ninguno programado.
    /// </summary>
    Task<(bool Exito, AniListNextAiringEpisode? Proximo)> ObtenerProximaEmisionAsync(int mediaId);
    /// <summary>
    /// Datos para las etiquetas de la ficha (nota media, formato, duración, estudio, fuente, tráiler). Exito=false si la
    /// consulta falló. Sin caché en memoria: la usa la caché local semanal.
    /// </summary>
    Task<(bool Exito, AniListMedia? Media)> ObtenerDatosExtraAsync(int mediaId);
    Task<AniListUser?> ObtenerPerfilUsuarioAsync(string token);
    Task<List<AniListMedia>> BuscarAnimesEnVivoAsync(string busqueda, System.Threading.CancellationToken cancellationToken = default);
    Task<List<AniListMedia>> ObtenerAnimesTendenciaAsync(System.Threading.CancellationToken cancellationToken = default);
    Task<List<AiringEpisode>> ObtenerCalendarioEmisionAsync(List<int> mediaIds, long inicioSemana, long finSemana);
}