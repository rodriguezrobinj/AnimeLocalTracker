using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

public partial class FileScannerService : IFileScannerService
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mkv", ".mp4", ".avi", ".webm" };

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

    /// <summary>
    /// Número de episodio según el núcleo Rust (anitomy), o 0 si el nombre no lo dice. Es el único criterio: antes, lo que
    /// Rust no resolvía pasaba al daemon Python (anitopy + regex), que sobre 182 nombres reales de fansub con el episodio
    /// anotado no acertó ninguno de más y falló 26 (una película "Movie 9" pasaba a ser el episodio 9; un especial "06.5",
    /// un segundo episodio 6). Un especial con decimales se queda sin número a propósito.
    /// Lo único que el núcleo añade a anitomy es el número seguido del idioma ("Anime 12 Latino"), en parser.rs.
    /// No se descartan 480/720/1080/2160: "1080p" ya lo reconoce anitomy como resolución, y un 1080 a secas es un episodio
    /// ("Episodio 1080" de una serie larga).
    /// </summary>
    public static int ExtraerNumeroEpisodio(string nombre)
    {
        var parsed = Native.NativeMethods.ParseFilename(nombre);
        return int.TryParse(parsed?.EpisodeNumber, out int ep) && ep > 0 ? ep : 0;
    }
}