// 2. La Implementación (DatabaseService.cs)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SQLite;
using AnimeLocalTracker.Core;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

public class DatabaseService : IDatabaseService, IDisposable
{
    private readonly string? _customDbPath;
    private SQLiteAsyncConnection _conexion = null!;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    // CA1869: opciones de serialización reutilizadas (export JSON)
    private static readonly System.Text.Json.JsonSerializerOptions JsonOpcionesIndentadas = new() { WriteIndented = true };

    public DatabaseService(string? customDbPath = null)
    {
        _customDbPath = customDbPath;
    }

    // CA1001: el lock de inicialización se libera al cerrar la app (el contenedor DI
    // dispone los singletons en el cierre de ServiceProvider)
    public void Dispose()
    {
        _initLock.Dispose();
        GC.SuppressFinalize(this);
    }

    public async Task InicializarBaseDatosAsync()
    {
        if (_conexion != null) return;

        // Doble chequeo con lock: App.OnStartup y los tests pueden inicializar concurrentemente
        await _initLock.WaitAsync();
        try
        {
            if (_conexion != null) return;

            string rutaBaseDatos;
            if (!string.IsNullOrEmpty(_customDbPath))
            {
                rutaBaseDatos = _customDbPath;
            }
            else
            {
                var rutaCarpetaApp = Path.GetDirectoryName(AppDataPaths.BibliotecaDb)!;
                Directory.CreateDirectory(rutaCarpetaApp);
                rutaBaseDatos = AppDataPaths.BibliotecaDb;
            }

            var conexion = new SQLiteAsyncConnection(rutaBaseDatos);

            await conexion.ExecuteScalarAsync<string>("PRAGMA journal_mode=WAL;");
            await conexion.ExecuteAsync("PRAGMA synchronous = NORMAL;");
            await conexion.ExecuteAsync("PRAGMA temp_store = MEMORY;");
            await conexion.ExecuteAsync("PRAGMA cache_size = -64000;");

            // ARC-06: el esquema evoluciona con migraciones versionadas (user_version),
            // no con CreateTableAsync suelto (que no altera columnas existentes).
            await EjecutarMigracionesPendientesAsync(conexion);

            _conexion = conexion;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// ARC-06: migraciones versionadas. Cada entrada (Versión, Descripción, Aplicar)
    /// se ejecuta en orden; user_version avanza al terminar cada una, de modo que una
    /// base vieja migra hasta la última versión y una nueva las aplica todas.
    /// </summary>
    private static readonly (int Version, string Descripcion, Func<SQLiteAsyncConnection, Task> Aplicar)[] Migraciones =
    {
        (1, "esquema base (tablas AnimeItem/RegistroEpisodio + índice compuesto)", CrearEsquemaBaseAsync),
        (2, "índice de la cola de sincronización (VistoLocal, SincronizadoEnNube)", CrearIndiceColaSincronizacionAsync),
        (3, "índice de UltimaReproduccion para el historial", CrearIndiceUltimaReproduccionAsync),
        (4, "columnas Temporada/AnioLanzamiento/EsFavorito en AnimeItem", AgregarColumnasTemporadaFavoritoAsync),
        (5, "tabla de logros desbloqueados + índice único (LogroId, Nivel)", CrearTablaLogrosAsync),
        (6, "relaciones entre animes (franquicias) + marca de sincronización", CrearTablasRelacionesAsync),
        (7, "historial de descargas (completadas y fallidas)", CrearTablaHistorialDescargasAsync),
        (8, "copia local de la próxima emisión (cuenta atrás)", CrearTablaProximaEmisionAsync),
        (9, "preferencias de emisión (avisos/descarga automática) y datos extra de AniList", CrearTablasPreferenciasYDatosExtraAsync),
        (10, "eliminar índices duplicados creados por [Indexed] (DB-01)", EliminarIndicesRedundantesAsync),
        (11, "partidas de minijuegos (récords y logros) + índice (JuegoId, Puntos)", CrearTablaPartidasMinijuegoAsync),
        (12, "personajes de AniList por anime (Adivina el personaje) + índice único (AnimeId, PersonajeId)", CrearTablasPersonajesAsync),
        (13, "análisis guardados de OP/ED por episodio (marcadores y saltos) + índices", CrearTablasSkipAsync),
        (14, "página de animeav1 ya verificada por anime (descargas sin repetir la búsqueda)", CrearTablaMediaAnimeAv1Async),
        (15, "descargas de música (openings/endings) en el historial de descargas", AgregarColumnasMusicaHistorialAsync),
        (16, "descartar análisis de OP/ED que pudieron guardarse con la respuesta de otra petición", DescartarAnalisisSkipDesfasadosAsync),
        (17, "copia local de la programación de emisión (Calendario y Actualizaciones sin conexión) + índices", CrearTablaEmisionesGuardadasAsync),
        (18, "seguimiento local por anime (editor de la Ficha sin conexión y cambios pendientes de AniList)", CrearTablaSeguimientoLocalAsync),
        // Columna nueva del modelo: sqlite-net la añade con ALTER TABLE ADD COLUMN (misma vía que la v4). Los animes que ya
        // estaban quedan con NULL.
        (19, "fecha de alta de cada anime (orden \"Añadidos recientemente\" de la Galería)", AgregarColumnasTemporadaFavoritoAsync),
        (20, "sonido del reproductor por anime o por capítulo (volumen, ecualizador y Modo Noche)", CrearTablaAjusteAudioAsync),
        // Columna nueva del modelo (misma vía que la v4 y la v19). Los animes que ya estaban quedan sin proteger.
        (21, "conservar los videos de un anime (protege del borrado de \"Eliminar tras ver\")", AgregarColumnasTemporadaFavoritoAsync),
        // Un episodio con dos filas era posible por una carrera entre guardados (v22 la cierra): se unen las que ya hay (con copia
        // previa de la base) y el índice (AniListId, NumeroEpisodio) pasa a ser único.
        (22, "episodios sin filas repetidas (se unen) + índice único por (anime, episodio)", DeduplicarRegistrosEpisodioAsync)
    };

    /// <summary>v20: <see cref="AjusteAudio"/> (clave primaria = "anime_episodio"; se lee siempre por clave, sin índice).</summary>
    private static Task CrearTablaAjusteAudioAsync(SQLiteAsyncConnection conexion) => conexion.CreateTableAsync<AjusteAudio>();

    /// <summary>v18: <see cref="SeguimientoLocal"/> (clave primaria = AniListId; los pendientes son pocos, sin índice).</summary>
    private static Task CrearTablaSeguimientoLocalAsync(SQLiteAsyncConnection conexion) => conexion.CreateTableAsync<SeguimientoLocal>();

    /// <summary>v17: programación de emisión guardada (<see cref="EmisionGuardada"/>). Índices explícitos: [Indexed] solo aplica a bases nuevas.</summary>
    private static async Task CrearTablaEmisionesGuardadasAsync(SQLiteAsyncConnection conexion)
    {
        await conexion.CreateTableAsync<EmisionGuardada>();
        await conexion.ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS IX_EmisionGuardada_Episodio ON EmisionGuardada(AniListId, Episodio);");
        await conexion.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_EmisionGuardada_Fecha ON EmisionGuardada(EmisionUnixUtc);");
    }

    /// <summary>
    /// v16: hasta ahora, una petición al motor Python interrumpida (cambiar de episodio a mitad del análisis, salir de la Ficha mientras
    /// hacía miniaturas) dejaba su respuesta en el canal y la siguiente petición la leía como propia: el opening/ending de un episodio se
    /// guardaba en otro (Katainaka no Ossan II: el 11 tenía los del 10 y el 12 los del 11) o quedaba vacío. No hay forma de saber qué
    /// análisis salieron así, de modo que se descartan todos: cada episodio se vuelve a analizar al abrirlo (unos segundos, en segundo plano).
    /// </summary>
    private static async Task DescartarAnalisisSkipDesfasadosAsync(SQLiteAsyncConnection conexion)
    {
        await conexion.ExecuteAsync("DELETE FROM SegmentoSkipGuardado;");
        await conexion.ExecuteAsync("DELETE FROM AnalisisSkipEpisodio;");
    }

    /// <summary>
    /// v15: columnas Tipo/TemaClave/TemaTitulo en DescargaHistorial. sqlite-net las añade con ALTER TABLE ADD COLUMN si la
    /// tabla ya existe; en bases nuevas la v7 ya la creó completa y esto no hace nada. Las filas antiguas quedan con Tipo
    /// NULL = episodio.
    /// </summary>
    private static async Task AgregarColumnasMusicaHistorialAsync(SQLiteAsyncConnection conexion)
    {
        await conexion.CreateTableAsync<DescargaHistorial>();
    }

    /// <summary>v14: AniListId → página de animeav1 verificada (clave primaria = AniListId, sin más índices).</summary>
    private static Task CrearTablaMediaAnimeAv1Async(SQLiteAsyncConnection conexion) => conexion.CreateTableAsync<MediaAnimeAv1Verificado>();

    /// <summary>
    /// v13: tramos de opening/ending/resumen ya ubicados en cada episodio, para no volver a detectarlos ni consultar la nube. Los índices
    /// se crean aquí de forma explícita (los [Indexed] solo aplican a bases nuevas).
    /// </summary>
    private static async Task CrearTablasSkipAsync(SQLiteAsyncConnection conexion)
    {
        await conexion.CreateTableAsync<AnalisisSkipEpisodio>();
        await conexion.CreateTableAsync<SegmentoSkipGuardado>();
        await conexion.ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS IX_AnalisisSkip_Episodio ON AnalisisSkipEpisodio(AnimeId, Episodio);");
        await conexion.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_SegmentoSkip_Episodio ON SegmentoSkipGuardado(AnimeId, Episodio);");
    }

    /// <summary>
    /// v12: personajes de cada anime y marca de cuándo se consultaron. El índice único (AnimeId, PersonajeId) sirve a la vez
    /// para buscar por anime y para que un personaje repetido en la respuesta de AniList no se guarde dos veces.
    /// </summary>
    private static async Task CrearTablasPersonajesAsync(SQLiteAsyncConnection conexion)
    {
        await conexion.CreateTableAsync<PersonajeAnime>();
        await conexion.CreateTableAsync<PersonajesAnimeSync>();
        await conexion.ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS IX_PersonajeAnime_Unico ON PersonajeAnime(AnimeId, PersonajeId);");
    }

    /// <summary>
    /// v11: partidas de minijuegos terminadas. El índice (JuegoId, Puntos) se crea aquí de forma explícita (no con
    /// [Indexed], que solo aplica a bases nuevas y volvería a duplicar índices como en DB-01).
    /// </summary>
    private static async Task CrearTablaPartidasMinijuegoAsync(SQLiteAsyncConnection conexion)
    {
        await conexion.CreateTableAsync<PartidaMinijuego>();
        await conexion.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_PartidaMinijuego_JuegoPuntos ON PartidaMinijuego(JuegoId, Puntos);");
    }

    /// <summary>
    /// v10 (DB-01): los atributos [Indexed] de los modelos generaban, en bases nuevas, índices automáticos que duplicaban a
    /// los que crean las migraciones explícitas (mismo o menos columnas): <c>DescargaHistorial_FechaUtc</c>,
    /// <c>RegistroEpisodio_UltimaReproduccion</c> y <c>RegistroEpisodio_AniListId</c> (prefijo de
    /// IX_RegistroEpisodio_AnimeEp). Cada índice de más se actualiza en cada escritura sin ayudar a ninguna consulta.
    /// Se quitan los [Indexed] de los modelos y aquí se borran los ya creados; es idempotente.
    /// </summary>
    private static async Task EliminarIndicesRedundantesAsync(SQLiteAsyncConnection conexion)
    {
        foreach (var indice in new[] { "DescargaHistorial_FechaUtc", "RegistroEpisodio_UltimaReproduccion", "RegistroEpisodio_AniListId" })
        {
            await conexion.ExecuteAsync($"DROP INDEX IF EXISTS {indice};");
        }
    }

    /// <summary>v9: avisos/descarga automática por anime y caché semanal de los datos extra de AniList.</summary>
    private static async Task CrearTablasPreferenciasYDatosExtraAsync(SQLiteAsyncConnection conexion)
    {
        await conexion.CreateTableAsync<PreferenciaEmision>();
        await conexion.CreateTableAsync<DatosExtraAnime>();
    }

    /// <summary>v8: próximo episodio de cada anime en emisión, para no consultar AniList en cada visita.</summary>
    private static Task CrearTablaProximaEmisionAsync(SQLiteAsyncConnection conexion) => conexion.CreateTableAsync<ProximaEmisionLocal>();

    /// <summary>
    /// v7: historial de descargas. Para bases existentes hace falta esta migración (los [Indexed] del modelo solo
    /// aplican a bases nuevas), así que el índice por fecha se crea explícitamente.
    /// </summary>
    private static async Task CrearTablaHistorialDescargasAsync(SQLiteAsyncConnection conexion)
    {
        await conexion.CreateTableAsync<DescargaHistorial>();
        await conexion.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_DescargaHistorial_FechaUtc ON DescargaHistorial(FechaUtc);");
    }

    /// <summary>
    /// v6: relaciones de AniList (precuela/secuela/spin-off…) para agrupar franquicias en Estadísticas.
    /// El índice único evita duplicar una arista aunque se vuelva a sincronizar.
    /// </summary>
    private static async Task CrearTablasRelacionesAsync(SQLiteAsyncConnection conexion)
    {
        await conexion.CreateTableAsync<RelacionAnime>();
        await conexion.CreateTableAsync<RelacionAnimeSync>();
        await conexion.ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS IX_RelacionAnime_Unica ON RelacionAnime(AnimeId, RelacionadoId);");
    }

    /// <summary>
    /// v5: niveles de logros ya conseguidos. El índice único evita duplicar un nivel aunque dos
    /// evaluaciones coincidan (se inserta con INSERT OR IGNORE).
    /// </summary>
    private static async Task CrearTablaLogrosAsync(SQLiteAsyncConnection conexion)
    {
        await conexion.CreateTableAsync<LogroDesbloqueado>();
        await conexion.ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS IX_LogroDesbloqueado_Unico ON LogroDesbloqueado(LogroId, Nivel);");
    }

    private static async Task EjecutarMigracionesPendientesAsync(SQLiteAsyncConnection conexion)
    {
        int versionActual = await conexion.ExecuteScalarAsync<int>("PRAGMA user_version;");

        foreach (var (version, descripcion, aplicar) in Migraciones.OrderBy(m => m.Version))
        {
            if (version <= versionActual) continue;

            try { await aplicar(conexion); }
            catch (MigracionAplazadaException ex)
            {
                // Las siguientes se aplican en orden, así que también esperan: la app abre con el esquema que ya tenía.
                AppLogger.Warn("DatabaseService", $"Migración v{version} aplazada al próximo arranque: {ex.Message}.");
                return;
            }
            await conexion.ExecuteAsync($"PRAGMA user_version = {version};");
            AppLogger.Info("DatabaseService", $"Migración aplicada: v{version} ({descripcion}).");
        }
    }

    private static async Task CrearEsquemaBaseAsync(SQLiteAsyncConnection conexion)
    {
        await conexion.CreateTableAsync<AnimeItem>();
        await conexion.CreateTableAsync<RegistroEpisodio>();

        // ÍNDICE COMPUESTO PARA BÚSQUEDAS RÁPIDAS POR (AniListId, NumeroEpisodio)
        await conexion.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_RegistroEpisodio_AnimeEp ON RegistroEpisodio(AniListId, NumeroEpisodio);");
    }

    /// <summary>
    /// PERF-10: la consulta de la cola de sincronización (VistoLocal=1 y SincronizadoEnNube=0)
    /// barría la tabla completa (SCAN). Con este índice es un rango directo cuando la
    /// biblioteca crezca a cientos de miles de registros.
    /// </summary>
    private static async Task CrearIndiceColaSincronizacionAsync(SQLiteAsyncConnection conexion)
    {
        await conexion.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_RegistroEpisodio_SyncCola ON RegistroEpisodio(VistoLocal, SincronizadoEnNube);");
    }

    /// <summary>
    /// v3: el atributo [Indexed] del modelo solo aplica a bases NUEVAS (CreateTable no
    /// altera); esta migración crea el índice de UltimaReproduccion en bases existentes
    /// para que las consultas del historial no hagan SCAN.
    /// </summary>
    private static async Task CrearIndiceUltimaReproduccionAsync(SQLiteAsyncConnection conexion)
    {
        await conexion.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_RegistroEpisodio_UltimaReproduccion ON RegistroEpisodio(UltimaReproduccion);");
    }

    /// <summary>
    /// v4: sqlite-net migra automáticamente las columnas nuevas del modelo con
    /// ALTER TABLE ADD COLUMN cuando la tabla ya existe (CreateTableAsync es idempotente
    /// en bases nuevas, donde la migración 1 ya las creó).
    /// </summary>
    private static async Task AgregarColumnasTemporadaFavoritoAsync(SQLiteAsyncConnection conexion)
    {
        await conexion.CreateTableAsync<AnimeItem>();
    }

    /// <summary>
    /// v22: une las filas repetidas de un mismo episodio (ver <see cref="UnirFilasRepetidas"/>) y convierte el índice por
    /// (AniListId, NumeroEpisodio) en único. Si hay algo que unir, antes se guarda una copia de la base junto a ella
    /// (<c>Backups/biblioteca.antes-de-v22.AAAAMMDD-HHMMSSmmm.db</c>): la unión borra filas y no se puede deshacer sin ella.
    /// Si la copia no se puede hacer (disco lleno, carpeta de solo lectura…) la migración se aplaza al próximo arranque en vez
    /// de borrar filas sin red de seguridad o impedir que la app abra.
    /// </summary>
    private static async Task DeduplicarRegistrosEpisodioAsync(SQLiteAsyncConnection conexion)
    {
        int episodiosRepetidos = await conexion.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM (SELECT 1 FROM RegistroEpisodio GROUP BY AniListId, NumeroEpisodio HAVING COUNT(*) > 1);");

        if (episodiosRepetidos > 0)
        {
            try { await CopiaAntesDeUnirFilasAsync(conexion); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SQLiteException)
            {
                throw new MigracionAplazadaException($"no se pudo guardar la copia previa a unir filas ({ex.Message})");
            }
        }

        // Unir y crear el índice único en UNA transacción: nadie puede colar un duplicado entre ambos pasos (con dos pasos, un
        // segundo proceso podía hacer fallar el CREATE UNIQUE INDEX y la app no abría en cada arranque). El DROP va primero
        // porque, al escribir, la transacción toma ya el bloqueo de escritura y la lectura de abajo ve la base definitiva.
        int unidos = 0, borradas = 0;
        await conexion.RunInTransactionAsync(db =>
        {
            db.Execute("DROP INDEX IF EXISTS IX_RegistroEpisodio_AnimeEp;");

            var repetidas = db.Query<RegistroEpisodio>(
                "SELECT r.* FROM RegistroEpisodio r JOIN (SELECT AniListId, NumeroEpisodio FROM RegistroEpisodio " +
                "GROUP BY AniListId, NumeroEpisodio HAVING COUNT(*) > 1) d " +
                "ON d.AniListId = r.AniListId AND d.NumeroEpisodio = r.NumeroEpisodio " +
                "ORDER BY r.AniListId, r.NumeroEpisodio, r.Id");

            foreach (var grupo in repetidas.GroupBy(r => (r.AniListId, r.NumeroEpisodio)))
            {
                var filas = grupo.OrderBy(r => r.Id).ToList();
                db.Update(UnirFilasRepetidas(filas));
                foreach (var sobrante in filas.Skip(1)) db.Delete<RegistroEpisodio>(sobrante.Id);
                unidos++;
                borradas += filas.Count - 1;
            }

            db.Execute("CREATE UNIQUE INDEX IX_RegistroEpisodio_AnimeEp ON RegistroEpisodio(AniListId, NumeroEpisodio);");
        });

        if (unidos > 0) AppLogger.Info("DatabaseService", $"Episodios con filas repetidas unidos: {unidos} (se borraron {borradas} filas sobrantes).");
    }

    /// <summary>
    /// Copia de la base antes de unir filas. El nombre lleva la fecha: si v22 vuelve a correr (p. ej. tras restaurar una copia
    /// vieja con duplicados) no pisa la copia anterior, que puede ser la única que conserva las filas originales.
    /// </summary>
    private static async Task CopiaAntesDeUnirFilasAsync(SQLiteAsyncConnection conexion)
    {
        string destino = Path.Combine(Path.GetDirectoryName(conexion.DatabasePath)!, "Backups",
            $"biblioteca.antes-de-v22.{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.db");
        if (!await CrearSnapshotAtomicoAsync(conexion, destino)) throw new IOException("la copia no se creó");
    }

    /// <summary>Una migración que no puede aplicarse ahora sin riesgo: se reintenta en el próximo arranque (user_version no avanza).</summary>
    private sealed class MigracionAplazadaException(string motivo) : Exception(motivo);

    /// <summary>
    /// Une las filas de UN episodio en la primera, que es la que sobrevive. El orden de la lista es el de antigüedad (de la más
    /// vieja a la más nueva): la migración las pasa ordenadas por Id y un lote importado, en el orden del archivo (allí todos los
    /// Id valen 0). El resultado no depende de en qué posición esté cada dato. Reglas: visto y favorito si lo era alguna;
    /// sincronizado solo si lo estaban todas las filas vistas (un 'visto' sin enviar a AniList se sigue enviando); fecha de
    /// reproducción la más reciente (nunca se inventa) y progreso el de esa reproducción; ruta, miniatura y datos técnicos
    /// los de la fila más nueva que los tenga.
    /// </summary>
    internal static RegistroEpisodio UnirFilasRepetidas(IReadOnlyList<RegistroEpisodio> filas)
    {
        var unida = filas[0];
        var masNuevasPrimero = Enumerable.Reverse(filas).ToList();   // OrderBy es estable: ante un empate gana la más nueva
        var vistas = filas.Where(f => f.VistoLocal).ToList();
        unida.VistoLocal = vistas.Count > 0;
        unida.FavoritoLocal = filas.Any(f => f.FavoritoLocal);
        unida.SincronizadoEnNube = vistas.Count > 0 ? vistas.All(f => f.SincronizadoEnNube) : filas.Any(f => f.SincronizadoEnNube);
        unida.Es10Bit = filas.Any(f => f.Es10Bit);

        var reciente = masNuevasPrimero.Where(f => f.UltimaReproduccion != null).OrderByDescending(f => f.UltimaReproduccion).FirstOrDefault()
                       ?? masNuevasPrimero.OrderByDescending(f => f.ProgresoSegundos).First();
        unida.UltimaReproduccion = filas.Max(f => f.UltimaReproduccion);
        unida.ProgresoSegundos = reciente.ProgresoSegundos;
        unida.TotalSegundos = reciente.TotalSegundos > 0 ? reciente.TotalSegundos : filas.Max(f => f.TotalSegundos);

        string? MasNuevo(Func<RegistroEpisodio, string?> campo)
            => masNuevasPrimero.Select(campo).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        unida.RutaArchivo = MasNuevo(f => f.RutaArchivo) ?? string.Empty;
        unida.Resolucion = MasNuevo(f => f.Resolucion);
        unida.CodecVideo = MasNuevo(f => f.CodecVideo);
        unida.Fps = MasNuevo(f => f.Fps);
        unida.RutaMiniatura = MasNuevo(f => f.RutaMiniatura);
        return unida;
    }

    /// <summary>
    /// Copia de seguridad rotativa de la base de datos (historial de visionado, favoritos,
    /// sincronización). Se ejecuta al arrancar: conserva las últimas
    /// <paramref name="maxCopias"/> copias en %LocalAppData%\AnimeLocalTrackerData\Backups
    /// (inmunes a la desinstalación). BAK-02: la rotación y el snapshot corren en un hilo
    /// de fondo, no en el de la UI.
    /// </summary>
    public async Task CrearBackupRotativoAsync(int maxCopias = 5, string? backupDir = null)
    {
        try
        {
            backupDir ??= Path.Combine(AppDataPaths.DataRoot, "Backups");

            await Task.Run(async () =>
            {
                // Rotación: 4→5, 3→4, …, 1→2 (la copia más reciente queda en .backup.1.db)
                for (int i = maxCopias - 1; i >= 1; i--)
                {
                    string viejo = Path.Combine(backupDir, $"biblioteca.backup.{i}.db");
                    string nuevo = Path.Combine(backupDir, $"biblioteca.backup.{i + 1}.db");
                    if (File.Exists(viejo))
                    {
                        if (File.Exists(nuevo)) File.Delete(nuevo);
                        File.Move(viejo, nuevo);
                    }
                }

                string destino = Path.Combine(backupDir, "biblioteca.backup.1.db");
                if (!await CrearSnapshotAtomicoAsync(_conexion, destino)) return;

                AppLogger.Info("DatabaseService", $"Backup de la biblioteca creado: {destino}");
            });
        }
        catch (Exception ex)
        {
            AppLogger.Warn("DatabaseService", $"No se pudo crear el backup de la biblioteca: {ex.Message}");
        }
    }

    /// <summary>
    /// Snapshot atómico de la base de datos mediante <c>VACUUM INTO</c> (SQLite 3.27+):
    /// genera una copia íntegra leyendo la base + WAL en un solo paso, sin checkpoint
    /// manual (BAK-01/BAK-04) y sin bloquear escrituras concurrentes.
    /// </summary>
    private static async Task<bool> CrearSnapshotAtomicoAsync(SQLiteAsyncConnection? conexion, string rutaDestino)
    {
        if (conexion == null) return false;

        string dbPath = conexion.DatabasePath;
        if (!File.Exists(dbPath) || new FileInfo(dbPath).Length == 0) return false;

        var destinoDir = Path.GetDirectoryName(rutaDestino);
        if (!string.IsNullOrEmpty(destinoDir)) Directory.CreateDirectory(destinoDir);

        // VACUUM INTO no puede ejecutarse dentro de una transacción; escapar comillas.
        string destinoSql = rutaDestino.Replace("'", "''");
        void BorrarDestino() { if (File.Exists(rutaDestino)) File.Delete(rutaDestino); }
        BorrarDestino();
        await EjecutarFueraDeTransaccionAsync(conexion, $"VACUUM INTO '{destinoSql}'", BorrarDestino);
        return File.Exists(rutaDestino);
    }

    /// <summary>
    /// Ejecuta una orden que SQLite no admite dentro de una transacción (VACUUM, ATTACH, DETACH). sqlite-net la rechaza a veces
    /// ("cannot VACUUM from within a transaction" o un "not an error") cuando justo corre en otro hilo un
    /// <c>RunInTransactionAsync</c> sobre la misma conexión; el reintento corto lo resuelve (medido con guardados concurrentes:
    /// 22 de 125 intentos fallaban sin reintento y 0 con él). <paramref name="limpiar"/> deshace lo que dejó el intento fallido.
    /// </summary>
    private static async Task EjecutarFueraDeTransaccionAsync(SQLiteAsyncConnection conexion, string sql, Action? limpiar = null)
    {
        for (int intento = 1; ; intento++)
        {
            try
            {
                await conexion.ExecuteAsync(sql);
                return;
            }
            catch (SQLiteException) when (intento < 4)
            {
                limpiar?.Invoke();
                await Task.Delay(50 * intento);
            }
        }
    }

    public async Task GuardarAnimeAsync(AnimeItem anime)
    {
        // Reemplazar la fila de un anime que ya existía no puede apagarle la protección de "Conservar los videos".
        if (!anime.ConservarVideos) anime.ConservarVideos = await ObtenerConservarVideosAsync(anime.AniListId);

        // InsertOrReplace actualiza el registro si el AniListId ya existe, o lo inserta si es nuevo
        await _conexion.InsertOrReplaceAsync(anime);
    }

    public async Task<List<AnimeItem>> ObtenerTodosLosAnimesAsync()
    {
        var animes = await _conexion.Table<AnimeItem>().ToListAsync();
        await Task.Run(() => 
        {
            foreach (var a in animes)
            {
                a.ResolverPortadaLocal();
            }
        });
        return animes;
    }

    /// <summary>
    /// PERF-02: proyección ligera de la biblioteca para listas (calendario, notificador,
    /// comprobaciones): omite la columna Sinopsis (hasta ~20 KB por anime en HTML) que
    /// solo necesita la ficha de detalle.
    /// </summary>
    public async Task<List<AnimeItem>> ObtenerAnimesLigerosAsync()
    {
        var items = await _conexion.QueryAsync<AnimeItem>(
            "SELECT AniListId, MalId, Titulo, NombresAlternativos, RutaCarpeta, UrlPortada, " +
            "Generos, TotalEpisodios, Estado, EstadoUsuario FROM AnimeItem;");
        // Fuera del hilo que llama (casi siempre el de la interfaz: Historial, Actualizaciones, Calendario): son tantos
        // accesos a disco como animes hay en la biblioteca.
        await Task.Run(() =>
        {
            foreach (var a in items)
            {
                a.ResolverPortadaLocal();
            }
        });
        return items;
    }

    /// <summary>Devuelve la fila completa de un anime (incluida la Sinopsis) por su id.</summary>
    public async Task<AnimeItem?> ObtenerAnimePorIdAsync(int aniListId)
    {
        var anime = await _conexion.Table<AnimeItem>().FirstOrDefaultAsync(a => a.AniListId == aniListId);
        // Sin esto, PortadaVisible cae a la URL remota de AniList y la portada no se ve sin conexión
        // (Historial/Actualizaciones navegan a la Ficha con el anime que devuelve este método).
        anime?.ResolverPortadaLocal();
        return anime;
    }

    /// <summary>PERF-03: comprueba la existencia sin cargar la biblioteca completa.</summary>
    public async Task<bool> ExisteAnimeAsync(int aniListId)
    {
        return await _conexion.ExecuteScalarAsync<int>(
                   "SELECT COUNT(*) FROM AnimeItem WHERE AniListId = ?", aniListId) > 0;
    }
    
    public async Task EliminarAnimeAsync(AnimeItem anime)
    {
        await _conexion.DeleteAsync(anime);
        // Limpiar también los registros de episodios para no dejar huérfanos
        await _conexion.ExecuteAsync("DELETE FROM RegistroEpisodio WHERE AniListId = ?", anime.AniListId);
    }

    public async Task EliminarRegistroEpisodioAsync(int aniListId, int numeroEpisodio)
    {
        await _conexion.ExecuteAsync("DELETE FROM RegistroEpisodio WHERE AniListId = ? AND NumeroEpisodio = ?", aniListId, numeroEpisodio);
    }

    /// <summary>
    /// El historial es un registro permanente: al borrar el archivo del disco se limpia
    /// todo lo relacionado con el archivo (ruta, miniatura y metadatos técnicos), pero se
    /// CONSERVAN visto, progreso, favorito, sincronización y fecha de reproducción.
    /// Afecta a todas las filas del episodio: dos guardados simultáneos pueden haber dejado más de una.
    /// </summary>
    public async Task ConservarRegistroTrasEliminarArchivoAsync(int aniListId, int numeroEpisodio)
    {
        await _conexion.ExecuteAsync(
            "UPDATE RegistroEpisodio SET RutaArchivo = '', RutaMiniatura = NULL, Resolucion = '', CodecVideo = '', Fps = '', Es10Bit = 0 " +
            "WHERE AniListId = ? AND NumeroEpisodio = ?", aniListId, numeroEpisodio);
    }

    /// <summary>
    /// Restaura la biblioteca desde una copia de seguridad (.db) SIN cerrar ni reemplazar la base en uso: vuelca los datos de la
    /// copia dentro de la misma base, en una sola transacción (BAK-03). Antes de tocar nada: la copia debe ser de esta app
    /// (integridad, tablas conocidas y no de una versión más nueva), se ensayan las migraciones sobre una copia de trabajo y se
    /// guarda la biblioteca actual en <c>Backups\biblioteca.antes-de-restaurar.*.db</c> para poder deshacer. Si algo falla,
    /// la transacción se revierte y la biblioteca queda como estaba. Lo que se esté guardando a la vez espera su turno en la conexión.
    /// </summary>
    public async Task<bool> RestaurarCopiaSeguridadAsync(string rutaOrigen)
    {
        if (_conexion == null || !File.Exists(rutaOrigen) || new FileInfo(rutaOrigen).Length == 0) return false;

        string carpeta = Path.GetDirectoryName(_conexion.DatabasePath)!;
        string trabajo = Path.Combine(carpeta, "biblioteca.restaurando.db");
        try
        {
            // 1. Validar el origen (en solo lectura) y sacar de él una copia de trabajo limpia, junto a la base viva.
            if (!await Task.Run(() => PrepararCopiaDeTrabajo(rutaOrigen, trabajo))) return false;

            // 2. Ensayar las migraciones sobre la copia de trabajo: si no puede dejarse en la última versión, la base viva no se toca.
            if (!await MigrarCopiaDeTrabajoAsync(trabajo)) return false;

            // 3. Red de seguridad: lo que hay ahora, para deshacer una restauración equivocada.
            string previa = Path.Combine(carpeta, "Backups", $"biblioteca.antes-de-restaurar.{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.db");
            if (!await CrearSnapshotAtomicoAsync(_conexion, previa))
            {
                AppLogger.Warn("DatabaseService", "Restore rechazado: no se pudo guardar la biblioteca actual antes de reemplazarla.");
                return false;
            }

            // 4. Volcado atómico: ATTACH fuera de la transacción (SQLite no lo permite dentro) y todo lo demás en una sola.
            await EjecutarFueraDeTransaccionAsync(_conexion, $"ATTACH DATABASE '{trabajo.Replace("'", "''")}' AS restaurando;");
            try { await _conexion.RunInTransactionAsync(VolcarCopiaDeTrabajo); }
            finally { await EjecutarFueraDeTransaccionAsync(_conexion, "DETACH DATABASE restaurando;"); }

            AppLogger.Info("DatabaseService", $"Biblioteca restaurada desde: {rutaOrigen} (la anterior quedó en {previa}).");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("DatabaseService", "Fallo al restaurar; la biblioteca actual no se modificó", ex);
            return false;
        }
        finally
        {
            foreach (var sufijo in new[] { "", "-wal", "-shm", "-journal" })
            {
                try { File.Delete(trabajo + sufijo); } catch { /* sin permiso o bloqueado: se reemplaza en la próxima restauración */ }
            }
        }
    }

    /// <summary>Valida que el origen sea una copia de esta app y deja en <paramref name="trabajo"/> una copia limpia (VACUUM INTO).</summary>
    private static bool PrepararCopiaDeTrabajo(string rutaOrigen, string trabajo)
    {
        try
        {
            using var origen = new SQLiteConnection(rutaOrigen, SQLiteOpenFlags.ReadOnly);
            if (origen.ExecuteScalar<string>("PRAGMA integrity_check;") != "ok")
            {
                AppLogger.Warn("DatabaseService", "Restore rechazado: la copia no pasó integrity_check.");
                return false;
            }
            if (origen.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('AnimeItem', 'RegistroEpisodio');") != 2)
            {
                AppLogger.Warn("DatabaseService", "Restore rechazado: el archivo no es una copia de la biblioteca (faltan las tablas de animes y episodios).");
                return false;
            }
            int version = origen.ExecuteScalar<int>("PRAGMA user_version;");
            if (version > Migraciones.Max(m => m.Version))
            {
                AppLogger.Warn("DatabaseService", $"Restore rechazado: la copia es de una versión más nueva de la app (esquema v{version}).");
                return false;
            }

            if (File.Exists(trabajo)) File.Delete(trabajo);
            origen.Execute($"VACUUM INTO '{trabajo.Replace("'", "''")}';");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("DatabaseService", $"Restore rechazado: no se pudo abrir la copia ({ex.Message})");
            return false;
        }
    }

    /// <summary>Aplica a la copia de trabajo las migraciones que le falten; false si no llega a la última versión.</summary>
    private static async Task<bool> MigrarCopiaDeTrabajoAsync(string trabajo)
    {
        var conexion = new SQLiteAsyncConnection(trabajo);
        try
        {
            await EjecutarMigracionesPendientesAsync(conexion);
            if (await conexion.ExecuteScalarAsync<int>("PRAGMA user_version;") == Migraciones.Max(m => m.Version)) return true;
            AppLogger.Warn("DatabaseService", "Restore rechazado: una migración de la copia quedó aplazada.");
            return false;
        }
        finally
        {
            await conexion.CloseAsync();
        }
    }

    private sealed class InfoColumna
    {
        public string Name { get; set; } = string.Empty;
    }

    /// <summary>
    /// Dentro de la transacción de la restauración: vacía cada tabla de la base viva y la rellena con la de la copia de trabajo
    /// (adjunta como <c>restaurando</c>). Las columnas se emparejan por nombre: el esquema, los índices y user_version son los de la
    /// base viva.
    /// </summary>
    private static void VolcarCopiaDeTrabajo(SQLiteConnection db)
    {
        var tablas = db.Query<InfoColumna>("SELECT name AS Name FROM restaurando.sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';");
        foreach (var tabla in tablas.Select(t => t.Name.Replace("\"", "\"\"")))
        {
            var vivas = db.Query<InfoColumna>($"PRAGMA main.table_info(\"{tabla}\");").Select(c => c.Name).ToList();
            var copia = db.Query<InfoColumna>($"PRAGMA restaurando.table_info(\"{tabla}\");").Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (vivas.Count == 0) continue;  // tabla que esta versión no tiene

            db.Execute($"DELETE FROM main.\"{tabla}\";");
            var comunes = string.Join(", ", vivas.Where(copia.Contains).Select(c => $"\"{c.Replace("\"", "\"\"")}\""));
            if (comunes.Length > 0) db.Execute($"INSERT INTO main.\"{tabla}\" ({comunes}) SELECT {comunes} FROM restaurando.\"{tabla}\";");
        }
    }

    /// <summary>
    /// Exporta un snapshot íntegro de la base de datos (VACUUM INTO) a la ruta de destino
    /// elegida por el usuario. Devuelve true si se creó correctamente.
    /// </summary>
    public async Task<bool> ExportarCopiaSeguridadAsync(string rutaDestino)
    {
        try
        {
            // BAK-02: la copia corre en un hilo de fondo, no en la UI
            return await Task.Run(async () => await CrearSnapshotAtomicoAsync(_conexion, rutaDestino));
        }
        catch (Exception ex)
        {
            AppLogger.Warn("DatabaseService", $"No se pudo exportar la copia de seguridad: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Exporta la biblioteca completa (animes + registros de episodios) a un JSON
    /// de respaldo portable. Devuelve la cantidad de animes exportados.
    /// </summary>
    public async Task<int> ExportarBibliotecaJsonAsync(string rutaDestino)
    {
        var animes = await ObtenerTodosLosAnimesAsync() ?? new List<AnimeItem>();
        var registros = await ObtenerTodosLosRegistrosAsync() ?? new List<RegistroEpisodio>();

        var backup = new BibliotecaBackup
        {
            Animes = animes,
            Registros = registros
        };

        // PERF-07: la serialización (CPU) corre en el thread pool, no en el hilo de UI.
        var json = await Task.Run(() => System.Text.Json.JsonSerializer.Serialize(backup, JsonOpcionesIndentadas));

        var destinoDir = Path.GetDirectoryName(rutaDestino);
        if (!string.IsNullOrEmpty(destinoDir)) Directory.CreateDirectory(destinoDir);
        await File.WriteAllTextAsync(rutaDestino, json);

        return animes.Count;
    }

    /// <summary>Tope de tamaño del JSON de importación (IMP-01): evita agotar la RAM con archivos gigantes.</summary>
    private const long TopeImportacionBytes = 50L * 1024 * 1024;

    /// <summary>
    /// Importa una biblioteca desde JSON (generado por <see cref="ExportarBibliotecaJsonAsync"/>),
    /// fusionando con la existente (upsert por AniListId / AniListId+Episodio).
    /// Devuelve la cantidad de animes importados.
    /// </summary>
    public async Task<ResultadoImportacion> ImportarBibliotecaJsonAsync(string rutaOrigen, string? rutaBaseAnimes = null)
    {
        if (!File.Exists(rutaOrigen)) return default;

        // IMP-01: rechazar archivos fuera de rango antes de leerlos
        if (new FileInfo(rutaOrigen).Length > TopeImportacionBytes)
            throw new InvalidDataException("El archivo de importación supera el límite de 50 MB.");

        // PERF-07: la lectura del archivo y el parseo (CPU) corren en el thread pool.
        string json = await Task.Run(() => File.ReadAllText(rutaOrigen));

        // FUN-013: un JSON con tipos inválidos o malformado debe fallar con mensaje claro
        // (antes la JsonException llegaba al ViewModel como un error genérico "Error").
        BibliotecaBackup? backup;
        try
        {
            backup = await Task.Run(() => System.Text.Json.JsonSerializer.Deserialize<BibliotecaBackup>(json));
        }
        catch (System.Text.Json.JsonException ex)
        {
            AppLogger.Error("DatabaseService", "Import: el archivo no es un JSON de biblioteca válido", ex);
            throw new InvalidDataException("El archivo seleccionado no es un JSON de biblioteca válido de AnimeLocalTracker.");
        }
        if (backup?.Animes == null) return default;

        // IMP-02: saneado semántico — solo filas coherentes entran a la base
        var animesValidos = backup.Animes.Where(EsAnimeImportable).ToList();
        var registrosValidos = (backup.Registros ?? new List<RegistroEpisodio>())
            .Where(EsRegistroImportable)
            .Select(r =>
            {
                // IMP-04: los registros importados NUNCA generan sync automático a AniList
                // (el sync periódico empujaría el progreso ajeno a la nube sin consentimiento).
                r.SincronizadoEnNube = true;
                return r;
            })
            .ToList();

        // IMP-05: el archivo puede venir de otra persona. Una ruta de red (\\servidor\…) hace que Windows se conecte a ese
        // servidor con las credenciales del usuario en cuanto la app mira la carpeta, así que solo se conservan las de
        // servidores que esta biblioteca YA usa (sus animes o su carpeta base); el resto entra sin carpeta.
        var animesActuales = await _conexion.Table<AnimeItem>().ToListAsync();
        var carpetasActuales = animesActuales.ToDictionary(a => a.AniListId, a => a.RutaCarpeta);
        var protegidosActuales = animesActuales.Where(a => a.ConservarVideos).Select(a => a.AniListId).ToHashSet();
        var servidoresPropios = carpetasActuales.Values
            .Select(EntradaSegura.ServidorDeRed)
            .Append(EntradaSegura.ServidorDeRed(rutaBaseAnimes))
            .Where(s => s != null)
            .ToHashSet();
        bool EsDeServidorAjeno(string? ruta) => EntradaSegura.ServidorDeRed(ruta) is { } servidor && !servidoresPropios.Contains(servidor);

        var sinCarpeta = new HashSet<int>();
        foreach (var anime in animesValidos.Where(a => EsDeServidorAjeno(a.RutaCarpeta)))
        {
            // Si el anime ya estaba en la biblioteca conserva la carpeta que tenía aquí.
            anime.RutaCarpeta = carpetasActuales.GetValueOrDefault(anime.AniListId) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(anime.RutaCarpeta)) sinCarpeta.Add(anime.AniListId);
        }
        int rutasDeRedQuitadas = sinCarpeta.Count;
        foreach (var registro in registrosValidos)
        {
            if (EsDeServidorAjeno(registro.RutaArchivo)) registro.RutaArchivo = string.Empty;
            // Las miniaturas solo viven en la carpeta de datos de la app: cualquier otra ruta no es de aquí.
            if (!string.IsNullOrWhiteSpace(registro.RutaMiniatura) && !EntradaSegura.EstaDentroDe(AppDataPaths.ThumbnailsDir, registro.RutaMiniatura))
                registro.RutaMiniatura = null;
        }
        if (rutasDeRedQuitadas > 0)
            AppLogger.Warn("DatabaseService", $"Import: {rutasDeRedQuitadas} animes apuntaban a un servidor de red ajeno a esta biblioteca; se importan sin carpeta.");

        int descartados = (backup.Animes.Count - animesValidos.Count)
                          + ((backup.Registros ?? new List<RegistroEpisodio>()).Count - registrosValidos.Count);
        if (descartados > 0)
            AppLogger.Warn("DatabaseService", $"Import: {descartados} filas descartadas por validación");

        // FUN-013: AniListId duplicados DENTRO del propio JSON — antes el InsertOrReplace
        // hacía que el último pisara al primero en silencio. Ahora se avisa y gana el último.
        var agrupadosPorId = animesValidos.GroupBy(a => a.AniListId).ToList();
        int idsDuplicados = agrupadosPorId.Count(g => g.Count() > 1);
        if (idsDuplicados > 0)
        {
            AppLogger.Warn("DatabaseService", $"Import: {idsDuplicados} AniListId duplicados en el JSON; se conserva la última entrada de cada uno.");
        }
        var animesUnicos = agrupadosPorId.Select(g => g.Last()).ToList();

        // Una protección que ya existe aquí nunca se quita al importar (el archivo puede ser anterior a la función o de otra persona).
        foreach (var anime in animesUnicos.Where(a => protegidosActuales.Contains(a.AniListId))) anime.ConservarVideos = true;

        // IMP-03: todo o nada — una sola transacción; cualquier fallo revierte el lote completo
        await _conexion.RunInTransactionAsync(db =>
        {
            foreach (var anime in animesUnicos) db.InsertOrReplace(anime);
            AplicarUpsertRegistros(db, registrosValidos);
        });

        return new ResultadoImportacion(animesUnicos.Count, rutasDeRedQuitadas);
    }

    private static bool EsAnimeImportable(AnimeItem a)
    {
        if (a.AniListId <= 0) return false;
        if (string.IsNullOrWhiteSpace(a.Titulo) || a.Titulo.Length > 500) return false;
        if (a.Sinopsis?.Length > 20000) return false;
        if (a.Generos?.Length > 2000) return false;
        if (a.NombresAlternativos?.Length > 2000) return false;
        if (a.Estado?.Length > 32 || a.EstadoUsuario?.Length > 32) return false;
        if (a.UrlPortada?.Length > 2000) return false;
        if (a.TotalEpisodios is < 0 or > 10000) return false;
        if (!string.IsNullOrWhiteSpace(a.RutaCarpeta) && !EsRutaSanitaria(a.RutaCarpeta)) return false;
        return true;
    }

    private static bool EsRegistroImportable(RegistroEpisodio r)
    {
        if (r.AniListId <= 0) return false;
        if (r.NumeroEpisodio is <= 0 or > 3000) return false;
        if (r.ProgresoSegundos < 0 || r.TotalSegundos < 0) return false;
        // ~31 años de reproducción: protege las estadísticas de duraciones absurdas
        if (r.ProgresoSegundos > 1_000_000_000 || r.TotalSegundos > 1_000_000_000) return false;
        if (r.Resolucion?.Length > 32 || r.CodecVideo?.Length > 32 || r.Fps?.Length > 32) return false;
        if (r.RutaMiniatura?.Length > 1000) return false;
        if (!string.IsNullOrWhiteSpace(r.RutaArchivo) && !EsRutaSanitaria(r.RutaArchivo)) return false;
        return true;
    }

    /// <summary>
    /// IMP-02: ruta local bien formada — longitud acotada, sin caracteres inválidos y sin
    /// esquemas remotos (http/https/ftp). La contención estricta al árbol base no aplica
    /// porque el import entre máquinas legitima rutas de otro equipo.
    /// </summary>
    // CA1861/CA1870: conjunto de caracteres inválidos en caché (SearchValues, .NET 8)
    private static readonly System.Buffers.SearchValues<char> CaracteresRutaInvalidos =
        System.Buffers.SearchValues.Create(['\0', '?']);

    private static bool EsRutaSanitaria(string ruta)
    {
        if (string.IsNullOrWhiteSpace(ruta) || ruta.Length > 4000) return false;
        if (ruta.AsSpan().IndexOfAny(CaracteresRutaInvalidos) >= 0) return false;
        if (Uri.TryCreate(ruta, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeFtp))
            return false;
        return true;
    }

    /// <summary>Contenedor JSON portable de la biblioteca.</summary>
    public class BibliotecaBackup
    {
        public List<AnimeItem> Animes { get; set; } = new();
        public List<RegistroEpisodio> Registros { get; set; } = new();
    }
    
    public async Task GuardarRegistroEpisodioAsync(RegistroEpisodio registro)
    {
        // Buscar e insertar/actualizar en UNA sola operación sobre la conexión. Antes eran dos (un SELECT y luego un INSERT): otra
        // petición (reproductor, miniaturas, ficha) podía colarse en medio y dejar dos filas del mismo episodio (39 en la base
        // real al revisarla el 2026-10-07). Solo se busca esta fila: guardar el progreso ocurre cada pocos segundos.
        await _conexion.RunInTransactionAsync(db =>
        {
            var existente = db.Table<RegistroEpisodio>()
                .FirstOrDefault(r => r.AniListId == registro.AniListId && r.NumeroEpisodio == registro.NumeroEpisodio);

            if (existente != null)
            {
                FusionarRegistro(existente, registro);
                db.Update(existente);
            }
            else
            {
                // Si es la primera vez, insertamos el nuevo registro (la fecha solo la pone
                // quien reproduce de verdad; los marcados manuales quedan sin fecha).
                db.Insert(registro);
            }
        });
    }

    /// <summary>
    /// Marca o desmarca un episodio como favorito sin tocar nada más. Antes la ficha guardaba un registro nuevo con
    /// GuardarRegistroEpisodioAsync y, como ese registro no traía el progreso, marcar como favorito un episodio a medio
    /// ver borraba el punto donde se había quedado.
    /// </summary>
    public async Task GuardarFavoritoEpisodioAsync(int aniListId, int numeroEpisodio, bool favorito, string? rutaArchivo)
    {
        await _conexion.RunInTransactionAsync(db =>
        {
            var existente = db.Table<RegistroEpisodio>()
                .FirstOrDefault(r => r.AniListId == aniListId && r.NumeroEpisodio == numeroEpisodio);

            if (existente != null)
            {
                existente.FavoritoLocal = favorito;
                if (string.IsNullOrWhiteSpace(existente.RutaArchivo) && !string.IsNullOrWhiteSpace(rutaArchivo)) existente.RutaArchivo = rutaArchivo;
                db.Update(existente);
            }
            else
            {
                db.Insert(new RegistroEpisodio
                {
                    AniListId = aniListId,
                    NumeroEpisodio = numeroEpisodio,
                    RutaArchivo = rutaArchivo ?? string.Empty,
                    FavoritoLocal = favorito,
                });
            }
        });
    }

    public async Task GuardarRegistrosEpisodioBulkAsync(IEnumerable<RegistroEpisodio> registros)
    {
        if (registros == null) return;

        var lista = registros.ToList();
        if (lista.Count == 0) return;

        await _conexion.RunInTransactionAsync(db => AplicarUpsertRegistros(db, lista));
    }

    /// <summary>
    /// Upsert transaccional de registros de episodio con merge (misma semántica que
    /// <see cref="GuardarRegistroEpisodioAsync"/>): conserva RutaArchivo si el nuevo viene
    /// vacío y no duplica filas. Fuente única para el bulk y el import de biblioteca.
    /// </summary>
    private static void AplicarUpsertRegistros(SQLiteConnection db, List<RegistroEpisodio> registros)
    {
        if (registros.Count == 0) return;

        // El mismo episodio repetido dentro del lote (JSON exportado antes de v22, listas armadas a mano) se une en una sola fila con
        // las reglas de v22: un 'visto' o un favorito nunca se pierde por el orden en que vengan las filas.
        registros = registros
            .GroupBy(r => (r.AniListId, r.NumeroEpisodio))
            .Select(g => g.Count() == 1 ? g.First() : UnirFilasRepetidas(g.ToList()))
            .ToList();

        // 1 SELECT para todos los animes involucrados (antes: 1 SELECT + 1 INSERT/UPDATE POR FILA = N+1)
        var aniListIds = registros.Select(r => r.AniListId).Distinct().ToList();
        var existentes = db.Table<RegistroEpisodio>()
            .Where(r => aniListIds.Contains(r.AniListId))
            .ToList()
            .GroupBy(r => (r.AniListId, r.NumeroEpisodio))
            .ToDictionary(g => g.Key, g => g.First());

        // (la fecha de reproducción la fija el llamador; aquí no se fabrica "ahora")
        var aInsertar = new List<RegistroEpisodio>();
        var aActualizar = new HashSet<RegistroEpisodio>();

        foreach (var registro in registros)
        {
            if (existentes.TryGetValue((registro.AniListId, registro.NumeroEpisodio), out var existente))
            {
                FusionarRegistro(existente, registro);
                aActualizar.Add(existente);
            }
            else
            {
                // Registro nuevo: sin fecha (la pone solo la reproducción real).
                aInsertar.Add(registro);
            }
        }

        if (aInsertar.Count > 0) db.InsertAll(aInsertar, runInTransaction: false);
        if (aActualizar.Count > 0) db.UpdateAll(aActualizar, runInTransaction: false);
    }

    /// <summary>
    /// Vuelca un registro recién guardado sobre el que ya existía para ese episodio. Lo que el nuevo no trae se conserva:
    /// la ruta del archivo, los datos técnicos y la miniatura si vienen vacíos, y la fecha de última reproducción si viene
    /// a NULL (un marcado manual no es historial: no se fabrica "ahora").
    /// </summary>
    private static void FusionarRegistro(RegistroEpisodio existente, RegistroEpisodio registro)
    {
        existente.VistoLocal = registro.VistoLocal;
        existente.FavoritoLocal = registro.FavoritoLocal;
        existente.ProgresoSegundos = registro.ProgresoSegundos;
        existente.TotalSegundos = registro.TotalSegundos;
        existente.UltimaReproduccion = registro.UltimaReproduccion ?? existente.UltimaReproduccion;
        if (!string.IsNullOrWhiteSpace(registro.RutaArchivo)) existente.RutaArchivo = registro.RutaArchivo;
        if (!string.IsNullOrWhiteSpace(registro.Resolucion)) existente.Resolucion = registro.Resolucion;
        if (!string.IsNullOrWhiteSpace(registro.CodecVideo)) existente.CodecVideo = registro.CodecVideo;
        if (!string.IsNullOrWhiteSpace(registro.Fps)) existente.Fps = registro.Fps;
        if (registro.Es10Bit) existente.Es10Bit = registro.Es10Bit;
        if (!string.IsNullOrWhiteSpace(registro.RutaMiniatura)) existente.RutaMiniatura = registro.RutaMiniatura;
    }

    public async Task<List<RegistroEpisodio>> ObtenerRegistrosPorAnimeAsync(int aniListId)
    {
        // Traemos todos los capítulos que ya viste de un anime en específico
        return await _conexion.Table<RegistroEpisodio>()
            .Where(r => r.AniListId == aniListId)
            .ToListAsync();
    }

    public async Task<List<RegistroEpisodio>> ObtenerTodosLosRegistrosAsync()
    {
        return await _conexion.Table<RegistroEpisodio>().ToListAsync();
    }

    private sealed class ConteoVistos
    {
        public int AniListId { get; set; }
        public int Vistos { get; set; }
    }

    public async Task<Dictionary<int, int>> ObtenerEpisodiosVistosPorAnimeAsync()
    {
        var filas = await _conexion.QueryAsync<ConteoVistos>(
            "SELECT AniListId, COUNT(*) AS Vistos FROM RegistroEpisodio WHERE VistoLocal = 1 GROUP BY AniListId;");
        return filas.ToDictionary(f => f.AniListId, f => f.Vistos);
    }

    public async Task GuardarProximasEmisionesAsync(IEnumerable<ProximaEmisionLocal> proximas)
    {
        var lista = proximas?.Where(p => p != null && p.AniListId > 0).ToList();
        if (lista == null || lista.Count == 0) return;

        await _conexion.RunInTransactionAsync(db =>
        {
            foreach (var proxima in lista) db.InsertOrReplace(proxima);
        });
    }

    public async Task<List<RegistroEpisodio>> ObtenerHistorialEpisodiosAsync(int limite = 300)
    {
        // Traer episodios reproducidos o en progreso ordenados por fecha de reproducción más reciente
        return await _conexion.QueryAsync<RegistroEpisodio>(
            "SELECT * FROM RegistroEpisodio WHERE UltimaReproduccion IS NOT NULL OR ProgresoSegundos > 0 ORDER BY UltimaReproduccion DESC LIMIT ?;",
            limite);
    }

    public async Task<List<RegistroEpisodio>> ObtenerEpisodiosNoSincronizadosAsync()
    {
        return await _conexion.Table<RegistroEpisodio>()
            .Where(r => r.VistoLocal && !r.SincronizadoEnNube)
            .ToListAsync();
    }

    public async Task MarcarEpisodiosSincronizadosAsync(IEnumerable<int> ids)
    {
        var idList = ids.ToList();
        if (idList.Count == 0) return;

        await _conexion.RunInTransactionAsync(db =>
        {
            // 1 SELECT con IN + 1 UPDATE masivo (antes: 1 Find + 1 Update POR ID)
            var registros = db.Table<RegistroEpisodio>()
                .Where(r => idList.Contains(r.Id))
                .ToList();

            foreach (var reg in registros)
            {
                reg.SincronizadoEnNube = true;
            }
            if (registros.Count > 0)
            {
                db.UpdateAll(registros, runInTransaction: false);
            }
        });
    }
    
    public async Task ActualizarAnimeAsync(AnimeItem anime)
    {
        // La protección de "Conservar los videos" no se toca desde aquí: la copia en memoria puede ser vieja (la Galería guarda
        // la fila entera, también en el refresco automático de AniList). Manda la base de datos y la copia se pone al día.
        anime.ConservarVideos = await ObtenerConservarVideosAsync(anime.AniListId);
        await _conexion.UpdateAsync(anime);
    }

    public async Task ActualizarAnimesAsync(IEnumerable<AnimeItem> animes)
    {
        var lista = animes?.ToList();
        if (lista == null || lista.Count == 0) return;

        var protegidos = (await _conexion.QueryScalarsAsync<int>("SELECT AniListId FROM AnimeItem WHERE ConservarVideos = 1")).ToHashSet();
        foreach (var anime in lista) anime.ConservarVideos = protegidos.Contains(anime.AniListId);

        // PERF-06: UpdateAll en una sola transacción (los animes ya existen en la BD).
        await _conexion.UpdateAllAsync(lista);
    }

    public async Task GuardarConservarVideosAsync(int aniListId, bool conservar)
    {
        await _conexion.ExecuteAsync("UPDATE AnimeItem SET ConservarVideos = ? WHERE AniListId = ?", conservar ? 1 : 0, aniListId);
    }

    public async Task<bool> ObtenerConservarVideosAsync(int aniListId)
    {
        return await _conexion.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM AnimeItem WHERE AniListId = ? AND ConservarVideos = 1", aniListId) > 0;
    }

    /// <summary>
    /// PRI-01: vacía las tablas de la biblioteca local (animes + registros) en una sola
    /// transacción. El esquema y las migraciones se conservan; el backup rotativo se
    /// regenerará vacío en el siguiente arranque.
    /// </summary>
    public async Task VaciarBibliotecaAsync()
    {
        await _conexion.RunInTransactionAsync(db =>
        {
            db.Execute("DELETE FROM RegistroEpisodio;");
            db.Execute("DELETE FROM AnimeItem;");
            db.Execute("DELETE FROM LogroDesbloqueado;");
            db.Execute("DELETE FROM RelacionAnime;");
            db.Execute("DELETE FROM RelacionAnimeSync;");
            db.Execute("DELETE FROM DescargaHistorial;");
            db.Execute("DELETE FROM ProximaEmisionLocal;");
            db.Execute("DELETE FROM PreferenciaEmision;");
            db.Execute("DELETE FROM AjusteAudio;");
            db.Execute("DELETE FROM DatosExtraAnime;");
            db.Execute("DELETE FROM PartidaMinijuego;");
            db.Execute("DELETE FROM PersonajeAnime;");
            db.Execute("DELETE FROM PersonajesAnimeSync;");
            db.Execute("DELETE FROM AnalisisSkipEpisodio;");
            db.Execute("DELETE FROM SegmentoSkipGuardado;");
            db.Execute("DELETE FROM MediaAnimeAv1Verificado;");
            db.Execute("DELETE FROM EmisionGuardada;");
            db.Execute("DELETE FROM SeguimientoLocal;");
        });

        // Un DELETE solo marca las filas como libres: títulos, rutas e historial seguían legibles dentro del archivo
        // (y de su -wal). Compactar reescribe la base sin ellas y vaciar el -wal quita la copia que queda ahí.
        try
        {
            await _conexion.ExecuteAsync("VACUUM;");
            await _conexion.ExecuteScalarAsync<int>("PRAGMA wal_checkpoint(TRUNCATE);");
        }
        catch (Exception ex)
        {
            AppLogger.Warn("DatabaseService", $"No se pudo compactar la base de datos tras vaciarla: {ex.Message}");
        }
    }

    public async Task<List<RelacionAnime>> ObtenerRelacionesAnimeAsync()
    {
        return await _conexion.Table<RelacionAnime>().ToListAsync();
    }

    public async Task<List<RelacionAnimeSync>> ObtenerRelacionesSincronizadasAsync()
    {
        return await _conexion.Table<RelacionAnimeSync>().ToListAsync();
    }

    public async Task GuardarRelacionesAnimeAsync(IReadOnlyDictionary<int, List<RelacionAnime>> relacionesPorAnime)
    {
        if (relacionesPorAnime == null || relacionesPorAnime.Count == 0) return;

        var ahora = DateTime.UtcNow;
        await _conexion.RunInTransactionAsync(db =>
        {
            foreach (var (animeId, aristas) in relacionesPorAnime)
            {
                // Se reemplazan las aristas del anime consultado: AniList es la fuente de verdad.
                db.Execute("DELETE FROM RelacionAnime WHERE AnimeId = ?;", animeId);
                foreach (var arista in aristas)
                {
                    db.Execute(
                        "INSERT OR IGNORE INTO RelacionAnime (AnimeId, RelacionadoId, Tipo) VALUES (?, ?, ?);",
                        animeId, arista.RelacionadoId, arista.Tipo);
                }

                db.Execute("INSERT OR REPLACE INTO RelacionAnimeSync (AnimeId, FechaUtc) VALUES (?, ?);", animeId, ahora);
            }
        });
    }

    public async Task<List<LogroDesbloqueado>> ObtenerLogrosDesbloqueadosAsync()
    {
        return await _conexion.Table<LogroDesbloqueado>().ToListAsync();
    }

    public async Task GuardarLogrosDesbloqueadosAsync(IEnumerable<LogroDesbloqueado> logros)
    {
        if (logros == null) return;

        var lista = logros.ToList();
        if (lista.Count == 0) return;

        // INSERT OR IGNORE: el índice único (LogroId, Nivel) descarta duplicados sin lanzar.
        await _conexion.RunInTransactionAsync(db =>
        {
            foreach (var logro in lista)
            {
                db.Execute(
                    "INSERT OR IGNORE INTO LogroDesbloqueado (LogroId, Nivel, FechaUtc) VALUES (?, ?, ?);",
                    logro.LogroId, logro.Nivel, logro.FechaUtc);
            }
        });
    }

    public async Task<List<PartidaMinijuego>> ObtenerPartidasMinijuegoAsync()
    {
        return await _conexion.Table<PartidaMinijuego>().ToListAsync();
    }

    public async Task GuardarPartidaMinijuegoAsync(PartidaMinijuego partida)
    {
        if (partida == null || string.IsNullOrWhiteSpace(partida.JuegoId)) return;
        await _conexion.InsertAsync(partida);
    }

    public async Task<AnalisisSkipEpisodio?> ObtenerAnalisisSkipAsync(int animeId, int episodio)
    {
        return await _conexion.Table<AnalisisSkipEpisodio>().Where(a => a.AnimeId == animeId && a.Episodio == episodio).FirstOrDefaultAsync();
    }

    public async Task<List<SegmentoSkipGuardado>> ObtenerSegmentosSkipAsync(int animeId, int episodio)
    {
        return await _conexion.Table<SegmentoSkipGuardado>().Where(s => s.AnimeId == animeId && s.Episodio == episodio).ToListAsync();
    }

    public async Task GuardarAnalisisSkipAsync(AnalisisSkipEpisodio analisis, IReadOnlyList<SegmentoSkipGuardado> segmentos)
    {
        if (analisis == null || analisis.AnimeId <= 0 || analisis.Episodio <= 0) return;

        await _conexion.RunInTransactionAsync(db =>
        {
            // El análisis nuevo reemplaza al anterior del mismo episodio (tramos incluidos).
            db.Execute("DELETE FROM SegmentoSkipGuardado WHERE AnimeId = ? AND Episodio = ?;", analisis.AnimeId, analisis.Episodio);
            db.Execute("DELETE FROM AnalisisSkipEpisodio WHERE AnimeId = ? AND Episodio = ?;", analisis.AnimeId, analisis.Episodio);

            analisis.Id = 0;
            db.Insert(analisis);
            foreach (var s in segmentos ?? Array.Empty<SegmentoSkipGuardado>())
            {
                s.Id = 0;
                s.AnimeId = analisis.AnimeId;
                s.Episodio = analisis.Episodio;
                db.Insert(s);
            }
        });
    }

    public async Task<List<PersonajeAnime>> ObtenerPersonajesAsync(IReadOnlyCollection<int> animeIds)
    {
        if (animeIds == null || animeIds.Count == 0) return new List<PersonajeAnime>();

        var resultado = new List<PersonajeAnime>();
        // Lotes de 500: SQLite limita las variables de una consulta (los ids son enteros, no hay riesgo de inyección).
        foreach (var lote in animeIds.Distinct().Chunk(500))
        {
            string ids = string.Join(",", lote);
            resultado.AddRange(await _conexion.QueryAsync<PersonajeAnime>($"SELECT * FROM PersonajeAnime WHERE AnimeId IN ({ids});"));
        }
        return resultado;
    }

    public async Task<List<PersonajesAnimeSync>> ObtenerMarcasPersonajesAsync(IReadOnlyCollection<int> animeIds)
    {
        if (animeIds == null || animeIds.Count == 0) return new List<PersonajesAnimeSync>();

        var resultado = new List<PersonajesAnimeSync>();
        foreach (var lote in animeIds.Distinct().Chunk(500))
        {
            string ids = string.Join(",", lote);
            resultado.AddRange(await _conexion.QueryAsync<PersonajesAnimeSync>($"SELECT * FROM PersonajesAnimeSync WHERE AnimeId IN ({ids});"));
        }
        return resultado;
    }

    public async Task GuardarPersonajesAsync(IReadOnlyDictionary<int, List<PersonajeAnime>> personajesPorAnime)
    {
        if (personajesPorAnime == null || personajesPorAnime.Count == 0) return;

        var ahora = DateTime.UtcNow;
        await _conexion.RunInTransactionAsync(db =>
        {
            foreach (var (animeId, personajes) in personajesPorAnime)
            {
                // AniList es la fuente de verdad: se reemplazan los personajes guardados de ese anime.
                db.Execute("DELETE FROM PersonajeAnime WHERE AnimeId = ?;", animeId);
                foreach (var p in personajes)
                {
                    db.Execute(
                        "INSERT OR IGNORE INTO PersonajeAnime (AnimeId, PersonajeId, Nombre, NombreNativo, Alternativos, ImagenUrl, Genero, Edad, Rol, Favoritos) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?);",
                        animeId, p.PersonajeId, p.Nombre, p.NombreNativo, p.Alternativos, p.ImagenUrl, p.Genero, p.Edad, p.Rol, p.Favoritos);
                }

                db.Execute("INSERT OR REPLACE INTO PersonajesAnimeSync (AnimeId, FechaUtc) VALUES (?, ?);", animeId, ahora);
            }
        });
    }

    public async Task<AjusteAudio?> ObtenerAjusteAudioAsync(int aniListId, int numeroEpisodio)
    {
        return await _conexion.FindAsync<AjusteAudio>(AjusteAudio.ClaveDe(aniListId, numeroEpisodio));
    }

    public async Task GuardarAjusteAudioAsync(AjusteAudio ajuste)
    {
        if (ajuste == null || ajuste.AniListId <= 0) return;
        ajuste.Clave = AjusteAudio.ClaveDe(ajuste.AniListId, ajuste.NumeroEpisodio);
        await _conexion.InsertOrReplaceAsync(ajuste);
    }

    public async Task<PreferenciaEmision?> ObtenerPreferenciaEmisionAsync(int aniListId)
    {
        return await _conexion.FindAsync<PreferenciaEmision>(aniListId);
    }

    public async Task GuardarPreferenciaEmisionAsync(PreferenciaEmision preferencia)
    {
        if (preferencia == null || preferencia.AniListId <= 0) return;
        await _conexion.InsertOrReplaceAsync(preferencia);
    }

    public async Task<List<PreferenciaEmision>> ObtenerPreferenciasEmisionActivasAsync()
    {
        return await _conexion.Table<PreferenciaEmision>().Where(p => p.Avisar || p.AutoDescargar).ToListAsync();
    }

    public async Task<DatosExtraAnime?> ObtenerDatosExtraAsync(int aniListId)
    {
        return await _conexion.FindAsync<DatosExtraAnime>(aniListId);
    }

    public async Task GuardarDatosExtraAsync(DatosExtraAnime datos)
    {
        if (datos == null || datos.AniListId <= 0) return;
        await _conexion.InsertOrReplaceAsync(datos);
    }

    public async Task<ProximaEmisionLocal?> ObtenerProximaEmisionAsync(int aniListId)
    {
        return await _conexion.FindAsync<ProximaEmisionLocal>(aniListId);
    }

    public async Task GuardarProximaEmisionAsync(ProximaEmisionLocal proxima)
    {
        if (proxima == null || proxima.AniListId <= 0) return;
        await _conexion.InsertOrReplaceAsync(proxima);
    }

    public async Task<SeguimientoLocal?> ObtenerSeguimientoLocalAsync(int aniListId)
    {
        return await _conexion.FindAsync<SeguimientoLocal>(aniListId);
    }

    public async Task GuardarSeguimientoLocalAsync(SeguimientoLocal seguimiento)
    {
        if (seguimiento == null || seguimiento.AniListId <= 0) return;
        await _conexion.InsertOrReplaceAsync(seguimiento);
    }

    public async Task<List<SeguimientoLocal>> ObtenerSeguimientosPendientesAsync()
    {
        return await _conexion.Table<SeguimientoLocal>().Where(s => s.Pendiente).ToListAsync();
    }

    public async Task<List<ProximaEmisionLocal>> ObtenerProximasEmisionesAsync()
    {
        return await _conexion.Table<ProximaEmisionLocal>().ToListAsync();
    }

    /// <summary>Lo guardado más antiguo que esto se descarta (Actualizaciones mira 7 días atrás; el Calendario, la semana actual).</summary>
    private const int DiasEmisionesGuardadas = 60;

    public async Task GuardarEmisionesAsync(IReadOnlyCollection<int> animeIds, long inicioUnix, long finUnix, IReadOnlyList<EmisionGuardada> emisiones)
    {
        var ids = animeIds.Where(id => id > 0).Distinct().ToList();
        long limite = DateTimeOffset.UtcNow.AddDays(-DiasEmisionesGuardadas).ToUnixTimeSeconds();
        await _conexion.RunInTransactionAsync(db =>
        {
            // La ventana consultada se reemplaza entera: un episodio que AniList movió o quitó no queda duplicado.
            foreach (var lote in ids.Chunk(500))
                db.Execute($"DELETE FROM EmisionGuardada WHERE EmisionUnixUtc BETWEEN ? AND ? AND AniListId IN ({string.Join(",", lote)});", inicioUnix, finUnix);
            foreach (var e in emisiones)
            {
                // Mismo episodio con otra fecha (fuera de la ventana): se queda la más reciente. Insert simple, no InsertOrReplace:
                // sqlite-net incluiría el Id 0 en el REPLACE y cada fila pisaría a la anterior.
                db.Execute("DELETE FROM EmisionGuardada WHERE AniListId = ? AND Episodio = ?;", e.AniListId, e.Episodio);
                e.Id = 0;
                db.Insert(e);
            }
            db.Execute("DELETE FROM EmisionGuardada WHERE EmisionUnixUtc < ?;", limite);
        });
    }

    public async Task<List<EmisionGuardada>> ObtenerEmisionesAsync(long inicioUnix, long finUnix)
    {
        return await _conexion.Table<EmisionGuardada>().Where(e => e.EmisionUnixUtc >= inicioUnix && e.EmisionUnixUtc <= finUnix).ToListAsync();
    }

    public async Task<MediaAnimeAv1Verificado?> ObtenerMediaAnimeAv1Async(int aniListId)
    {
        return await _conexion.FindAsync<MediaAnimeAv1Verificado>(aniListId);
    }

    public async Task GuardarMediaAnimeAv1Async(MediaAnimeAv1Verificado media)
    {
        if (media == null || media.AniListId <= 0 || string.IsNullOrWhiteSpace(media.Slug)) return;
        await _conexion.InsertOrReplaceAsync(media);
    }

    // Tope de filas del historial de descargas: lo más antiguo se descarta para que la tabla no crezca sin límite.
    private const int MaxHistorialDescargas = 500;

    public async Task<List<DescargaHistorial>> ObtenerDescargasHistorialAsync(int limite = 300)
    {
        return await _conexion.Table<DescargaHistorial>()
            .OrderByDescending(d => d.FechaUtc)
            .Take(Math.Max(1, limite))
            .ToListAsync();
    }

    public async Task GuardarDescargaHistorialAsync(DescargaHistorial descarga)
    {
        if (descarga == null) return;

        await _conexion.RunInTransactionAsync(db =>
        {
            db.Insert(descarga);
            // Retención: se conservan solo las MaxHistorialDescargas más recientes.
            db.Execute(
                "DELETE FROM DescargaHistorial WHERE Id NOT IN (SELECT Id FROM DescargaHistorial ORDER BY FechaUtc DESC, Id DESC LIMIT ?);",
                MaxHistorialDescargas);
        });
    }

    public async Task EliminarDescargaHistorialAsync(int id)
    {
        await _conexion.ExecuteAsync("DELETE FROM DescargaHistorial WHERE Id = ?;", id);
    }

    public async Task LimpiarDescargasHistorialAsync(bool soloFallidas = false)
    {
        await _conexion.ExecuteAsync(soloFallidas
            ? "DELETE FROM DescargaHistorial WHERE Completada = 0;"
            : "DELETE FROM DescargaHistorial;");
    }
}
