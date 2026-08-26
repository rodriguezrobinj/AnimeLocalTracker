using AnimeLocalTracker.Core.Services;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Core.Services;

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
                stateInfo = JsonSerializer.Deserialize<DownloadStateInfo>(json) ?? new DownloadStateInfo();
                if (stateInfo.TotalBytes != totalBytes) throw new Exception("TotalBytes mismatch");
            }
            catch
            {
                // Estado corrupto o de otra descarga distinta: empezar de cero
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
