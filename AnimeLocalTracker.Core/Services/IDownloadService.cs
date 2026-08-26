using AnimeLocalTracker.Core.Services;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Core.Models;

namespace AnimeLocalTracker.Core.Services;

public interface IDownloadService
{
    Task<string?> GetVideoUrlAsync(string pageUrl, CancellationToken cancellationToken = default);
    Task DownloadVideoAsync(string videoUrl, string destinationPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
    bool EstaDescargando(int aniListId, int numeroEpisodio, out double progreso);
    Task IniciarDescargaEpisodioAsync(int aniListId, string animeTitulo, string carpetaDestino, int numeroEpisodio, IEnumerable<string>? titulosAlternativos = null);
    void CancelarDescarga(int aniListId, int numeroEpisodio);
    void CancelarTodas();
    void PausarDescarga(int aniListId, int numeroEpisodio);
    void PausarTodas();
    void ReanudarDescarga(int aniListId, int numeroEpisodio);
    void ReanudarTodas();
    IReadOnlyList<DescargaItem> ObtenerDescargasActivas();
}
