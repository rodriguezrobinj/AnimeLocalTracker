using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Services.Python;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Proveedor AnimeAV1 (Fase A multi-fuente): resuelve episodios usando los embeds
/// multi-servidor de la página (MP4Upload directo con el extractor C# y, como respaldo, TransferIt y Mega con la
/// API de MEGA y Voe con su extractor propio en el daemon; HLS/UPNShare/Byse también pasan por el daemon pero
/// hoy no resuelven).
/// Es un IProveedorVideo intercambiable dentro del orquestador.
/// </summary>
public class ProveedorVideoAnimeAv1 : IProveedorVideo
{
    private readonly IPythonBridgeService _pythonBridge;
    private readonly AnimeAv1VideoSourceResolver _resolver;
    private readonly ResolvedorServidoresVideo _servidores;

    public string Nombre => "AnimeAV1";

    public ProveedorVideoAnimeAv1(IPythonBridgeService pythonBridge, AnimeAv1VideoSourceResolver resolver)
    {
        _pythonBridge = pythonBridge;
        _resolver = resolver;
        _servidores = new ResolvedorServidoresVideo(pythonBridge, resolver);
    }

    public async Task<string?> BuscarUrlEpisodioAsync(IEnumerable<string> titulos, int numeroEpisodio, int? aniListId = null, string? audioPreferido = null, string? servidorPreferido = null, CancellationToken ct = default)
    {
        // La página del episodio publica los embeds de HLS, UPNShare, Voe, Byse y MP4Upload, y en su bloque de
        // descargas TransferIt y Mega. Se prueban en orden de preferencia (ver OrdenarEmbedsPorPreferencia).
        // El AniListId se usa para verificar el MAL ID de la página (anti-confusión
        // entre animes con nombres parecidos).
        var embeds = await _resolver.ObtenerEmbedsEpisodioAsync(titulos, numeroEpisodio, aniListId, ct);
        var ordenados = AnimeAv1HtmlParser.OrdenarEmbedsPorPreferencia(embeds, audioPreferido, servidorPreferido);
        if (ordenados.Count == 0) return null;

        return await _servidores.ResolverPrimeroAsync(ordenados, "https://animeav1.com/", ct);
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
                    AppLogger.Info("ProveedorVideoAnimeAv1", $"Stream resuelto exitosamente con yt-dlp: {ResolvedorServidoresVideo.SanitizarUrlParaLog(result.DirectUrl)}");
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

    /// <summary>DTO del resultado del daemon (resolve-stream). Público para testeo.</summary>
    public class StreamResult
    {
        public bool Success { get; set; }
        public string? Title { get; set; }
        public string? DirectUrl { get; set; }
        public string? Error { get; set; }
    }
}
