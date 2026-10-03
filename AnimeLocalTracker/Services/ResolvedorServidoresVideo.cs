using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Services.Python;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Convierte la lista de servidores de un episodio (ya ordenada por preferencia) en la URL de descarga del primero que
/// resuelva. Es lo común a todos los sitios (AnimeAV1, JKAnime…): MP4Upload con el extractor C#, TransferIt y Mega con la
/// API de MEGA (Mega entrega la URL con la clave para que DownloadService descifre al terminar) y el resto con el daemon
/// (Voe, y HLS/UPNShare/Byse, que hoy no resuelven). Cada sitio solo aporta la lista de servidores.
/// </summary>
public sealed class ResolvedorServidoresVideo(IPythonBridgeService pythonBridge, AnimeAv1VideoSourceResolver resolverHttp)
{
    /// <param name="ordenados">Servidores del episodio en el orden en que se probarán.</param>
    /// <param name="referer">Página del sitio de origen: algunos servidores comprueban desde dónde se abre el reproductor.</param>
    public async Task<string?> ResolverPrimeroAsync(IEnumerable<AnimeAv1HtmlParser.EmbedServidor> ordenados, string referer, CancellationToken ct)
    {
        foreach (var embed in ordenados)
        {
            if (ct.IsCancellationRequested) return null;

            try
            {
                if (embed.Server.Equals("MP4Upload", StringComparison.OrdinalIgnoreCase))
                {
                    // Extractor directo probado (C#): embed → player → src
                    var directo = await resolverHttp.ResolverMp4UploadAsync(embed.Url, referer, ct);
                    if (Core.UrlSeguridad.EsUrlVideoPermitida(directo))
                    {
                        AppLogger.Info("ResolvedorServidoresVideo", $"Episodio resuelto vía MP4Upload: {SanitizarUrlParaLog(directo)}");
                        return directo;
                    }
                    AppLogger.Debug("ResolvedorServidoresVideo", "Servidor 'MP4Upload' sin video directo en el player (embed roto o 'undef' del sitio).");
                    continue;
                }

                // TransferIt y Mega: API de MEGA (C#). TransferIt entrega el archivo en claro; Mega lo entrega cifrado y la URL
                // lleva la clave para que DownloadService lo descifre al terminar.
                bool esTransferIt = embed.Server.Equals("TransferIt", StringComparison.OrdinalIgnoreCase);
                if (esTransferIt || embed.Server.Equals("Mega", StringComparison.OrdinalIgnoreCase))
                {
                    var urlMega = esTransferIt
                        ? await resolverHttp.ResolverTransferItAsync(embed.Url, ct)
                        : await resolverHttp.ResolverMegaAsync(embed.Url, ct);
                    if (Core.UrlSeguridad.EsUrlDescargaHttpSegura(urlMega))
                    {
                        AppLogger.Info("ResolvedorServidoresVideo", $"Episodio resuelto vía {embed.Server}: {SanitizarUrlParaLog(urlMega)}");
                        return urlMega;
                    }
                    AppLogger.Debug("ResolvedorServidoresVideo", $"Servidor '{embed.Server}' sin enlace de descarga utilizable.");
                    continue;
                }

                // Servidores que resuelve el daemon (HLS, Voe, UPNShare, Byse)
                if (!await pythonBridge.IsAvailableAsync())
                {
                    AppLogger.Debug("ResolvedorServidoresVideo", $"Daemon Python no disponible; se omite '{embed.Server}'.");
                    continue;
                }

                var result = await pythonBridge.ExecuteCommandOneShotAsync<object, ProveedorVideoAnimeAv1.StreamResult>(
                    "resolve-stream",
                    new { url = embed.Url, server = embed.Server },
                    ct);

                if (result == null)
                {
                    AppLogger.Warn("ResolvedorServidoresVideo", $"Servidor '{embed.Server}' sin respuesta del daemon (resolver o URL no soportada).");
                    continue;
                }
                if (!result.Success)
                {
                    AppLogger.Info("ResolvedorServidoresVideo", $"Servidor '{embed.Server}' falló en el daemon: {result.Error}");
                    continue;
                }
                if (!Core.UrlSeguridad.EsUrlDescargaHttpSegura(result.DirectUrl))
                {
                    AppLogger.Warn("ResolvedorServidoresVideo", $"Servidor '{embed.Server}' devolvió URL no segura: {SanitizarUrlParaLog(result.DirectUrl)}");
                    continue;
                }

                // Los manifiestos HLS/DASH se descargan con el daemon
                // (download-stream con yt-dlp segmentado)
                if (Core.UrlSeguridad.EsUrlManifiestoStreaming(result.DirectUrl))
                {
                    AppLogger.Info("ResolvedorServidoresVideo", $"Episodio resuelto como HLS/DASH ({embed.Server}); se descargará con el daemon.");
                    return result.DirectUrl;
                }

                AppLogger.Info("ResolvedorServidoresVideo", $"Episodio resuelto con yt-dlp ({embed.Server}): {SanitizarUrlParaLog(result.DirectUrl)}");
                return result.DirectUrl;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("ResolvedorServidoresVideo", $"Servidor '{embed.Server}' falló con excepción: {ex.Message}");
            }
        }

        return null;
    }

    /// <summary>La URL sin consulta ni fragmento (que puede llevar la clave de Mega): es lo único que se escribe en el registro.</summary>
    internal static string SanitizarUrlParaLog(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "(vacía)";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "(url no parseable)";
        return $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}";
    }
}
