using System.Collections.Generic;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

public interface IDatabaseService
{
    Task InicializarBaseDatosAsync();
    Task CrearBackupRotativoAsync(int maxCopias = 5, string? backupDir = null);
    Task<List<AnimeItem>> ObtenerTodosLosAnimesAsync();
    Task GuardarAnimeAsync(AnimeItem anime);
    Task EliminarAnimeAsync(AnimeItem anime);
    
    // === NUEVOS MÉTODOS PARA EL TRACKING ===
    Task GuardarRegistroEpisodioAsync(RegistroEpisodio registro);
    Task GuardarRegistrosEpisodioBulkAsync(IEnumerable<RegistroEpisodio> registros);
    Task<List<RegistroEpisodio>> ObtenerRegistrosPorAnimeAsync(int aniListId);
    Task<List<RegistroEpisodio>> ObtenerTodosLosRegistrosAsync();
    Task<List<RegistroEpisodio>> ObtenerEpisodiosNoSincronizadosAsync();
    Task MarcarEpisodiosSincronizadosAsync(IEnumerable<int> ids);
    Task ActualizarAnimeAsync(AnimeItem anime);
}