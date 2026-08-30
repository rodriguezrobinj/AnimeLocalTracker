using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Services;

public class DownloadStateStore : IDownloadStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public async Task<DownloadStateInfo> CargarOInicializarAsync(string statePath, long totalBytes, int segmentCount)
    {
        DownloadStateInfo stateInfo;

        if (File.Exists(statePath))
        {
            try
            {
                string json = await File.ReadAllTextAsync(statePath);
                var deserialized = JsonSerializer.Deserialize<DownloadStateInfo>(json);
                if (EsEstadoValido(deserialized, totalBytes, segmentCount))
                {
                    stateInfo = deserialized!;
                }
                else
                {
                    AppLogger.Warn("DownloadStateStore", $"Archivo de estado '{statePath}' corrupto o no coincidente. Reiniciando descarga limpia.");
                    stateInfo = new DownloadStateInfo { TotalBytes = totalBytes };
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("DownloadStateStore", $"Error leyendo estado '{statePath}': {ex.Message}. Reiniciando.");
                stateInfo = new DownloadStateInfo { TotalBytes = totalBytes };
            }
        }
        else
        {
            stateInfo = new DownloadStateInfo { TotalBytes = totalBytes };
        }

        if (stateInfo.Segments.Count == 0)
        {
            long segmentSize = totalBytes / segmentCount;
            for (int i = 0; i < segmentCount; i++)
            {
                long start = i * segmentSize;
                long end = (i == segmentCount - 1) ? totalBytes - 1 : (start + segmentSize - 1);
                stateInfo.Segments.Add(new SegmentState { Start = start, End = end, CurrentOffset = start });
            }
        }

        return stateInfo;
    }

    private static bool EsEstadoValido(DownloadStateInfo? info, long totalBytes, int segmentCount)
    {
        if (info == null) return false;
        if (info.TotalBytes != totalBytes || info.TotalBytes <= 0) return false;
        if (info.Segments.Count != segmentCount) return false;

        long expectedStart = 0;
        for (int i = 0; i < info.Segments.Count; i++)
        {
            var seg = info.Segments[i];
            if (seg.Start != expectedStart) return false;
            if (seg.End < seg.Start || seg.End >= totalBytes) return false;
            if (seg.CurrentOffset < seg.Start || seg.CurrentOffset > seg.End + 1) return false;
            if (i == info.Segments.Count - 1 && seg.End != totalBytes - 1) return false;

            expectedStart = seg.End + 1;
        }

        return expectedStart == totalBytes;
    }

    public async Task GuardarAsync(string statePath, DownloadStateInfo info)
    {
        string json = JsonSerializer.Serialize(info, JsonOptions);
        await File.WriteAllTextAsync(statePath, json);
    }

    public void EliminarArchivosTemporales(string? rutaTemporal)
    {
        if (string.IsNullOrEmpty(rutaTemporal)) return;
        try
        {
            if (File.Exists(rutaTemporal)) File.Delete(rutaTemporal);
            string statePath = rutaTemporal + ".state";
            if (File.Exists(statePath)) File.Delete(statePath);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("DownloadStateStore", $"No se pudo limpiar temporales '{rutaTemporal}': {ex.Message}");
        }
    }
}
