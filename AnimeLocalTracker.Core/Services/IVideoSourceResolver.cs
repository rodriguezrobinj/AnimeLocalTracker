using AnimeLocalTracker.Core.Services;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Core.Services;

/// <summary>
/// Responsable único de resolver la URL directa de video para un episodio,
/// probando slugs generados a partir de los títulos y el catálogo de AnimeAV1.
/// </summary>
public interface IVideoSourceResolver
{
    /// <summary>
    /// Busca la URL de video del episodio en AnimeAV1 usando los títulos conocidos del anime.
    /// </summary>
    Task<string?> BuscarUrlEpisodioAsync(IEnumerable<string> titulos, int numeroEpisodio, CancellationToken cancellationToken = default);

    /// <summary>
    /// Extrae la URL directa de video desde una página de animeav1.com o mp4upload.com.
    /// </summary>
    Task<string?> GetVideoUrlAsync(string pageUrl, CancellationToken cancellationToken = default);
}
