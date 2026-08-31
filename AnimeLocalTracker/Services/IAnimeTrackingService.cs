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
    // Obtener los datos actuales de tu cuenta
    Task<AniListMediaList?> ObtenerSeguimientoUsuarioAsync(int mediaId, string token);
    // Guardar el panel completo de datos
    Task<bool> GuardarSeguimientoUsuarioAsync(int mediaId, string estado, int progreso, float puntaje, System.DateTime? fechaInicio, System.DateTime? fechaFin, string token);
    Task<AniListUser?> ObtenerPerfilUsuarioAsync(string token);
    Task<List<AniListMedia>> BuscarAnimesEnVivoAsync(string busqueda, System.Threading.CancellationToken cancellationToken = default);
    Task<List<AniListMedia>> ObtenerAnimesTendenciaAsync(System.Threading.CancellationToken cancellationToken = default);
    Task<List<AiringEpisode>> ObtenerCalendarioEmisionAsync(List<int> mediaIds, long inicioSemana, long finSemana);
}