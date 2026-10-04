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

    private static readonly string[] NombresReservados =
        ["CON", "PRN", "AUX", "NUL", .. Enumerable.Range(0, 10).SelectMany(n => new[] { $"COM{n}", $"LPT{n}" })];

    /// <summary>
    /// Nombre de la carpeta de un anime a partir de su título. Además de cambiar lo que Windows no admite en un nombre,
    /// nunca devuelve "." ni ".." (la carpeta del anime sería la biblioteca entera o la de encima, y borrar el anime
    /// borraría eso), ni un nombre terminado en punto o espacio (Windows los recorta y la ruta guardada no coincidiría),
    /// ni un nombre reservado del sistema (CON, NUL, COM1…).
    /// </summary>
    internal static string NombreDeCarpeta(string titulo, int aniListId)
    {
        string nombre = string.Join("_", titulo.Split(Path.GetInvalidFileNameChars())).Trim().TrimEnd('.', ' ');
        if (nombre.Length == 0) return $"Anime {aniListId}";

        string sinExtension = nombre.Split('.')[0].TrimEnd();
        return NombresReservados.Contains(sinExtension, StringComparer.OrdinalIgnoreCase) ? "_" + nombre : nombre;
    }

    /// <summary>
    /// Crea la carpeta, construye el AnimeItem con los metadatos de AniList, lo persiste
    /// en SQLite y notifica a la galería. Devuelve null si el anime ya existía.
    /// </summary>
    public async Task<AnimeItem?> CrearYGuardarAnimeAsync(AniListMedia animeAPI, string titulo)
    {
        if (animeAPI?.Title == null || string.IsNullOrWhiteSpace(titulo)) return null;

        if (await ExisteEnBibliotecaAsync(animeAPI.Id)) return null;

        string nombreSeguro = NombreDeCarpeta(titulo, animeAPI.Id);
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
            Sinopsis = animeAPI.Description ?? string.Empty,
            Temporada = animeAPI.Season ?? string.Empty,
            AnioLanzamiento = animeAPI.StartDate?.Year ?? 0,
            FechaAgregadoUtc = DateTime.UtcNow
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
    private static int CalcularEpisodiosEmitidos(AniListMedia animeAPI) => animeAPI.EpisodiosEmitidos(totalConocido: 0);
}
