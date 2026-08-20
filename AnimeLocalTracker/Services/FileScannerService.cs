using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

public class FileScannerService : IFileScannerService
{
    public async Task<List<EpisodioItem>> EscanearEpisodiosAsync(string carpeta)
    {
        return await Task.Run(() =>
        {
            var lista = new List<EpisodioItem>();
            if (!Directory.Exists(carpeta)) return lista;

            // Limpiar archivos residuales incompletos de descargas interrumpidas propias
            try
            {
                var residuales = Directory.EnumerateFiles(carpeta, "Episodio *.downloading", SearchOption.TopDirectoryOnly);
                foreach (var res in residuales)
                {
                    try { File.Delete(res); } catch { }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("FileScannerService", $"Error al limpiar residuales en {carpeta}: {ex.Message}");
            }

            try
            {
                // Buscamos archivos de video comunes de forma nativa en el sistema
                var extensiones = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mkv", ".mp4", ".avi" };
                var archivos = Directory.EnumerateFiles(carpeta, "*.*", new EnumerationOptions { RecurseSubdirectories = true })
                                        .Where(f => extensiones.Contains(Path.GetExtension(f)));

                foreach (var archivo in archivos)
                {
                    var nombre = Path.GetFileNameWithoutExtension(archivo);
                    int numero = ExtraerNumeroEpisodio(nombre);

                    var item = new EpisodioItem {
                        TituloArchivo = nombre,
                        RutaCompleta = archivo,
                        NumeroEpisodio = numero
                    };
                    item.CalcularTamanoArchivo();
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

    private static int ExtraerNumeroEpisodio(string nombre)
    {
        // 1. Patrón explícito de episodio: "Ep 05", "E05", "Episode 05", "Episodio 05", "Cap 05"
        var matchExplicito = Regex.Match(nombre, @"(?:\b(?:E|EP|Episode|Episodio|Cap|Capitulo)[\s._-]*|[\[\(-])(\d{1,3})(?:[\]\)-]|\b|$)", RegexOptions.IgnoreCase);
        if (matchExplicito.Success && int.TryParse(matchExplicito.Groups[1].Value, out int epExp))
        {
            if (epExp is not 480 and not 720 and not 1080 and not 2160)
                return epExp;
        }

        // 2. Patrón genérico: busca números de 1 a 3 dígitos excluyendo resoluciones comunes
        var matches = Regex.Matches(nombre, @"(?<!\d)(\d{1,3})(?!\d)");
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