using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

public partial class FileScannerService : IFileScannerService
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mkv", ".mp4", ".avi" };

    public async Task<List<EpisodioItem>> EscanearEpisodiosAsync(string carpeta)
    {
        return await Task.Run(() =>
        {
            var lista = new List<EpisodioItem>();
            if (!Directory.Exists(carpeta)) return lista;

            var dirInfo = new DirectoryInfo(carpeta);

            // Solo parciales abandonados: los de descargas en pausa o por reanudar se conservan
            LimpiezaDescargasParciales.LimpiarAbandonados(dirInfo, DateTime.UtcNow);

            try
            {
                var enumerationOptions = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                };

                var archivos = dirInfo.EnumerateFiles("*.*", enumerationOptions)
                                      .Where(f => VideoExtensions.Contains(f.Extension));

                foreach (var fileInfo in archivos)
                {
                    var nombreSinExtension = Path.GetFileNameWithoutExtension(fileInfo.Name);
                    int numero = ExtraerNumeroEpisodio(nombreSinExtension);

                    var item = new EpisodioItem
                    {
                        TituloArchivo = nombreSinExtension,
                        RutaCompleta = fileInfo.FullName,
                        NumeroEpisodio = numero
                    };
                    item.CalcularTamanoArchivo(fileInfo.Length);
                    lista.Add(item);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("FileScannerService", $"Error al escanear episodios en {carpeta}", ex);
            }

            return lista.OrderBy(x => x.NumeroEpisodio).ToList();
        });
    }

    public static int ExtraerNumeroEpisodio(string nombre)
    {
        var parsed = Native.NativeMethods.ParseFilename(nombre);
        if (parsed != null && !string.IsNullOrWhiteSpace(parsed.EpisodeNumber))
        {
            if (int.TryParse(parsed.EpisodeNumber, out int ep))
            {
                if (ep is not 480 and not 720 and not 1080 and not 2160)
                {
                    return ep;
                }
            }
        }

        return 0;
    }
}