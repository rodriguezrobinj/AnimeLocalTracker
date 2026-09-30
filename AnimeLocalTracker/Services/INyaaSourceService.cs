using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Release encontrado en Nyaa.si, ya elegido por número de semillas dentro de los
/// candidatos válidos. <see cref="EsBatch"/> distingue un release de un solo episodio
/// (el caso normal) de un batch/temporada completa (Fase 2a): en ese caso
/// <c>ITorrentDownloadService</c> debe buscar el archivo del episodio pedido DENTRO
/// del torrent en vez de asumir que es el único video. <see cref="Dudoso"/>: el nombre del release no
/// se pudo confirmar como este anime y temporada (solo aparecen así en el selector manual).
/// </summary>
public readonly record struct CandidatoTorrent(string Titulo, string TorrentUrl, string InfoHash, int Seeders, long TamanoBytes, bool EsBatch = false, bool Dudoso = false);

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
    /// Busca el episodio con los títulos conocidos del anime y devuelve el mejor release
    /// verificado (mismo anime y temporada, episodio correcto). Null si no hay ninguno
    /// (ni un solo episodio ni un pack que lo incluya con semillas suficientes).
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
    /// candidatos verificados (ordenados igual: preferencias, luego semillas). Lista vacía
    /// si no hay ninguno. Con <paramref name="aniListId"/> también cuentan los releases de la parte
    /// anterior con numeración continua ("2nd Season - 14" = episodio 1 de "2nd Season Part 2").
    /// </summary>
    Task<List<CandidatoTorrent>> BuscarCandidatosAsync(
        IEnumerable<string> titulos,
        int numeroEpisodio,
        string? grupoPreferido = null,
        string? resolucionPreferida = null,
        int? aniListId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Para el selector manual: los mismos candidatos verificados que <see cref="BuscarCandidatosAsync"/>
    /// (sueltos y packs) y, al final, marcados como <see cref="CandidatoTorrent.Dudoso"/>, los releases con
    /// el episodio correcto cuyo nombre no se pudo confirmar como este anime — la descarga automática
    /// nunca los usa, pero el usuario puede reconocerlos y elegirlos.
    /// </summary>
    Task<List<CandidatoTorrent>> BuscarCandidatosParaElegirAsync(
        IEnumerable<string> titulos,
        int numeroEpisodio,
        string? grupoPreferido = null,
        string? resolucionPreferida = null,
        int? aniListId = null,
        CancellationToken ct = default);
}
