using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

public class FileScannerService
{
    public async Task<List<EpisodioItem>> EscanearEpisodiosAsync(string carpeta)
    {
        return await Task.Run(() =>
        {
            var lista = new List<EpisodioItem>();
            if (!Directory.Exists(carpeta)) return lista;

            // Buscamos archivos .mkv, .mp4, .avi
            var archivos = Directory.EnumerateFiles(carpeta, "*.*", SearchOption.AllDirectories)
                                    .Where(s => s.EndsWith(".mkv") || s.EndsWith(".mp4") || s.EndsWith(".avi"));

            foreach (var archivo in archivos)
            {
                var nombre = Path.GetFileNameWithoutExtension(archivo);
                
                // Regex simple: busca números de 1 a 3 dígitos que estén solos
                // Ajustaremos esto conforme veas qué archivos te detecta
                var match = Regex.Match(nombre, @"(?<!\d)(\d{1,3})(?!\d)");
                
                int numero = match.Success ? int.Parse(match.Groups[1].Value) : 0;

                lista.Add(new EpisodioItem {
                    TituloArchivo = nombre,
                    RutaCompleta = archivo,
                    NumeroEpisodio = numero
                });
            }
            return lista.OrderBy(x => x.NumeroEpisodio).ToList();
        });
    }
}