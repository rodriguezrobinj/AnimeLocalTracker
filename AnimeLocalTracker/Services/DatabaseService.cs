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
    private SQLiteAsyncConnection _conexion = null!;

    public async Task InicializarBaseDatosAsync()
    {
        if (_conexion != null) return;

        var rutaAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var rutaCarpetaApp = Path.Combine(rutaAppData, "AnimeLocalTracker");
        Directory.CreateDirectory(rutaCarpetaApp); 

        var rutaBaseDatos = Path.Combine(rutaCarpetaApp, "biblioteca.db");
        _conexion = new SQLiteAsyncConnection(rutaBaseDatos);

        await _conexion.ExecuteScalarAsync<string>("PRAGMA journal_mode=WAL;");

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
            // Si ya existe (ej: lo habías visto pero la sincronización falló), solo lo actualizamos
            existente.VistoLocal = registro.VistoLocal;
            existente.FavoritoLocal = registro.FavoritoLocal;
            await _conexion.UpdateAsync(existente);
        }
        else
        {
            // Si es la primera vez que lo ves, insertamos el nuevo registro
            await _conexion.InsertAsync(registro);
        }
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
    
    public async Task ActualizarAnimeAsync(AnimeItem anime)
    {
        await _conexion.UpdateAsync(anime);
    }
}