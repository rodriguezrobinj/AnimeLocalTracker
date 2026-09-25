using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Responsable único de resolver la URL directa de video para un episodio,
/// probando slugs generados a partir de los títulos y el catálogo de AnimeAV1.
/// </summary>
public interface IVideoSourceResolver
{
    /// <summary>
    /// Busca la URL de video del episodio en AnimeAV1 usando los títulos conocidos del anime.
    /// aniListId permite verificar la identidad del anime (MAL ID) y evitar
    /// confusiones entre títulos parecidos. audioPreferido es una PREFERENCIA con fallback
    /// (AppSettings.PreferenciaAudioAnimeAv1, "SUB"/"DUB"): si la pista pedida no está
    /// disponible para el episodio, se resuelve igual en la otra. servidorPreferido también
    /// es preferencia con fallback (AppSettings.ServidorPreferidoAnimeAv1, ej. "MP4Upload"):
    /// se prueba primero y, si falla, se sigue con el resto en el orden de siempre.
    /// </summary>
    Task<string?> BuscarUrlEpisodioAsync(IEnumerable<string> titulos, int numeroEpisodio, int? aniListId = null, string? audioPreferido = null, string? servidorPreferido = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Extrae la URL directa de video desde una página de animeav1.com o mp4upload.com.
    /// </summary>
    Task<string?> GetVideoUrlAsync(string pageUrl, CancellationToken cancellationToken = default);
}
