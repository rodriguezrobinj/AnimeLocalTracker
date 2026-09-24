using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Catálogo de openings/endings de un anime vía la API pública de AnimeThemes.moe
/// (https://api.animethemes.moe). Ver <see cref="AnimeThemesService"/>.
/// </summary>
public interface IAnimeThemesService
{
    /// <summary>Devuelve los OP/ED conocidos para este anime (identificado por su AniList ID, sin
    /// necesidad de matching por título: AnimeThemes mapea IDs externos directamente). Lista vacía
    /// si el anime no está en su base o la API no responde — nunca lanza.</summary>
    Task<List<AnimeThemeInfo>> ObtenerTemasAsync(int aniListId, CancellationToken ct = default);
}
