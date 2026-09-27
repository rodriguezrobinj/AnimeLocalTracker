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

    // === MINIJUEGOS: partidas terminadas (de ahí salen los récords y las métricas de los logros de Minijuegos) ===
    Task<List<PartidaMinijuego>> ObtenerPartidasMinijuegoAsync();
    Task GuardarPartidaMinijuegoAsync(PartidaMinijuego partida);

    // === SKIP: análisis guardado de cada episodio (dónde están su opening/ending/resumen) ===
    Task<AnalisisSkipEpisodio?> ObtenerAnalisisSkipAsync(int animeId, int episodio);
    Task<List<SegmentoSkipGuardado>> ObtenerSegmentosSkipAsync(int animeId, int episodio);
    /// <summary>Guarda el análisis de un episodio con sus tramos, reemplazando el anterior de ese mismo episodio.</summary>
    Task GuardarAnalisisSkipAsync(AnalisisSkipEpisodio analisis, IReadOnlyList<SegmentoSkipGuardado> segmentos);

    // === PERSONAJES: copia local de los personajes de AniList por anime ("Adivina el personaje" sin conexión) ===
    /// <summary>Personajes guardados de los animes indicados (ids de AniList).</summary>
    Task<List<PersonajeAnime>> ObtenerPersonajesAsync(IReadOnlyCollection<int> animeIds);
    /// <summary>Cuándo se consultaron a AniList los personajes de cada uno de los animes indicados (solo los ya consultados).</summary>
    Task<List<PersonajesAnimeSync>> ObtenerMarcasPersonajesAsync(IReadOnlyCollection<int> animeIds);
    /// <summary>Reemplaza los personajes de cada anime por los recibidos y anota la fecha de consulta (aunque no tenga ninguno).</summary>
    Task GuardarPersonajesAsync(IReadOnlyDictionary<int, List<PersonajeAnime>> personajesPorAnime);

    // === PRÓXIMA EMISIÓN: copia local del próximo episodio de animes en emisión (cuenta atrás sin red) ===
    Task<ProximaEmisionLocal?> ObtenerProximaEmisionAsync(int aniListId);
    Task GuardarProximaEmisionAsync(ProximaEmisionLocal proxima);

    // === PREFERENCIAS DE EMISIÓN (avisar / descargar automáticamente) y DATOS EXTRA de AniList (etiquetas de la ficha) ===
    Task<PreferenciaEmision?> ObtenerPreferenciaEmisionAsync(int aniListId);
    Task GuardarPreferenciaEmisionAsync(PreferenciaEmision preferencia);
    Task<List<PreferenciaEmision>> ObtenerPreferenciasEmisionActivasAsync();
    Task<DatosExtraAnime?> ObtenerDatosExtraAsync(int aniListId);
    Task GuardarDatosExtraAsync(DatosExtraAnime datos);

    // === DESCARGAS: historial de resultados finales (completadas y fallidas), más recientes primero ===
    Task<List<DescargaHistorial>> ObtenerDescargasHistorialAsync(int limite = 300);
    Task GuardarDescargaHistorialAsync(DescargaHistorial descarga);
    Task EliminarDescargaHistorialAsync(int id);
    /// <summary>Borra el historial: todo, o solo las fallidas si <paramref name="soloFallidas"/> es true.</summary>
    Task LimpiarDescargasHistorialAsync(bool soloFallidas = false);

    // === FRANQUICIAS: relaciones de AniList (precuela/secuela/spin-off…) para agrupar en Estadísticas ===
    Task<List<RelacionAnime>> ObtenerRelacionesAnimeAsync();
    Task<List<RelacionAnimeSync>> ObtenerRelacionesSincronizadasAsync();

    /// <summary>Reemplaza las aristas de cada anime consultado y lo marca como sincronizado (aunque no tenga ninguna).</summary>
    Task GuardarRelacionesAnimeAsync(IReadOnlyDictionary<int, List<RelacionAnime>> relacionesPorAnime);
}