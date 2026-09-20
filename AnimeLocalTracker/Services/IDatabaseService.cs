using System.Collections.Generic;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

public interface IDatabaseService
{
    Task InicializarBaseDatosAsync();
    Task CrearBackupRotativoAsync(int maxCopias = 5, string? backupDir = null);
    Task<List<AnimeItem>> ObtenerTodosLosAnimesAsync();

    // === LECTURAS LIGERAS (PERF-02/PERF-03): sin columnas pesadas ni cargas completas ===
    Task<List<AnimeItem>> ObtenerAnimesLigerosAsync();
    Task<AnimeItem?> ObtenerAnimePorIdAsync(int aniListId);
    Task<bool> ExisteAnimeAsync(int aniListId);

    Task GuardarAnimeAsync(AnimeItem anime);
    Task EliminarAnimeAsync(AnimeItem anime);
    Task EliminarRegistroEpisodioAsync(int aniListId, int numeroEpisodio);

    /// <summary>Al borrar el archivo del disco se conserva el registro (historial).</summary>
    Task ConservarRegistroTrasEliminarArchivoAsync(int aniListId, int numeroEpisodio);
    Task<bool> ExportarCopiaSeguridadAsync(string rutaDestino);
    Task<bool> RestaurarCopiaSeguridadAsync(string rutaOrigen);
    Task<int> ExportarBibliotecaJsonAsync(string rutaDestino);
    Task<int> ImportarBibliotecaJsonAsync(string rutaOrigen);
    
    // === NUEVOS MÉTODOS PARA EL TRACKING ===
    Task GuardarRegistroEpisodioAsync(RegistroEpisodio registro);
    Task GuardarRegistrosEpisodioBulkAsync(IEnumerable<RegistroEpisodio> registros);
    Task<List<RegistroEpisodio>> ObtenerRegistrosPorAnimeAsync(int aniListId);
    Task<List<RegistroEpisodio>> ObtenerTodosLosRegistrosAsync();
    Task<List<RegistroEpisodio>> ObtenerHistorialEpisodiosAsync(int limite = 300);
    Task<List<RegistroEpisodio>> ObtenerEpisodiosNoSincronizadosAsync();
    Task MarcarEpisodiosSincronizadosAsync(IEnumerable<int> ids);
    Task ActualizarAnimeAsync(AnimeItem anime);

    // === ESCRITURAS MASIVAS (PERF-06): una transacción por lote en vez de N escrituras ===
    Task ActualizarAnimesAsync(IEnumerable<AnimeItem> animes);

    // PRI-01: borrado total de la biblioteca local (tablas) para "Borrar todos mis datos".
    Task VaciarBibliotecaAsync();

    // === LOGROS: niveles ya conseguidos (para avisar una sola vez y no perderlos) ===
    Task<List<LogroDesbloqueado>> ObtenerLogrosDesbloqueadosAsync();
    Task GuardarLogrosDesbloqueadosAsync(IEnumerable<LogroDesbloqueado> logros);

    // === FRANQUICIAS: relaciones de AniList (precuela/secuela/spin-off…) para agrupar en Estadísticas ===
    Task<List<RelacionAnime>> ObtenerRelacionesAnimeAsync();
    Task<List<RelacionAnimeSync>> ObtenerRelacionesSincronizadasAsync();

    /// <summary>Reemplaza las aristas de cada anime consultado y lo marca como sincronizado (aunque no tenga ninguna).</summary>
    Task GuardarRelacionesAnimeAsync(IReadOnlyDictionary<int, List<RelacionAnime>> relacionesPorAnime);
}