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

            // Limpiar archivos residuales incompletos de descargas interrumpidas
            try
            {
                var residuales = Directory.EnumerateFiles(carpeta, "*.*", new EnumerationOptions { RecurseSubdirectories = true })
                    .Where(f => f.EndsWith(".downloading", System.StringComparison.OrdinalIgnoreCase) || 
                                f.EndsWith(".tmp", System.StringComparison.OrdinalIgnoreCase) || 
                                f.EndsWith(".part", System.StringComparison.OrdinalIgnoreCase));
                foreach (var res in residuales)
                {
                    try { File.Delete(res); } catch { }
                }
            }
            catch { }

            // Buscamos archivos .mkv, .mp4, .avi de forma nativa en el sistema (10x más rápido)
            var extensiones = new[] { ".mkv", ".mp4", ".avi" };
            var archivos = Directory.EnumerateFiles(carpeta, "*.*", new EnumerationOptions { RecurseSubdirectories = true })
                                    .Where(f => extensiones.Contains(Path.GetExtension(f).ToLower()));

            foreach (var archivo in archivos)
            {
                var nombre = Path.GetFileNameWithoutExtension(archivo);
                
                // Regex simple: busca números de 1 a 3 dígitos que estén solos
                // Ajustaremos esto conforme veas qué archivos te detecta
                var match = Regex.Match(nombre, @"(?<!\d)(\d{1,3})(?!\d)");
                
                int numero = match.Success ? int.Parse(match.Groups[1].Value) : 0;

                var item = new EpisodioItem {
                    TituloArchivo = nombre,
                    RutaCompleta = archivo,
                    NumeroEpisodio = numero
                };
                item.CalcularTamanoArchivo();
                lista.Add(item);
            }
            return lista.OrderBy(x => x.NumeroEpisodio).ToList();
        });
    }
}