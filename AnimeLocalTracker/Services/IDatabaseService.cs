using System.Collections.Generic;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Resultado de importar una biblioteca: animes fusionados y cuántos entraron SIN su carpeta porque apuntaba a un
/// servidor de red que esta biblioteca no usa (ver <see cref="AnimeLocalTracker.Core.EntradaSegura.ServidorDeRed"/>).
/// </summary>
public readonly record struct ResultadoImportacion(int Animes, int RutasDeRedQuitadas);

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
    /// <param name="rutaBaseAnimes">Carpeta de anime configurada: si está en un servidor de red, las rutas a ESE servidor se conservan.</param>
    Task<ResultadoImportacion> ImportarBibliotecaJsonAsync(string rutaOrigen, string? rutaBaseAnimes = null);

    // === NUEVOS MÉTODOS PARA EL TRACKING ===
    Task GuardarRegistroEpisodioAsync(RegistroEpisodio registro);
    /// <summary>Cambia SOLO la marca de favorito de un episodio (crea el registro si no existe): no toca progreso, visto ni historial.</summary>
    Task GuardarFavoritoEpisodioAsync(int aniListId, int numeroEpisodio, bool favorito, string? rutaArchivo);
    Task GuardarRegistrosEpisodioBulkAsync(IEnumerable<RegistroEpisodio> registros);
    Task<List<RegistroEpisodio>> ObtenerRegistrosPorAnimeAsync(int aniListId);
    Task<List<RegistroEpisodio>> ObtenerTodosLosRegistrosAsync();
    /// <summary>
    /// Cuántos episodios vistos tiene cada anime (los que no tienen ninguno no aparecen). Lo cuenta la base de datos: la
    /// Galería solo necesita ese número y antes traía todos los registros de episodios para contarlos ella.
    /// </summary>
    Task<Dictionary<int, int>> ObtenerEpisodiosVistosPorAnimeAsync();
    Task<List<RegistroEpisodio>> ObtenerHistorialEpisodiosAsync(int limite = 300);
    Task<List<RegistroEpisodio>> ObtenerEpisodiosNoSincronizadosAsync();
    Task MarcarEpisodiosSincronizadosAsync(IEnumerable<int> ids);
    Task ActualizarAnimeAsync(AnimeItem anime);

    /// <summary>
    /// "Conservar los videos": la única forma de cambiar la protección. Escribe solo esa columna; <see cref="ActualizarAnimeAsync"/>,
    /// <see cref="ActualizarAnimesAsync"/> y <see cref="GuardarAnimeAsync"/> nunca la apagan (una pantalla con una copia vieja del
    /// anime no puede quitarla sin que nadie se entere).
    /// </summary>
    Task GuardarConservarVideosAsync(int aniListId, bool conservar);

    /// <summary>Lo que dice la base de datos ahora: es la fuente de verdad de la protección, no la copia que tenga cada pantalla.</summary>
    Task<bool> ObtenerConservarVideosAsync(int aniListId);

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
    /// <summary>Guarda varias de una vez (una sola transacción): "Actualizar biblioteca" trae la de todos los animes en emisión.</summary>
    Task GuardarProximasEmisionesAsync(IEnumerable<ProximaEmisionLocal> proximas);
    Task<List<ProximaEmisionLocal>> ObtenerProximasEmisionesAsync();

    Task<SeguimientoLocal?> ObtenerSeguimientoLocalAsync(int aniListId);
    Task GuardarSeguimientoLocalAsync(SeguimientoLocal seguimiento);
    /// <summary>Cambios del editor de seguimiento hechos sin conexión, a la espera de subirse a AniList.</summary>
    Task<List<SeguimientoLocal>> ObtenerSeguimientosPendientesAsync();

    /// <summary>Guarda la programación que dio AniList para esos animes en esa ventana (la reemplaza entera).</summary>
    Task GuardarEmisionesAsync(IReadOnlyCollection<int> animeIds, long inicioUnix, long finUnix, IReadOnlyList<EmisionGuardada> emisiones);
    Task<List<EmisionGuardada>> ObtenerEmisionesAsync(long inicioUnix, long finUnix);

    // === PÁGINA DE ANIMEAV1 VERIFICADA por anime (las descargas van directo a ella, también tras reiniciar) ===
    Task<MediaAnimeAv1Verificado?> ObtenerMediaAnimeAv1Async(int aniListId);
    Task GuardarMediaAnimeAv1Async(MediaAnimeAv1Verificado media);

    // === PREFERENCIAS DE EMISIÓN (avisar / descargar automáticamente) y DATOS EXTRA de AniList (etiquetas de la ficha) ===
    /// <summary>Sonido guardado para ese anime (episodio 0) o para ese capítulo; null si no hay.</summary>
    Task<AjusteAudio?> ObtenerAjusteAudioAsync(int aniListId, int numeroEpisodio);
    Task GuardarAjusteAudioAsync(AjusteAudio ajuste);
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