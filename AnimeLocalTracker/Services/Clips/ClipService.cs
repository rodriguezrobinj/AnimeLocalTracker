using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Core;

namespace AnimeLocalTracker.Services.Clips;

/// <summary>
/// Ejecuta ffmpeg con la línea de <see cref="ArgumentosClip"/>. Con prioridad baja: el episodio sigue reproduciéndose mientras
/// se crea el clip y no debe dar tirones.
/// </summary>
public sealed class ClipService : IClipService
{
    private const string Origen = "Clips";
    private static readonly TimeSpan Limite = TimeSpan.FromMinutes(2);

    public ClipService() : this(CarpetaPorDefecto(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), AppDataPaths.DataRoot)) { }

    /// <param name="carpeta">Inyectable: las pruebas usan una carpeta temporal, nunca la de Vídeos real.</param>
    internal ClipService(string carpeta) => Carpeta = carpeta;

    /// <summary>
    /// Vídeos\AnimeLocalTracker. Windows puede no dar la carpeta Vídeos (perfil redirigido) y devuelve "": con eso la ruta quedaba
    /// relativa y el clip se escribía junto al programa. Entonces, la carpeta Videos del perfil; sin perfil, los datos de la app.
    /// </summary>
    internal static string CarpetaPorDefecto(string videos, string perfil, string datosApp)
    {
        if (Path.IsPathRooted(videos)) return Path.Combine(videos, "AnimeLocalTracker");
        if (Path.IsPathRooted(perfil)) return Path.Combine(perfil, "Videos", "AnimeLocalTracker");
        return Path.Combine(datosApp, "Clips");
    }

    public string Carpeta { get; }

    public async Task<string?> CrearAsync(SolicitudClip solicitud, CancellationToken ct)
    {
        string destino = solicitud.RutaSalida;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destino)!);
            destino = RutaLibre(destino);

            var reloj = Stopwatch.StartNew();
            var r = await ProcesoExterno.EjecutarAsync(FfmpegLocator.Ffmpeg, ArgumentosClip.Construir(solicitud with { RutaSalida = destino }),
                Limite, ct, ProcessPriorityClass.BelowNormal).ConfigureAwait(false);

            if (r is { Codigo: 0 } && File.Exists(destino) && new FileInfo(destino).Length > 0)
            {
                AppLogger.Info(Origen, $"Clip {solicitud.Formato} de {solicitud.DuracionSeg:0.#} s creado en {reloj.ElapsedMilliseconds} ms ({new FileInfo(destino).Length / 1024} KB).");
                return destino;
            }

            AppLogger.Warn(Origen, $"ffmpeg no pudo crear el clip (código {r?.Codigo.ToString() ?? "sin arrancar"}): {Final(r?.Error)}");
        }
        catch (OperationCanceledException)
        {
            AppLogger.Warn(Origen, $"El clip se canceló o tardó más de {Limite.TotalMinutes:0} min.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogger.Warn(Origen, $"No se pudo preparar el clip en {destino}: {ex.Message}");
        }

        BorrarAMedias(destino);
        return null;
    }

    /// <summary>"clip.mp4" → "clip (2).mp4" → "clip (3).mp4"… hasta dar con un nombre libre.</summary>
    private static string RutaLibre(string ruta)
    {
        if (!File.Exists(ruta)) return ruta;
        string carpeta = Path.GetDirectoryName(ruta)!, nombre = Path.GetFileNameWithoutExtension(ruta), ext = Path.GetExtension(ruta);
        for (int n = 2; ; n++)
        {
            string candidata = Path.Combine(carpeta, $"{nombre} ({n}){ext}");
            if (!File.Exists(candidata)) return candidata;
        }
    }

    private static void BorrarAMedias(string ruta)
    {
        try { if (File.Exists(ruta)) File.Delete(ruta); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>ffmpeg escribe el motivo del fallo al final de su salida de error.</summary>
    private static string Final(string? error)
        => string.IsNullOrWhiteSpace(error) ? "(sin detalle)" : error.Length > 500 ? error[^500..].Trim() : error.Trim();
}
