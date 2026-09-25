using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

public interface IDownloadService
{
    Task<string?> GetVideoUrlAsync(string pageUrl, CancellationToken cancellationToken = default);
    Task DownloadVideoAsync(string videoUrl, string destinationPath, IProgress<(double Progress, double Speed)>? progress = null, CancellationToken cancellationToken = default);
    bool EstaDescargando(int aniListId, int numeroEpisodio, out double progreso);
    Task IniciarDescargaEpisodioAsync(int aniListId, string animeTitulo, string carpetaDestino, int numeroEpisodio, IEnumerable<string>? titulosAlternativos = null);
    /// <summary>
    /// Como <see cref="IniciarDescargaEpisodioAsync"/>, pero para la descarga automática de episodios nuevos: si el
    /// episodio aún no está en el servidor, el fallo NO se apunta en el historial de descargas (se reintenta más tarde).
    /// </summary>
    Task IniciarDescargaAutomaticaAsync(int aniListId, string animeTitulo, string carpetaDestino, int numeroEpisodio, IEnumerable<string>? titulosAlternativos = null);

    /// <summary>
    /// Fase 2d: descarga por torrent el candidato elegido a mano por el usuario (sin
    /// búsqueda automática ni resolver HTTP). No-op si <see cref="ITorrentDownloadService"/>
    /// no está disponible o ya hay una descarga activa para ese episodio.
    /// </summary>
    Task IniciarDescargaTorrentManualAsync(int aniListId, string animeTitulo, string carpetaDestino, int numeroEpisodio, CandidatoTorrent candidatoElegido, IEnumerable<string>? titulosAlternativos = null);
    void CancelarDescarga(int aniListId, int numeroEpisodio);
    void CancelarTodas();
    void PausarDescarga(int aniListId, int numeroEpisodio);
    void PausarTodas();
    void ReanudarDescarga(int aniListId, int numeroEpisodio);
    void ReanudarTodas();
    IReadOnlyList<DescargaItem> ObtenerDescargasActivas();

    /// <summary>Adelanta una descarga en cola al primer puesto. False si no estaba esperando un slot.</summary>
    bool PriorizarDescarga(int aniListId, int numeroEpisodio);

    /// <summary>
    /// Ajusta en caliente el número máximo de descargas simultáneas.
    /// </summary>
    void ActualizarLimiteDescargas(int nuevoLimite);
}
