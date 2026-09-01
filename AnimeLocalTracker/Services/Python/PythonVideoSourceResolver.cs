using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Services.Python
{
    public class PythonVideoSourceResolver : IVideoSourceResolver
    {
        private readonly IPythonBridgeService _pythonBridge;
        private readonly AnimeAv1VideoSourceResolver _fallbackResolver;

        public PythonVideoSourceResolver(IPythonBridgeService pythonBridge, IHttpClientFactory httpClientFactory)
        {
            _pythonBridge = pythonBridge;
            _fallbackResolver = new AnimeAv1VideoSourceResolver(httpClientFactory.CreateClient("Downloader"));
        }

        public async Task<string?> BuscarUrlEpisodioAsync(IEnumerable<string> titulos, int numeroEpisodio, CancellationToken cancellationToken = default)
        {
            // Delegamos la búsqueda del catálogo/slugs al resolver estándar de AnimeAV1
            return await _fallbackResolver.BuscarUrlEpisodioAsync(titulos, numeroEpisodio, cancellationToken);
        }

        public async Task<string?> GetVideoUrlAsync(string pageUrl, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(pageUrl)) return null;

            try
            {
                // 1. Intentar resolver con yt-dlp a través del bridge de Python
                if (await _pythonBridge.IsAvailableAsync())
                {
                    var result = await _pythonBridge.ExecuteCommandAsync<object, StreamResult>(
                        "resolve-stream",
                        new { url = pageUrl },
                        cancellationToken
                    );

                    if (result != null && result.Success && !string.IsNullOrEmpty(result.DirectUrl))
                    {
                        // Hardening INT-01: el resultado de yt-dlp también pasa la
                        // política https (si no, se cae al fallback C# validado).
                        if (Core.UrlSeguridad.EsUrlDescargaHttpSegura(result.DirectUrl))
                        {
                            AppLogger.Info("PythonVideoResolver", $"Stream resuelto exitosamente con yt-dlp: {SanitizarUrlParaLog(result.DirectUrl)}");
                            return result.DirectUrl;
                        }
                        AppLogger.Warn("PythonVideoResolver", "Stream de yt-dlp rechazado (URL no https). Usando fallback C#.");
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("PythonVideoResolver", $"Fallo en extracción con Python: {ex.Message}. Intentando fallback nativo C#.");
            }

            // 2. Fallback al extractor interno en C#
            return await _fallbackResolver.GetVideoUrlAsync(pageUrl, cancellationToken);
        }

        private static string SanitizarUrlParaLog(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "(vacía)";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "(url no parseable)";
            return $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}";
        }

        private class StreamResult
        {
            public bool Success { get; set; }
            public string? Title { get; set; }
            public string? DirectUrl { get; set; }
            public string? Error { get; set; }
        }
    }
}
