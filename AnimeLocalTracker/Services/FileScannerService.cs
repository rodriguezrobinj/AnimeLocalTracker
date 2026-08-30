using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

public partial class FileScannerService : IFileScannerService
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mkv", ".mp4", ".avi" };

    [GeneratedRegex(@"(?:\b(?:E|EP|Episode|Episodio|Cap|Capitulo)[\s._-]*|[\[\(-])(\d{1,4})(?:[\]\)-]|\b|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PatronExplicitoRegex();

    [GeneratedRegex(@"(?<!\d)(\d{1,4})(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex PatronGenericoRegex();

    public async Task<List<EpisodioItem>> EscanearEpisodiosAsync(string carpeta)
    {
        return await Task.Run(() =>
        {
            var lista = new List<EpisodioItem>();
            if (!Directory.Exists(carpeta)) return lista;

            var dirInfo = new DirectoryInfo(carpeta);

            // Limpiar archivos residuales incompletos de descargas interrumpidas propias
            try
            {
                var residuales = dirInfo.EnumerateFiles("Episodio *.downloading", SearchOption.TopDirectoryOnly);
                foreach (var res in residuales)
                {
                    try
                    {
                        res.Delete();
                    }
                    catch (IOException)
                    {
                        // Archivo en uso por una descarga activa actual: omitir limpiamente
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Debug("FileScannerService", $"No se pudo eliminar archivo residual '{res.FullName}': {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug("FileScannerService", $"Error al limpiar residuales en {carpeta}: {ex.Message}");
            }

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
        // 1. Patrón explícito de episodio: "Ep 05", "E05", "Episode 05", "Episodio 05", "Cap 05"
        var matchExplicito = PatronExplicitoRegex().Match(nombre);
        if (matchExplicito.Success && int.TryParse(matchExplicito.Groups[1].Value, out int epExp))
        {
            if (epExp is not 480 and not 720 and not 1080 and not 2160)
                return epExp;
        }

        // 2. Patrón genérico: busca números de 1 a 4 dígitos excluyendo resoluciones comunes
        var matches = PatronGenericoRegex().Matches(nombre);
        foreach (Match m in matches)
        {
            if (int.TryParse(m.Groups[1].Value, out int num))
            {
                if (num is not 480 and not 720 and not 1080 and not 2160)
                    return num;
            }
        }

        return 0;
    }
}