using System.Collections.Generic;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

public interface IDatabaseService
{
    Task InicializarBaseDatosAsync();
    Task<List<AnimeItem>> ObtenerTodosLosAnimesAsync();
    Task GuardarAnimeAsync(AnimeItem anime);
    Task EliminarAnimeAsync(AnimeItem anime);
    
    // === NUEVOS MÉTODOS PARA EL TRACKING ===
    Task GuardarRegistroEpisodioAsync(RegistroEpisodio registro);
    Task<List<RegistroEpisodio>> ObtenerRegistrosPorAnimeAsync(int aniListId);
    Task<List<RegistroEpisodio>> ObtenerTodosLosRegistrosAsync();
    Task ActualizarAnimeAsync(AnimeItem anime);
}