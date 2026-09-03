using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services.Python;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Mantenimiento de caché: elimina miniaturas y portadas de animes/episodios que
/// ya no existen en la biblioteca (huérfanos tras borrar archivos o animes).
/// Nunca borra episodios ni datos del usuario: solo archivos de caché de imágenes.
/// </summary>
public class CacheMaintenanceService
{
    private static readonly string[] VideoExtensions =
        { ".mkv", ".mp4", ".avi", ".webm", ".ts", ".mov", ".m4v" };

    private readonly IDatabaseService _databaseService;

    public CacheMaintenanceService(IDatabaseService databaseService)
    {
        _databaseService = databaseService;
    }

    /// <summary>
    /// Recorre la biblioteca real (carpetas + base de datos) y borra las miniaturas
    /// y portadas que no corresponden a ningún archivo/anime existente.
    /// Devuelve el resumen de lo liberado.
    /// </summary>
    public async Task<ResultadoLimpieza> LimpiarCacheHuerfanoAsync()
    {
        var animes = await _databaseService.ObtenerTodosLosAnimesAsync() ?? new List<AnimeItem>();

        int miniaturasBorradas = 0;
        int portadasBorradas = 0;
        long bytesLiberados = 0;

        // 1. Miniaturas: el hash es unidireccional, así que reconstruimos el conjunto
        //    esperado escaneando las carpetas de animes de la biblioteca.
        var miniaturasEsperadas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var anime in animes)
        {
            if (string.IsNullOrWhiteSpace(anime.RutaCarpeta) || !Directory.Exists(anime.RutaCarpeta)) continue;
            try
            {
                foreach (var archivo in Directory.EnumerateFiles(anime.RutaCarpeta, "*", SearchOption.AllDirectories))
                {
                    if (VideoExtensions.Contains(Path.GetExtension(archivo).ToLowerInvariant()))
                    {
                        miniaturasEsperadas.Add(Path.GetFullPath(PythonEpisodeEnricher.ObtenerRutaMiniaturaEsperada(archivo)));
                    }
                }
            }
            catch { }
        }

        var thumbsDir = AppDataPaths.ThumbnailsDir;
        if (Directory.Exists(thumbsDir))
        {
            foreach (var thumb in Directory.EnumerateFiles(thumbsDir, "*.jpg"))
            {
                try
                {
                    var info = new FileInfo(thumb);
                    if (!miniaturasEsperadas.Contains(Path.GetFullPath(thumb)) || info.Length == 0)
                    {
                        bytesLiberados += info.Length;
                        File.Delete(thumb);
                        miniaturasBorradas++;
                    }
                }
                catch { }
            }
        }

        // 2. Portadas: el nombre es el AniListId → solo se conservan las de animes en la BD
        var idsValidos = new HashSet<int>(animes.Select(a => a.AniListId));
        var coversDir = AppDataPaths.CoversDir;
        if (Directory.Exists(coversDir))
        {
            foreach (var cover in Directory.EnumerateFiles(coversDir, "*.jpg"))
            {
                try
                {
                    if (int.TryParse(Path.GetFileNameWithoutExtension(cover), out int animeId) && !idsValidos.Contains(animeId))
                    {
                        bytesLiberados += new FileInfo(cover).Length;
                        File.Delete(cover);
                        portadasBorradas++;
                    }
                }
                catch { }
            }
        }

        return new ResultadoLimpieza(miniaturasBorradas, portadasBorradas, bytesLiberados);
    }

    public record ResultadoLimpieza(int MiniaturasBorradas, int PortadasBorradas, long BytesLiberados)
    {
        public double MegabytesLiberados => BytesLiberados / (1024.0 * 1024.0);
    }
}
