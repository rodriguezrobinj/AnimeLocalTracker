using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Services.Python;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Proveedor AnimeAV1 (Fase A multi-fuente): resuelve episodios usando los embeds
/// multi-servidor de la página (MP4Upload directo con el extractor C# y
/// Voe/UPNShare/HLS/Byse vía yt-dlp en el daemon; Mega queda fuera de alcance).
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

    public async Task<string?> BuscarUrlEpisodioAsync(IEnumerable<string> titulos, int numeroEpisodio, int? aniListId = null, CancellationToken ct = default)
    {
        // La página del episodio publica los embeds de HLS, UPNShare, Voe, Byse,
        // Mega y MP4Upload. Se prueban en orden de preferencia; Mega se omite.
        // El AniListId se usa para verificar el MAL ID de la página (anti-confusión
        // entre animes con nombres parecidos).
        var embeds = await _resolver.ObtenerEmbedsEpisodioAsync(titulos, numeroEpisodio, aniListId, ct);
        var ordenados = AnimeAv1HtmlParser.OrdenarEmbedsPorPreferencia(embeds);
        if (ordenados.Count == 0) return null;

        foreach (var embed in ordenados)
        {
            if (ct.IsCancellationRequested) return null;

            try
            {
                if (embed.Server.Equals("MP4Upload", StringComparison.OrdinalIgnoreCase))
                {
                    // Extractor directo probado (C#): embed → player → src
                    var directo = await _resolver.GetVideoUrlAsync(embed.Url, ct);
                    if (Core.UrlSeguridad.EsUrlVideoPermitida(directo))
                    {
                        AppLogger.Info("ProveedorVideoAnimeAv1", $"Episodio resuelto vía MP4Upload: {SanitizarUrlParaLog(directo)}");
                        return directo;
                    }
                    AppLogger.Debug("ProveedorVideoAnimeAv1", "Servidor 'MP4Upload' sin video directo en el player (embed roto o 'undef' del sitio).");
                    continue;
                }

                // Servidores que resuelve yt-dlp (HLS, Voe, UPNShare, Byse)
                if (!await _pythonBridge.IsAvailableAsync())
                {
                    AppLogger.Debug("ProveedorVideoAnimeAv1", $"Daemon Python no disponible; se omite '{embed.Server}'.");
                    continue;
                }

                var result = await _pythonBridge.ExecuteCommandOneShotAsync<object, StreamResult>(
                    "resolve-stream",
                    new { url = embed.Url },
                    ct);

                if (result == null)
                {
                    AppLogger.Warn("ProveedorVideoAnimeAv1", $"Servidor '{embed.Server}' sin respuesta del daemon (resolver o URL no soportada).");
                    continue;
                }
                if (!result.Success)
                {
                    AppLogger.Info("ProveedorVideoAnimeAv1", $"Servidor '{embed.Server}' falló en el daemon: {result.Error}");
                    continue;
                }
                if (!Core.UrlSeguridad.EsUrlDescargaHttpSegura(result.DirectUrl))
                {
                    AppLogger.Warn("ProveedorVideoAnimeAv1", $"Servidor '{embed.Server}' devolvió URL no segura: {SanitizarUrlParaLog(result.DirectUrl)}");
                    continue;
                }

                // Los manifiestos HLS/DASH se descargan con el daemon
                // (download-stream con yt-dlp segmentado)
                if (Core.UrlSeguridad.EsUrlManifiestoStreaming(result.DirectUrl))
                {
                    AppLogger.Info("ProveedorVideoAnimeAv1", $"Episodio resuelto como HLS/DASH ({embed.Server}); se descargará con el daemon.");
                    return result.DirectUrl;
                }

                AppLogger.Info("ProveedorVideoAnimeAv1", $"Episodio resuelto con yt-dlp ({embed.Server}): {SanitizarUrlParaLog(result.DirectUrl)}");
                return result.DirectUrl;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("ProveedorVideoAnimeAv1", $"Servidor '{embed.Server}' falló con excepción: {ex.Message}");
            }
        }

        return null;
    }

    public async Task<string?> GetVideoUrlAsync(string pageUrl, CancellationToken ct = default)
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
                    ct
                );

                if (result == null)
                {
                    AppLogger.Warn("ProveedorVideoAnimeAv1", "Daemon sin respuesta para la página; usando fallback C#.");
                }
                else if (!result.Success)
                {
                    AppLogger.Info("ProveedorVideoAnimeAv1", $"Daemon no pudo resolver la página: {result.Error}");
                }
                else if (!Core.UrlSeguridad.EsUrlDescargaHttpSegura(result.DirectUrl))
                {
                    AppLogger.Warn("ProveedorVideoAnimeAv1", "Stream de yt-dlp rechazado (URL no https). Usando fallback C#.");
                }
                else
                {
                    AppLogger.Info("ProveedorVideoAnimeAv1", $"Stream resuelto exitosamente con yt-dlp: {SanitizarUrlParaLog(result.DirectUrl)}");
                    return result.DirectUrl;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("ProveedorVideoAnimeAv1", $"Fallo en extracción con Python: {ex.Message}. Intentando fallback nativo C#.");
        }

        // 2. Fallback al extractor interno en C# (solo domina sus propios hosts)
        return await _resolver.GetVideoUrlAsync(pageUrl, ct);
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
