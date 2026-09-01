using System;
using System.Collections.Generic;
using System.Linq;
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
            // Fase 1 multi-servidor: la página del episodio publica los embeds de
            // HLS, UPNShare, Voe, Byse, Mega y MP4Upload. Se prueban en orden de
            // preferencia; Mega se omite (requiere API propia de mega.nz).
            var embeds = await _fallbackResolver.ObtenerEmbedsEpisodioAsync(titulos, numeroEpisodio, cancellationToken);
            var ordenados = AnimeAv1HtmlParser.OrdenarEmbedsPorPreferencia(embeds);
            if (ordenados.Count == 0) return null;

            foreach (var embed in ordenados)
            {
                if (cancellationToken.IsCancellationRequested) return null;

                try
                {
                    if (embed.Server.Equals("MP4Upload", StringComparison.OrdinalIgnoreCase))
                    {
                        // Extractor directo probado (C#): embed → player → src
                        var directo = await _fallbackResolver.GetVideoUrlAsync(embed.Url, cancellationToken);
                        if (Core.UrlSeguridad.EsUrlVideoPermitida(directo))
                        {
                            AppLogger.Info("PythonVideoResolver", $"Episodio resuelto vía MP4Upload: {SanitizarUrlParaLog(directo)}");
                            return directo;
                        }
                        continue;
                    }

                    // Servidores que resuelve yt-dlp (Voe, UPNShare, HLS, Byse)
                    if (!await _pythonBridge.IsAvailableAsync()) continue;

                    var result = await _pythonBridge.ExecuteCommandAsync<object, StreamResult>(
                        "resolve-stream",
                        new { url = embed.Url },
                        cancellationToken);

                    if (result != null && result.Success && Core.UrlSeguridad.EsUrlDescargaHttpSegura(result.DirectUrl))
                    {
                        // Los manifiestos HLS/DASH se descargan con el daemon
                        // (fase 2: download-stream con yt-dlp segmentado)
                        if (Core.UrlSeguridad.EsUrlManifiestoStreaming(result.DirectUrl))
                        {
                            AppLogger.Info("PythonVideoResolver", $"Episodio resuelto como HLS/DASH ({embed.Server}); se descargará con el daemon.");
                            return result.DirectUrl;
                        }

                        AppLogger.Info("PythonVideoResolver", $"Episodio resuelto con yt-dlp ({embed.Server}): {SanitizarUrlParaLog(result.DirectUrl)}");
                        return result.DirectUrl;
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("PythonVideoResolver", $"Servidor '{embed.Server}' falló: {ex.Message}");
                }
            }

            return null;
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

        private static string SanitizarUrlParaLog(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "(vacía)";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "(url no parseable)";
            return $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}";
        }

        /// <summary>DTO del resultado del daemon (resolve-stream). Público para testeo.</summary>
        public class StreamResult
        {
            public bool Success { get; set; }
            public string? Title { get; set; }
            public string? DirectUrl { get; set; }
            public string? Error { get; set; }
        }
    }
}
