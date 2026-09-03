using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Models;
using CommunityToolkit.Mvvm.Messaging;

namespace AnimeLocalTracker.Services;

/// <summary>
/// ARQ-02: lógica compartida de alta de animes en la biblioteca local.
/// Antes estaba duplicada (~70 líneas) en MainViewModel.SeleccionarYCrearAnimeAsync
/// y AgregarAnimeViewModel.AñadirAnimeAsync; ahora hay un único punto de verdad:
/// validación de existencia, sanitización de nombre, creación de carpeta, cálculo
/// de episodios emitidos por estado, títulos alternativos, persistencia y aviso
/// a la galería. Los ViewModels solo manejan su estado de UI.
/// </summary>
public class AnimeLibraryService
{
    private readonly IDatabaseService _databaseService;
    private readonly ISettingsService _settingsService;

    public AnimeLibraryService(IDatabaseService databaseService, ISettingsService settingsService)
    {
        _databaseService = databaseService;
        _settingsService = settingsService;
    }

    /// <summary>¿El anime ya está en la biblioteca local?</summary>
    public async Task<bool> ExisteEnBibliotecaAsync(int aniListId)
    {
        // PERF-03: antes se cargaba la biblioteca completa (+ File.Exists por anime)
        // solo para comprobar un id.
        return await _databaseService.ExisteAnimeAsync(aniListId);
    }

    /// <summary>
    /// Crea la carpeta, construye el AnimeItem con los metadatos de AniList, lo persiste
    /// en SQLite y notifica a la galería. Devuelve null si el anime ya existía.
    /// </summary>
    public async Task<AnimeItem?> CrearYGuardarAnimeAsync(AniListMedia animeAPI, string titulo)
    {
        if (animeAPI?.Title == null || string.IsNullOrWhiteSpace(titulo)) return null;

        if (await ExisteEnBibliotecaAsync(animeAPI.Id)) return null;

        string nombreSeguro = string.Join("_", titulo.Split(Path.GetInvalidFileNameChars()));
        string rutaBaseVideos = _settingsService.ObtenerRutaBaseAnimes();
        string nuevaRutaCarpeta = Path.Combine(rutaBaseVideos, nombreSeguro);

        if (!Directory.Exists(nuevaRutaCarpeta))
        {
            Directory.CreateDirectory(nuevaRutaCarpeta);
        }

        int episodiosEmitidos = CalcularEpisodiosEmitidos(animeAPI);

        var titulosAlt = new List<string>();
        if (!string.IsNullOrWhiteSpace(animeAPI.Title.English)) titulosAlt.Add(animeAPI.Title.English!);
        if (!string.IsNullOrWhiteSpace(animeAPI.Title.UserPreferred) && animeAPI.Title.UserPreferred != titulo)
            titulosAlt.Add(animeAPI.Title.UserPreferred!);
        // El título nativo (japonés) es clave: los sitios lo publican en su aka
        // ("ja-jp") y su catálogo lo matchea — el principal muchas veces no.
        if (!string.IsNullOrWhiteSpace(animeAPI.Title.Native)) titulosAlt.Add(animeAPI.Title.Native!);
        if (animeAPI.Synonyms != null) titulosAlt.AddRange(animeAPI.Synonyms.Where(s => !string.IsNullOrWhiteSpace(s)));

        var nuevoAnime = new AnimeItem
        {
            AniListId = animeAPI.Id,
            MalId = animeAPI.IdMal,
            Titulo = titulo,
            NombresAlternativos = string.Join(" | ", titulosAlt.Distinct()),
            UrlPortada = animeAPI.CoverImage?.ExtraLarge ?? animeAPI.CoverImage?.Large ?? string.Empty,
            RutaCarpeta = nuevaRutaCarpeta,
            Estado = animeAPI.Status ?? "UNKNOWN",
            TotalEpisodios = episodiosEmitidos,
            Generos = animeAPI.Genres != null ? string.Join(", ", animeAPI.Genres) : string.Empty,
            Sinopsis = animeAPI.Description ?? string.Empty
        };

        await _databaseService.GuardarAnimeAsync(nuevoAnime);

        // Notificar a la galería y al resto de la aplicación
        WeakReferenceMessenger.Default.Send(new AnimeAñadidoMensaje(nuevoAnime));

        return nuevoAnime;
    }

    /// <summary>
    /// Calcula cuántos episodios han salido según el estado del anime:
    /// no estrenado → 0; en emisión → el que sigue al último emitido; finalizado → total.
    /// </summary>
    private static int CalcularEpisodiosEmitidos(AniListMedia animeAPI)
    {
        string estadoAnime = animeAPI.Status?.ToUpperInvariant() ?? "UNKNOWN";

        return estadoAnime switch
        {
            "NOT_YET_RELEASED" => 0,
            "RELEASING" when animeAPI.NextAiringEpisode != null => Math.Max(0, animeAPI.NextAiringEpisode.Episode - 1),
            _ => animeAPI.Episodes ?? 0
        };
    }
}
