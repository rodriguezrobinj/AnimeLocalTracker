using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Audio de referencia de los openings/endings de un anime. <see cref="Temas"/> incluye todo lo disponible en disco (descargas de
/// la Ficha + caché propia) y lo que se bajó ahora para este episodio. <see cref="Completa"/> es false si algún tema que aplica al
/// episodio no se pudo conseguir (sin red, AnimeThemes caído o el anime no está en su catálogo).
/// </summary>
public sealed record ReferenciasEpisodio(IReadOnlyList<TemaLocalDisponible> Temas, bool Completa);

public interface IReferenciasAudioService
{
    /// <summary>
    /// Deja listo el audio oficial de los temas que aplican a este episodio, bajándolo de AnimeThemes si hace falta. Nunca lanza
    /// (salvo cancelación): sin red devuelve lo que ya haya en disco.
    /// </summary>
    Task<ReferenciasEpisodio> ObtenerAsync(int aniListId, int episodio, CancellationToken ct = default);
}

/// <summary>
/// Consigue, sin pasos manuales, el audio de referencia con el que se ubican el opening y el ending dentro de un episodio.
/// Orden: mp3 que el usuario ya descargó desde la Ficha → caché propia (<see cref="AppDataPaths.SkipReferencesDir"/>, el .ogg
/// original sin convertir, ~3 MB por tema) → descarga desde AnimeThemes. La caché tiene un tope de tamaño: al pasarse se borran
/// los archivos menos usados (bajarlos otra vez es barato).
/// </summary>
public sealed class ReferenciasAudioService : IReferenciasAudioService
{
    /// <summary>Tiempo máximo esperando la lista de temas de AnimeThemes (ya estuvo caída): el análisis no se queda colgado.</summary>
    internal static readonly TimeSpan TiempoMaximoApi = TimeSpan.FromSeconds(15);

    internal const long MaximoBytesAudio = 25L * 1024 * 1024;

    private readonly IAnimeThemesService _themes;
    private readonly IAnimeThemesDownloadService _descargas;
    private readonly IHttpClientFactory _httpFactory;
    private readonly string _carpeta;

    /// <summary>Tamaño máximo de la caché de referencias (unos 120 temas). Ajustable solo en pruebas.</summary>
    internal long MaximoBytesCache { get; set; } = 400L * 1024 * 1024;

    /// <param name="carpeta">Solo para pruebas: carpeta de la caché (las pruebas nunca deben escribir en <see cref="AppDataPaths"/>).</param>
    public ReferenciasAudioService(IAnimeThemesService themes, IAnimeThemesDownloadService descargas, IHttpClientFactory httpFactory, string? carpeta = null)
    {
        _themes = themes;
        _descargas = descargas;
        _httpFactory = httpFactory;
        _carpeta = carpeta ?? AppDataPaths.SkipReferencesDir;
    }

    public async Task<ReferenciasEpisodio> ObtenerAsync(int aniListId, int episodio, CancellationToken ct = default)
    {
        var temas = new List<TemaLocalDisponible>();
        AgregarSinRepetir(temas, ListarLocalesDeLaFicha(aniListId));
        AgregarSinRepetir(temas, ListarCache(aniListId));

        bool completa = false;
        try
        {
            using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limite.CancelAfter(TiempoMaximoApi);

            var catalogo = await _themes.ObtenerTemasAsync(aniListId, limite.Token);
            if (catalogo.Count > 0)
            {
                completa = true;
                foreach (var tema in catalogo.Where(t => t.AplicaAlEpisodio(episodio)))
                {
                    if (temas.Any(t => MismoTema(t, tema))) continue;

                    string? ruta = await DescargarAsync(aniListId, tema, ct);
                    if (ruta == null) { completa = false; continue; }

                    temas.Add(new TemaLocalDisponible(tema.Tipo, tema.Slug, tema.Version, tema.RangoEpisodios, ruta));
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            completa = false;
            AppLogger.Debug("ReferenciasAudioService", $"No se pudo completar las referencias de {aniListId}: {ex.Message}");
        }

        return new ReferenciasEpisodio(temas, completa);
    }

    private List<TemaLocalDisponible> ListarLocalesDeLaFicha(int aniListId)
    {
        try { return _descargas.ListarDescargasLocales(aniListId) ?? new List<TemaLocalDisponible>(); }
        catch (Exception ex)
        {
            AppLogger.Debug("ReferenciasAudioService", $"No se pudieron listar las descargas locales de {aniListId}: {ex.Message}");
            return new List<TemaLocalDisponible>();
        }
    }

    private string CarpetaAnime(int aniListId) => Path.Combine(_carpeta, aniListId.ToString());

    private static string NombreCache(AnimeThemeInfo tema) => Path.ChangeExtension(tema.NombreArchivoLocal(), ".ogg");

    private List<TemaLocalDisponible> ListarCache(int aniListId)
    {
        var resultado = new List<TemaLocalDisponible>();
        string carpeta = CarpetaAnime(aniListId);
        if (!Directory.Exists(carpeta)) return resultado;

        try
        {
            foreach (var archivo in Directory.GetFiles(carpeta, "*.ogg"))
            {
                if (AnimeThemesDownloadService.TryParseNombreArchivo(archivo, out var tema) && tema != null)
                {
                    resultado.Add(tema);
                    try { File.SetLastAccessTimeUtc(archivo, DateTime.UtcNow); } catch { /* solo orden de uso */ }
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ReferenciasAudioService", $"Error listando la caché de referencias de {aniListId}: {ex.Message}");
        }
        return resultado;
    }

    private static void AgregarSinRepetir(List<TemaLocalDisponible> destino, IEnumerable<TemaLocalDisponible> nuevos)
    {
        foreach (var t in nuevos)
        {
            if (!destino.Any(d => MismoTema(d, t))) destino.Add(t);
        }
    }

    private static string Norm(string? rango) => (rango ?? string.Empty).Replace(" ", "");

    private static bool MismoTema(TemaLocalDisponible a, TemaLocalDisponible b) =>
        string.Equals(a.Tipo, b.Tipo, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Slug, b.Slug, StringComparison.OrdinalIgnoreCase)
        && a.Version == b.Version && Norm(a.RangoEpisodios) == Norm(b.RangoEpisodios);

    private static bool MismoTema(TemaLocalDisponible a, AnimeThemeInfo b) =>
        string.Equals(a.Tipo, b.Tipo, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Slug, b.Slug, StringComparison.OrdinalIgnoreCase)
        && a.Version == b.Version && Norm(a.RangoEpisodios) == Norm(b.RangoEpisodios);

    /// <summary>Solo https y solo el dominio de AnimeThemes: una URL rara de la API no debe hacer que la app baje de cualquier sitio.</summary>
    internal static bool EsUrlPermitida(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host.Equals("animethemes.moe", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".animethemes.moe", StringComparison.OrdinalIgnoreCase));

    private async Task<string?> DescargarAsync(int aniListId, AnimeThemeInfo tema, CancellationToken ct)
    {
        if (!EsUrlPermitida(tema.AudioUrlOgg)) return null;

        string carpeta = CarpetaAnime(aniListId);
        string destino = Path.Combine(carpeta, NombreCache(tema));
        string temporal = destino + ".tmp";

        try
        {
            Directory.CreateDirectory(carpeta);

            var http = _httpFactory.CreateClient("Downloader");
            using var respuesta = await http.GetAsync(tema.AudioUrlOgg, HttpCompletionOption.ResponseHeadersRead, ct);
            respuesta.EnsureSuccessStatusCode();
            if (respuesta.Content.Headers.ContentLength is long declarado && declarado > MaximoBytesAudio) return null;

            await using (var origen = await respuesta.Content.ReadAsStreamAsync(ct))
            await using (var salida = File.Create(temporal))
            {
                var buffer = new byte[81920];
                long total = 0;
                int n;
                while ((n = await origen.ReadAsync(buffer, ct)) > 0)
                {
                    total += n;
                    if (total > MaximoBytesAudio) return null;
                    await salida.WriteAsync(buffer.AsMemory(0, n), ct);
                }
            }

            File.Move(temporal, destino, overwrite: true); // nunca queda un audio a medias en la caché
            PodarCache(destino);
            return destino;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ReferenciasAudioService", $"No se pudo bajar la referencia '{tema.Slug}' de {aniListId}: {ex.Message}");
            return null;
        }
        finally
        {
            try { if (File.Exists(temporal)) File.Delete(temporal); } catch { /* best-effort */ }
        }
    }

    /// <summary>Si la caché pasa del tope, borra los archivos menos usados (nunca el recién guardado).</summary>
    internal void PodarCache(string conservar)
    {
        try
        {
            if (!Directory.Exists(_carpeta)) return;

            var archivos = new DirectoryInfo(_carpeta).EnumerateFiles("*.ogg", SearchOption.AllDirectories).ToList();
            long total = archivos.Sum(f => f.Length);
            foreach (var f in archivos.Where(f => !string.Equals(f.FullName, conservar, StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.LastAccessTimeUtc > f.LastWriteTimeUtc ? f.LastAccessTimeUtc : f.LastWriteTimeUtc))
            {
                if (total <= MaximoBytesCache) break;
                total -= f.Length;
                f.Delete();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ReferenciasAudioService", $"No se pudo podar la caché de referencias: {ex.Message}");
        }
    }
}
