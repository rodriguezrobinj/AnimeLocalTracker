using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services.Python
{
    public class PythonFileScannerService : IFileScannerService
    {
        private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mkv", ".mp4", ".avi", ".webm" };
        private readonly IPythonBridgeService _pythonBridge;

        public PythonFileScannerService(IPythonBridgeService pythonBridge)
        {
            _pythonBridge = pythonBridge;
        }

        public async Task<List<EpisodioItem>> EscanearEpisodiosAsync(string carpeta)
        {
            return await Task.Run(async () =>
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
                        try { res.Delete(); } catch (Exception ex) { AppLogger.Warn("PythonFileScanner", $"No se pudo eliminar residual '{res.FullName}': {ex.Message}"); }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("PythonFileScanner", $"Error al limpiar residuales en {carpeta}: {ex.Message}");
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
                                          .Where(f => VideoExtensions.Contains(f.Extension))
                                          .ToList();

                    var noReconocidos = new List<(FileInfo File, EpisodioItem Item)>();

                    // 1. Fase ultrarrápida en memoria usando Regex nativo (0ms)
                    foreach (var fileInfo in archivos)
                    {
                        string nombreSinExtension = Path.GetFileNameWithoutExtension(fileInfo.Name);
                        int numero = FileScannerService.ExtraerNumeroEpisodio(nombreSinExtension);

                        var item = new EpisodioItem
                        {
                            TituloArchivo = nombreSinExtension,
                            RutaCompleta = fileInfo.FullName,
                            NumeroEpisodio = numero
                        };
                        item.CalcularTamanoArchivo(fileInfo.Length);
                        lista.Add(item);

                        if (numero == 0)
                        {
                            noReconocidos.Add((fileInfo, item));
                        }
                    }

                    // 2. Si quedaron archivos con nombres crípticos o complejos sin número detectado,
                    // enviamos una ÚNICA llamada batch a Python/Anitopy para resolverlos de golpe
                    if (noReconocidos.Count > 0 && await _pythonBridge.IsAvailableAsync())
                    {
                        var filenames = noReconocidos.Select(x => x.File.Name).ToList();
                        var batchResult = await _pythonBridge.ExecuteCommandAsync<object, BatchParseResult>(
                            "parse-batch",
                            new { filenames, directory_context = dirInfo.Name }
                        );

                        if (batchResult != null && batchResult.Success && batchResult.Results != null)
                        {
                            for (int i = 0; i < Math.Min(noReconocidos.Count, batchResult.Results.Count); i++)
                            {
                                var parsed = batchResult.Results[i];
                                if (parsed.EpisodeNumber.HasValue && parsed.EpisodeNumber.Value > 0)
                                {
                                    noReconocidos[i].Item.NumeroEpisodio = parsed.EpisodeNumber.Value;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Error("PythonFileScanner", $"Error al escanear episodios en {carpeta}", ex);
                }

                return lista.OrderBy(x => x.NumeroEpisodio).ToList();
            });
        }

        private class BatchParseResult
        {
            public bool Success { get; set; }
            public List<ParseResult>? Results { get; set; }
        }

        private class ParseResult
        {
            public bool Success { get; set; }
            public string? AnimeTitle { get; set; }
            public int? EpisodeNumber { get; set; }
            public int? SeasonNumber { get; set; }
            public string? ReleaseGroup { get; set; }
            public string? VideoResolution { get; set; }
        }
    }
}
