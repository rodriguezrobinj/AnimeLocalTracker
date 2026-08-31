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

public class DatabaseService : IDatabaseService
{
    private readonly string? _customDbPath;
    private SQLiteAsyncConnection _conexion = null!;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public DatabaseService(string? customDbPath = null)
    {
        _customDbPath = customDbPath;
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

            _conexion = conexion;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Copia de seguridad rotativa de la base de datos (historial de visionado, favoritos,
    /// sincronización). Se ejecuta al arrancar: hace checkpoint del WAL para que la copia
    /// sea consistente y conserva las últimas <paramref name="maxCopias"/> copias en
    /// %LocalAppData%\AnimeLocalTrackerData\Backups (inmunes a la desinstalación).
    /// </summary>
    public async Task CrearBackupRotativoAsync(int maxCopias = 5, string? backupDir = null)
    {
        try
        {
            if (_conexion == null) return;

            string dbPath = _conexion.DatabasePath;
            var info = new FileInfo(dbPath);
            if (!info.Exists || info.Length == 0) return;

            // Checkpoint del WAL: sin esto la copia podría perder las escrituras recientes
            try
            {
                await _conexion.ExecuteAsync("PRAGMA wal_checkpoint(TRUNCATE);");
            }
            catch { }

            backupDir ??= Path.Combine(AppDataPaths.DataRoot, "Backups");
            Directory.CreateDirectory(backupDir);

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
            File.Copy(dbPath, destino, overwrite: true);

            AppLogger.Info("DatabaseService", $"Backup de la biblioteca creado: {destino}");
        }
        catch (Exception ex)
        {
            AppLogger.Warn("DatabaseService", $"No se pudo crear el backup de la biblioteca: {ex.Message}");
        }
    }

    public async Task GuardarAnimeAsync(AnimeItem anime)
    {
        // InsertOrReplace actualiza el registro si el AniListId ya existe, o lo inserta si es nuevo
        await _conexion.InsertOrReplaceAsync(anime);
    }

    public async Task<List<AnimeItem>> ObtenerTodosLosAnimesAsync()
    {
        return await _conexion.Table<AnimeItem>().ToListAsync();
    }
    
    public async Task EliminarAnimeAsync(AnimeItem anime)
    {
        await _conexion.DeleteAsync(anime);
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

        await _conexion.RunInTransactionAsync(db =>
        {
            // 1 SELECT para todos los animes involucrados (antes: 1 SELECT + 1 INSERT/UPDATE POR FILA = N+1)
            var aniListIds = lista.Select(r => r.AniListId).Distinct().ToList();
            var existentes = db.Table<RegistroEpisodio>()
                .Where(r => aniListIds.Contains(r.AniListId))
                .ToList()
                .GroupBy(r => (r.AniListId, r.NumeroEpisodio))
                .ToDictionary(g => g.Key, g => g.First());

            var ahora = DateTime.UtcNow;
            var aInsertar = new List<RegistroEpisodio>();
            var aActualizar = new List<RegistroEpisodio>();

            foreach (var registro in lista)
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
        });
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