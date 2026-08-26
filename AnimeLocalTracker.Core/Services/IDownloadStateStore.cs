using AnimeLocalTracker.Core.Services;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Core.Services;

/// <summary>
/// Estado persistible de una descarga segmentada (archivo .state junto al temporal).
/// </summary>
public class DownloadStateInfo
{
    public long TotalBytes { get; set; }
    public List<SegmentState> Segments { get; set; } = new();
}

public class SegmentState
{
    public long Start { get; set; }
    public long End { get; set; }
    public long CurrentOffset { get; set; }
}

/// <summary>
/// Responsable único de la persistencia y limpieza del estado de descargas
/// (archivos .downloading / .state), permitiendo reanudar descargas interrumpidas.
/// </summary>
public interface IDownloadStateStore
{
    /// <summary>
    /// Carga el estado previo desde <paramref name="statePath"/> o inicializa uno nuevo
    /// con <paramref name="segmentCount"/> segmentos uniformes.
    /// </summary>
    Task<DownloadStateInfo> CargarOInicializarAsync(string statePath, long totalBytes, int segmentCount);

    /// <summary>
    /// Persiste el estado actual (típicamente al pausar/interrumpir).
    /// </summary>
    Task GuardarAsync(string statePath, DownloadStateInfo info);

    /// <summary>
    /// Elimina el archivo temporal y su .state asociado (best-effort, sin lanzar).
    /// </summary>
    void EliminarArchivosTemporales(string? rutaTemporal);
}
