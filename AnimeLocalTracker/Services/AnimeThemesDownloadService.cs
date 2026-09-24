using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Descarga el .ogg (audio solo, sin video) de un tema de AnimeThemes.moe y lo convierte a .mp3
/// con el FFmpeg embebido de la app (mismo binario que usa VideoIntegrityService/PythonEpisodeEnricher;
/// trae el codificador libmp3lame incluido). El .ogg nunca queda en disco tras el intento: solo se
/// guarda el .mp3 final.
/// </summary>
public class AnimeThemesDownloadService : IAnimeThemesDownloadService
{
    private readonly IHttpClientFactory _httpClientFactory;

    // Preferir el ffmpeg embebido (carpeta FFmpeg/ del output); si no está, caer al del PATH,
    // mismo criterio que VideoIntegrityService.RutaFfprobe.
    private static readonly Lazy<string> RutaFfmpeg = new(() =>
    {
        string embebido = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FFmpeg", "ffmpeg.exe");
        return File.Exists(embebido) ? embebido : "ffmpeg";
    });

    public AnimeThemesDownloadService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    private static string CarpetaAnime(int aniListId) => Path.Combine(AppDataPaths.MusicDir, aniListId.ToString());

    public string ObtenerRutaLocalEsperada(int aniListId, AnimeThemeInfo tema) =>
        Path.Combine(CarpetaAnime(aniListId), tema.NombreArchivoLocal());

    public bool EstaDescargado(int aniListId, AnimeThemeInfo tema) =>
        File.Exists(ObtenerRutaLocalEsperada(aniListId, tema));

    public async Task<string?> DescargarYConvertirAsync(int aniListId, AnimeThemeInfo tema, CancellationToken ct = default)
    {
        string carpeta = CarpetaAnime(aniListId);
        string rutaFinal = ObtenerRutaLocalEsperada(aniListId, tema);
        string rutaTemporalOgg = Path.Combine(carpeta, Path.GetFileNameWithoutExtension(rutaFinal) + ".ogg.tmp");

        try
        {
            Directory.CreateDirectory(carpeta);

            var http = _httpClientFactory.CreateClient("Downloader");
            using (var respuesta = await http.GetAsync(tema.AudioUrlOgg, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                respuesta.EnsureSuccessStatusCode();
                await using var origen = await respuesta.Content.ReadAsStreamAsync(ct);
                await using var destino = File.Create(rutaTemporalOgg);
                await origen.CopyToAsync(destino, ct);
            }

            bool convertido = await ConvertirAMp3Async(rutaTemporalOgg, rutaFinal, ct);
            return convertido ? rutaFinal : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            AppLogger.Debug("AnimeThemesDownloadService", $"Error descargando/convirtiendo '{tema.Slug}': {ex.Message}");
            return null;
        }
        finally
        {
            try { if (File.Exists(rutaTemporalOgg)) File.Delete(rutaTemporalOgg); } catch { /* best-effort */ }
        }
    }

    private static async Task<bool> ConvertirAMp3Async(string rutaOgg, string rutaMp3Destino, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = RutaFfmpeg.Value,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            // -hide_banner -loglevel error: sin esto, ffmpeg vuelca bastante texto a stderr (banner +
            // progreso) que, al no leerse, llena el buffer del pipe redirigido y CUELGA el proceso para
            // siempre esperando un lector — WaitForExitAsync nunca vuelve (comprobado en vivo: quedó un
            // ffmpeg.exe zombi). -q:a 2 = VBR ~190kbps, calidad suficiente para un OP/ED.
            foreach (var arg in new[] { "-y", "-hide_banner", "-loglevel", "error", "-i", rutaOgg, "-codec:a", "libmp3lame", "-q:a", "2", rutaMp3Destino })
            {
                psi.ArgumentList.Add(arg);
            }

            using var proceso = Process.Start(psi);
            if (proceso == null) return false;

            // Defensa adicional: aunque -loglevel error ya debería dejar stderr casi vacío, drenar
            // ambos streams en paralelo evita el mismo deadlock si algún día vuelve a haber salida
            // inesperada (advertencias, etc.) — nunca dejar un pipe redirigido sin lector.
            var salidaEstandar = proceso.StandardOutput.ReadToEndAsync(ct);
            var salidaError = proceso.StandardError.ReadToEndAsync(ct);
            await proceso.WaitForExitAsync(ct);
            await Task.WhenAll(salidaEstandar, salidaError);

            return proceso.ExitCode == 0 && File.Exists(rutaMp3Destino) && new FileInfo(rutaMp3Destino).Length > 0;
        }
        catch (Exception ex)
        {
            AppLogger.Debug("AnimeThemesDownloadService", $"Error convirtiendo a mp3: {ex.Message}");
            return false;
        }
    }

    public void Eliminar(int aniListId, AnimeThemeInfo tema)
    {
        try
        {
            string ruta = ObtenerRutaLocalEsperada(aniListId, tema);
            if (File.Exists(ruta)) File.Delete(ruta);
        }
        catch (Exception ex)
        {
            AppLogger.Debug("AnimeThemesDownloadService", $"No se pudo borrar '{tema.Slug}': {ex.Message}");
        }
    }

    public List<TemaLocalDisponible> ListarDescargasLocales(int aniListId)
    {
        var resultado = new List<TemaLocalDisponible>();
        string carpeta = CarpetaAnime(aniListId);
        if (!Directory.Exists(carpeta)) return resultado;

        try
        {
            foreach (var archivo in Directory.GetFiles(carpeta, "*.mp3"))
            {
                if (TryParseNombreArchivo(archivo, out var tema) && tema != null)
                {
                    resultado.Add(tema);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("AnimeThemesDownloadService", $"Error listando descargas locales de AniListId {aniListId}: {ex.Message}");
        }
        return resultado;
    }

    /// <summary>Reconstruye tipo/slug/versión/rango a partir de un nombre generado por
    /// <see cref="AnimeThemeInfo.NombreArchivoLocal"/> (formato: Tipo_Slug_vN_epRango.mp3).</summary>
    internal static bool TryParseNombreArchivo(string rutaArchivo, out TemaLocalDisponible? resultado)
    {
        resultado = null;
        string nombre = Path.GetFileNameWithoutExtension(rutaArchivo);
        var partes = nombre.Split('_', 4);
        if (partes.Length < 4) return false;

        string tipo = partes[0];
        string slug = partes[1];

        if (!partes[2].StartsWith('v') || !int.TryParse(partes[2][1..], out int version)) return false;
        if (!partes[3].StartsWith("ep", StringComparison.Ordinal)) return false;

        string epParte = partes[3]["ep".Length..];
        string? rango = epParte == "todos" ? null : epParte.Replace('_', ',');

        resultado = new TemaLocalDisponible(tipo, slug, version, rango, rutaArchivo);
        return true;
    }
}
