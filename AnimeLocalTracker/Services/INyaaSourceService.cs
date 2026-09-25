using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Release encontrado en Nyaa.si, ya elegido por número de semillas dentro de los
/// candidatos válidos. <see cref="EsBatch"/> distingue un release de un solo episodio
/// (el caso normal) de un batch/temporada completa (Fase 2a): en ese caso
/// <c>ITorrentDownloadService</c> debe buscar el archivo del episodio pedido DENTRO
/// del torrent en vez de asumir que es el único video.
/// </summary>
public readonly record struct CandidatoTorrent(string Titulo, string TorrentUrl, string InfoHash, int Seeders, long TamanoBytes, bool EsBatch = false);

/// <summary>
/// Fuente de descarga por BitTorrent: busca en Nyaa.si el episodio pedido, prefiriendo
/// siempre un release de UN solo episodio; si no hay ninguno, cae a un batch/temporada
/// completa con semillas suficientes (Fase 2a — ver <see cref="NyaaRssParser.EsBatch"/>
/// y <see cref="CandidatoTorrent.EsBatch"/>). No descarga nada — eso lo hace
/// <c>ITorrentDownloadService</c> por separado.
/// </summary>
public interface INyaaSourceService
{
    /// <summary>
    /// Busca el episodio probando los títulos conocidos del anime (mismos términos
    /// de búsqueda, en el mismo orden de especificidad, que ya usa el buscador de
    /// catálogo de AnimeAV1). Null si ningún término encontró nada aprovechable
    /// (ni un solo episodio ni un batch con semillas suficientes).
    /// </summary>
    /// <param name="grupoPreferido">Fase 2b: AppSettings.GrupoFansubPreferidoTorrent —
    /// preferencia con fallback, ver <see cref="NyaaRssParser.ElegirMejorCandidato"/>.</param>
    /// <param name="resolucionPreferida">Fase 2b: AppSettings.ResolucionPreferidaTorrent —
    /// misma preferencia con fallback.</param>
    Task<CandidatoTorrent?> BuscarEpisodioAsync(
        IEnumerable<string> titulos,
        int numeroEpisodio,
        string? grupoPreferido = null,
        string? resolucionPreferida = null,
        CancellationToken ct = default);

    /// <summary>
    /// Fase 2d: igual que <see cref="BuscarEpisodioAsync"/> pero devuelve TODOS los
    /// candidatos válidos (ordenados igual: preferencias, luego semillas) del primer
    /// término que encontró alguno — para un selector manual. Lista vacía si ningún
    /// término encontró nada aprovechable.
    /// </summary>
    Task<List<CandidatoTorrent>> BuscarCandidatosAsync(
        IEnumerable<string> titulos,
        int numeroEpisodio,
        string? grupoPreferido = null,
        string? resolucionPreferida = null,
        CancellationToken ct = default);
}
