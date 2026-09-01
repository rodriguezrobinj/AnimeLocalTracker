using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Services.Python;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Proveedor AnimeAV1 (Fase A multi-fuente): resuelve episodios usando los embeds
/// multi-servidor de la pÃ¡gina (MP4Upload directo con el extractor C# y
/// Voe/UPNShare/HLS/Byse vÃ­a yt-dlp en el daemon; Mega queda fuera de alcance).
/// Es un IProveedorVideo intercambiable dentro del orquestador.
/// </summary>
public class ProveedorVideoAnimeAv1 : IProveedorVideo
{
    private readonly IPythonBridgeService _pythonBridge;
    private readonly AnimeAv1VideoSourceResolver _resolver;

    public string Nombre => "AnimeAV1";

    public ProveedorVideoAnimeAv1(IPythonBridgeService pythonBridge, AnimeAv1VideoSourceResolver resolver)
    {
        _pythonBridge = pythonBridge;
        _resolver = resolver;
    }

    public async Task<string?> BuscarUrlEpisodioAsync(IEnumerable<string> titulos, int numeroEpisodio, CancellationToken ct = default)
    {
        // La pÃ¡gina del episodio publica los embeds de HLS, UPNShare, Voe, Byse,
        // Mega y MP4Upload. Se prueban en orden de preferencia; Mega se omite.
        var embeds = await _resolver.ObtenerEmbedsEpisodioAsync(titulos, numeroEpisodio, ct);
        var ordenados = AnimeAv1HtmlParser.OrdenarEmbedsPorPreferencia(embeds);
        if (ordenados.Count == 0) return null;

        foreach (var embed in ordenados)
        {
            if (ct.IsCancellationRequested) return null;

            try
            {
                if (embed.Server.Equals("MP4Upload", StringComparison.OrdinalIgnoreCase))
                {
                    // Extractor directo probado (C#): embed â†’ player â†’ src
                    var directo = await _resolver.GetVideoUrlAsync(embed.Url, ct);
                    if (Core.UrlSeguridad.EsUrlVideoPermitida(directo))
                    {
                        AppLogger.Info("ProveedorVideoAnimeAv1", $"Episodio resuelto vÃ­a MP4Upload: {SanitizarUrlParaLog(directo)}");
                        return directo;
                    }
                    continue;
                }

                // Servidores que resuelve yt-dlp (Voe, UPNShare, HLS, Byse)
                if (!await _pythonBridge.IsAvailableAsync()) continue;

                var result = await _pythonBridge.ExecuteCommandAsync<object, StreamResult>(
                    "resolve-stream",
                    new { url = embed.Url },
                    ct);

                if (result != null && result.Success && Core.UrlSeguridad.EsUrlDescargaHttpSegura(result.DirectUrl))
                {
                    // Los manifiestos HLS/DASH se descargan con el daemon
                    // (download-stream con yt-dlp segmentado)
                    if (Core.UrlSeguridad.EsUrlManifiestoStreaming(result.DirectUrl))
                    {
                        AppLogger.Info("ProveedorVideoAnimeAv1", $"Episodio resuelto como HLS/DASH ({embed.Server}); se descargarÃ¡ con el daemon.");
                        return result.DirectUrl;
                    }

                    AppLogger.Info("ProveedorVideoAnimeAv1", $"Episodio resuelto con yt-dlp ({embed.Server}): {SanitizarUrlParaLog(result.DirectUrl)}");
                    return result.DirectUrl;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug("ProveedorVideoAnimeAv1", $"Servidor '{embed.Server}' fallÃ³: {ex.Message}");
            }
        }

        return null;
    }

    public async Task<string?> GetVideoUrlAsync(string pageUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pageUrl)) return null;

        try
        {
            // 1. Intentar resolver con yt-dlp a travÃ©s del bridge de Python
            if (await _pythonBridge.IsAvailableAsync())
            {
                var result = await _pythonBridge.ExecuteCommandAsync<object, StreamResult>(
                    "resolve-stream",
                    new { url = pageUrl },
                    ct
                );

                if (result != null && result.Success && !string.IsNullOrEmpty(result.DirectUrl))
                {
                    // Hardening INT-01: el resultado de yt-dlp tambiÃ©n pasa la
                    // polÃ­tica https (si no, se cae al fallback C# validado).
                    if (Core.UrlSeguridad.EsUrlDescargaHttpSegura(result.DirectUrl))
                    {
                        AppLogger.Info("ProveedorVideoAnimeAv1", $"Stream resuelto exitosamente con yt-dlp: {SanitizarUrlParaLog(result.DirectUrl)}");
                        return result.DirectUrl;
                    }
                    AppLogger.Warn("ProveedorVideoAnimeAv1", "Stream de yt-dlp rechazado (URL no https). Usando fallback C#.");
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("ProveedorVideoAnimeAv1", $"Fallo en extracciÃ³n con Python: {ex.Message}. Intentando fallback nativo C#.");
        }

        // 2. Fallback al extractor interno en C# (solo domina sus propios hosts)
        return await _resolver.GetVideoUrlAsync(pageUrl, ct);
    }

    private static string SanitizarUrlParaLog(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "(vacÃ­a)";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "(url no parseable)";
        return $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}";
    }

    /// <summary>DTO del resultado del daemon (resolve-stream). PÃºblico para testeo.</summary>
    public class StreamResult
    {
        public bool Success { get; set; }
        public string? Title { get; set; }
        public string? DirectUrl { get; set; }
        public string? Error { get; set; }
    }
}
