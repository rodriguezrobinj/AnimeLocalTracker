// 2. La Implementación (DatabaseService.cs)
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SQLite;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

public class DatabaseService : IDatabaseService
{
    private readonly string? _customDbPath;
    private SQLiteAsyncConnection _conexion = null!;

    public DatabaseService(string? customDbPath = null)
    {
        _customDbPath = customDbPath;
    }

    public async Task InicializarBaseDatosAsync()
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
        
        _conexion = new SQLiteAsyncConnection(rutaBaseDatos);

        await _conexion.ExecuteScalarAsync<string>("PRAGMA journal_mode=WAL;");
        await _conexion.ExecuteAsync("PRAGMA synchronous = NORMAL;");
        await _conexion.ExecuteAsync("PRAGMA temp_store = MEMORY;");
        await _conexion.ExecuteAsync("PRAGMA cache_size = -64000;");

        // Creamos ambas tablas
        await _conexion.CreateTableAsync<AnimeItem>();
        
        // NUEVA TABLA:
        await _conexion.CreateTableAsync<RegistroEpisodio>(); 

        // ÍNDICE COMPUESTO PARA BÚSQUEDAS RÁPIDAS POR (AniListId, NumeroEpisodio)
        await _conexion.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_RegistroEpisodio_AnimeEp ON RegistroEpisodio(AniListId, NumeroEpisodio);");
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

        await _conexion.RunInTransactionAsync(db =>
        {
            foreach (var registro in registros)
            {
                var existente = db.Table<RegistroEpisodio>()
                    .FirstOrDefault(r => r.AniListId == registro.AniListId && r.NumeroEpisodio == registro.NumeroEpisodio);

                if (existente != null)
                {
                    existente.VistoLocal = registro.VistoLocal;
                    existente.FavoritoLocal = registro.FavoritoLocal;
                    existente.ProgresoSegundos = registro.ProgresoSegundos;
                    existente.TotalSegundos = registro.TotalSegundos;
                    existente.UltimaReproduccion = registro.UltimaReproduccion ?? DateTime.UtcNow;
                    if (!string.IsNullOrWhiteSpace(registro.RutaArchivo))
                    {
                        existente.RutaArchivo = registro.RutaArchivo;
                    }
                    db.Update(existente);
                }
                else
                {
                    if (!registro.UltimaReproduccion.HasValue)
                    {
                        registro.UltimaReproduccion = DateTime.UtcNow;
                    }
                    db.Insert(registro);
                }
            }
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
            foreach (var id in idList)
            {
                var reg = db.Find<RegistroEpisodio>(id);
                if (reg != null)
                {
                    reg.SincronizadoEnNube = true;
                    db.Update(reg);
                }
            }
        });
    }
    
    public async Task ActualizarAnimeAsync(AnimeItem anime)
    {
        await _conexion.UpdateAsync(anime);
    }
}