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
                            AppLogger.Debug("PythonFileScanner", $"No se pudo eliminar residual '{res.FullName}': {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("PythonFileScanner", $"Error al limpiar residuales en {carpeta}: {ex.Message}");
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

                    // 2. Si quedaron archivos con nombres crípticos o complejos sin número detectado:
                    // Prioridad 1: Motor nativo Rust FFI (0.001 ms, 0 MB RAM)
                    // Prioridad 2: Demonio Python Anitopy (Fallback)
                    if (noReconocidos.Count > 0)
                    {
                        var filenames = noReconocidos.Select(x => x.File.Name).ToList();

                        if (Native.NativeMethods.IsAvailable)
                        {
                            try
                            {
                                var rustResults = Native.NativeMethods.ParseBatch(filenames);
                                for (int i = 0; i < Math.Min(noReconocidos.Count, rustResults.Count); i++)
                                {
                                    var r = rustResults[i];
                                    if (!string.IsNullOrWhiteSpace(r.EpisodeNumber) &&
                                        int.TryParse(r.EpisodeNumber, out int epNum) && epNum > 0)
                                    {
                                        noReconocidos[i].Item.NumeroEpisodio = epNum;
                                    }
                                }
                            }
                            catch (Exception rustEx)
                            {
                                AppLogger.Debug("PythonFileScanner", $"Fallo en parser Rust nativo, intentando fallback Python: {rustEx.Message}");
                            }
                        }

                        // Reintentar con Python para los que aún sigan con episodio 0
                        var aunNoReconocidos = noReconocidos.Where(x => x.Item.NumeroEpisodio == 0).ToList();
                        if (aunNoReconocidos.Count > 0 && await _pythonBridge.IsAvailableAsync())
                        {
                            var pendingNames = aunNoReconocidos.Select(x => x.File.Name).ToList();
                            var batchResult = await _pythonBridge.ExecuteCommandAsync<object, BatchParseResult>(
                                "parse-batch",
                                new { filenames = pendingNames, directory_context = dirInfo.Name }
                            );

                            if (batchResult != null && batchResult.Success && batchResult.Results != null)
                            {
                                for (int i = 0; i < Math.Min(aunNoReconocidos.Count, batchResult.Results.Count); i++)
                                {
                                    var parsed = batchResult.Results[i];
                                    if (parsed.EpisodeNumber.HasValue && parsed.EpisodeNumber.Value > 0)
                                    {
                                        aunNoReconocidos[i].Item.NumeroEpisodio = parsed.EpisodeNumber.Value;
                                    }
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
