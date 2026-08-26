using AnimeLocalTracker.Core.Services;
// 2. La Implementación (DatabaseService.cs)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SQLite;
using AnimeLocalTracker.Core.Models;

namespace AnimeLocalTracker.Core.Services;

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
                var rutaAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var rutaCarpetaApp = Path.Combine(rutaAppData, "AnimeLocalTracker");
                Directory.CreateDirectory(rutaCarpetaApp);
                rutaBaseDatos = Path.Combine(rutaCarpetaApp, "biblioteca.db");
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
