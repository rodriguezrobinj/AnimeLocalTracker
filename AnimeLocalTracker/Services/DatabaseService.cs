// 2. La Implementación (DatabaseService.cs)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SQLite;
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

            // Creamos ambas tablas
            await conexion.CreateTableAsync<AnimeItem>();
            await conexion.CreateTableAsync<RegistroEpisodio>();

            // ÍNDICE COMPUESTO PARA BÚSQUEDAS RÁPIDAS POR (AniListId, NumeroEpisodio)
            await conexion.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_RegistroEpisodio_AnimeEp ON RegistroEpisodio(AniListId, NumeroEpisodio);");

            // ARQ-01: Versiones de base de datos para futuras migraciones de esquema
            int userVersion = await conexion.ExecuteScalarAsync<int>("PRAGMA user_version;");
            if (userVersion < 1)
            {
                // Versión base 1: Tablas iniciales ya creadas arriba con CreateTableAsync
                await conexion.ExecuteAsync("PRAGMA user_version = 1;");
            }

            _conexion = conexion;
        }
        finally
        {
            _initLock.Release();
        }
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
                if (!await CrearSnapshotAtomicoAsync(destino)) return;

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
    private async Task<bool> CrearSnapshotAtomicoAsync(string rutaDestino)
    {
        if (_conexion == null) return false;

        string dbPath = _conexion.DatabasePath;
        if (!File.Exists(dbPath) || new FileInfo(dbPath).Length == 0) return false;

        var destinoDir = Path.GetDirectoryName(rutaDestino);
        if (!string.IsNullOrEmpty(destinoDir)) Directory.CreateDirectory(destinoDir);

        // VACUUM INTO no puede ejecutarse dentro de una transacción; escapar comillas.
        string destinoSql = rutaDestino.Replace("'", "''");
        if (File.Exists(rutaDestino)) File.Delete(rutaDestino);
        await _conexion.ExecuteAsync($"VACUUM INTO '{destinoSql}'");
        return File.Exists(rutaDestino);
    }

    public async Task GuardarAnimeAsync(AnimeItem anime)
    {
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
    /// Restaura la biblioteca desde una copia de seguridad (.db): valida integridad
    /// SQLite ANTES de tocar la base actual, guarda el estado previo para revertir
    /// ante cualquier fallo, y reabre la conexión al final (BAK-03).
    /// </summary>
    public async Task<bool> RestaurarCopiaSeguridadAsync(string rutaOrigen)
    {
        if (!File.Exists(rutaOrigen) || new FileInfo(rutaOrigen).Length == 0) return false;

        // 1. Validar la integridad de la copia antes de tocar la base actual
        try
        {
            using var check = new SQLiteConnection(rutaOrigen);
            if (check.ExecuteScalar<string>("PRAGMA integrity_check;") != "ok")
            {
                AppLogger.Warn("DatabaseService", "Restore rechazado: la copia no pasó integrity_check.");
                return false;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("DatabaseService", $"Restore rechazado: no se pudo abrir la copia ({ex.Message})");
            return false;
        }

        if (_conexion == null) return false;
        string dbPath = _conexion.DatabasePath;
        string guard = dbPath + ".restore_prev";

        try
        {
            // 2. Guard del estado actual (permite revertir ante cualquier fallo)
            try { if (File.Exists(guard)) File.Delete(guard); } catch { }
            File.Copy(dbPath, guard, overwrite: true);

            // 3. Cerrar la conexión y reemplazar el archivo
            try { await _conexion.CloseAsync(); } catch { }
            _conexion = null!;
            File.Copy(rutaOrigen, dbPath, overwrite: true);

            // 4. Reabrir (recrea WAL, tablas e índices)
            await InicializarBaseDatosAsync();

            try { if (File.Exists(guard)) File.Delete(guard); } catch { }
            AppLogger.Info("DatabaseService", $"Biblioteca restaurada desde: {rutaOrigen}");
            return true;
        }
        catch (Exception ex)
        {
            // 5. Revertir al estado previo
            AppLogger.Error("DatabaseService", "Fallo al restaurar; revirtiendo al estado previo", ex);
            try { if (File.Exists(guard)) File.Copy(guard, dbPath, overwrite: true); } catch { }
            _conexion = null!;
            await InicializarBaseDatosAsync();
            return false;
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
            return await Task.Run(async () => await CrearSnapshotAtomicoAsync(rutaDestino));
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

        var json = System.Text.Json.JsonSerializer.Serialize(backup, JsonOpcionesIndentadas);

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
    public async Task<int> ImportarBibliotecaJsonAsync(string rutaOrigen)
    {
        if (!File.Exists(rutaOrigen)) return 0;

        // IMP-01: rechazar archivos fuera de rango antes de leerlos
        if (new FileInfo(rutaOrigen).Length > TopeImportacionBytes)
            throw new InvalidDataException("El archivo de importación supera el límite de 50 MB.");

        var json = await File.ReadAllTextAsync(rutaOrigen);
        var backup = System.Text.Json.JsonSerializer.Deserialize<BibliotecaBackup>(json);
        if (backup?.Animes == null) return 0;

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

        int descartados = (backup.Animes.Count - animesValidos.Count)
                          + ((backup.Registros ?? new List<RegistroEpisodio>()).Count - registrosValidos.Count);
        if (descartados > 0)
            AppLogger.Warn("DatabaseService", $"Import: {descartados} filas descartadas por validación");

        // IMP-03: todo o nada — una sola transacción; cualquier fallo revierte el lote completo
        await _conexion.RunInTransactionAsync(db =>
        {
            foreach (var anime in animesValidos) db.InsertOrReplace(anime);
            AplicarUpsertRegistros(db, registrosValidos);
        });

        return animesValidos.Count;
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
        // Buscamos si ya existe un registro previo para este anime y este capítulo
        var existente = await _conexion.Table<RegistroEpisodio>()
            .FirstOrDefaultAsync(r => r.AniListId == registro.AniListId && r.NumeroEpisodio == registro.NumeroEpisodio);

        if (existente != null)
        {
            // Si ya existe, actualizamos los campos
            existente.VistoLocal = registro.VistoLocal;
            existente.FavoritoLocal = registro.FavoritoLocal;
            existente.ProgresoSegundos = registro.ProgresoSegundos;
            existente.TotalSegundos = registro.TotalSegundos;
            existente.UltimaReproduccion = registro.UltimaReproduccion ?? DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(registro.RutaArchivo))
            {
                existente.RutaArchivo = registro.RutaArchivo;
            }
            if (!string.IsNullOrWhiteSpace(registro.Resolucion)) existente.Resolucion = registro.Resolucion;
            if (!string.IsNullOrWhiteSpace(registro.CodecVideo)) existente.CodecVideo = registro.CodecVideo;
            if (!string.IsNullOrWhiteSpace(registro.Fps)) existente.Fps = registro.Fps;
            if (registro.Es10Bit) existente.Es10Bit = registro.Es10Bit;
            if (!string.IsNullOrWhiteSpace(registro.RutaMiniatura)) existente.RutaMiniatura = registro.RutaMiniatura;

            await _conexion.UpdateAsync(existente);
        }
        else
        {
            // Si es la primera vez, insertamos el nuevo registro
            if (!registro.UltimaReproduccion.HasValue)
            {
                registro.UltimaReproduccion = DateTime.UtcNow;
            }
            await _conexion.InsertAsync(registro);
        }
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

        // 1 SELECT para todos los animes involucrados (antes: 1 SELECT + 1 INSERT/UPDATE POR FILA = N+1)
        var aniListIds = registros.Select(r => r.AniListId).Distinct().ToList();
        var existentes = db.Table<RegistroEpisodio>()
            .Where(r => aniListIds.Contains(r.AniListId))
            .ToList()
            .GroupBy(r => (r.AniListId, r.NumeroEpisodio))
            .ToDictionary(g => g.Key, g => g.First());

        var ahora = DateTime.UtcNow;
        var aInsertar = new List<RegistroEpisodio>();
        var aActualizar = new List<RegistroEpisodio>();

        foreach (var registro in registros)
        {
            if (existentes.TryGetValue((registro.AniListId, registro.NumeroEpisodio), out var existente))
            {
                // Mismo merge que GuardarRegistroEpisodioAsync: conservar RutaArchivo si el nuevo viene vacío
                existente.VistoLocal = registro.VistoLocal;
                existente.FavoritoLocal = registro.FavoritoLocal;
                existente.ProgresoSegundos = registro.ProgresoSegundos;
                existente.TotalSegundos = registro.TotalSegundos;
                existente.UltimaReproduccion = registro.UltimaReproduccion ?? ahora;
                if (!string.IsNullOrWhiteSpace(registro.RutaArchivo))
                {
                    existente.RutaArchivo = registro.RutaArchivo;
                }
                if (!string.IsNullOrWhiteSpace(registro.Resolucion)) existente.Resolucion = registro.Resolucion;
                if (!string.IsNullOrWhiteSpace(registro.CodecVideo)) existente.CodecVideo = registro.CodecVideo;
                if (!string.IsNullOrWhiteSpace(registro.Fps)) existente.Fps = registro.Fps;
                if (registro.Es10Bit) existente.Es10Bit = registro.Es10Bit;
                if (!string.IsNullOrWhiteSpace(registro.RutaMiniatura)) existente.RutaMiniatura = registro.RutaMiniatura;

                aActualizar.Add(existente);
            }
            else
            {
                if (!registro.UltimaReproduccion.HasValue)
                {
                    registro.UltimaReproduccion = ahora;
                }
                aInsertar.Add(registro);
            }
        }

        if (aInsertar.Count > 0) db.InsertAll(aInsertar, runInTransaction: false);
        if (aActualizar.Count > 0) db.UpdateAll(aActualizar, runInTransaction: false);
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
        await _conexion.UpdateAsync(anime);
    }
}