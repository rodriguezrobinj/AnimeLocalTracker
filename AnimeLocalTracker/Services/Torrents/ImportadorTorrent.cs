using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;

namespace AnimeLocalTracker.Services.Torrents;

/// <summary>Un torrent importado: guardado en la carpeta de la app y listo para el cuadro de confirmación.</summary>
public sealed record TorrentImportado(string Nombre, IReadOnlyList<ArchivoTorrent> Archivos, string Hash, string RutaGuardada)
{
    /// <summary>Lo que va en <c>CandidatoTorrent.TorrentUrl</c>: el motor solo acepta esta forma si es de la carpeta de importados.</summary>
    public string Url => new Uri(RutaGuardada).AbsoluteUri;
    public long Tamano => Archivos.Sum(a => a.Tamano);
}

/// <summary>Por qué no se pudo importar, ya en palabras del usuario.</summary>
public sealed class ImportacionTorrentException(string mensaje) : Exception(mensaje);

/// <summary>Torrents soltados en la ventana o pegados como magnet (ver docs/plan-torrents-arrastrar-magnet.md).</summary>
public sealed class ImportadorTorrent
{
    public static readonly TimeSpan EsperaMaximaMagnet = TimeSpan.FromMinutes(2);

    private readonly ITorrentDownloadService _motor;

    public ImportadorTorrent(ITorrentDownloadService motor, string carpeta)
    {
        _motor = motor;
        Carpeta = carpeta;
    }

    public string Carpeta { get; }

    public static bool PareceMagnet(string? texto)
    {
        string t = texto?.Trim() ?? string.Empty;
        return t.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase) && MagnetLink.TryParse(t, out _);
    }

    public async Task<TorrentImportado> DesdeArchivoAsync(string ruta, CancellationToken ct)
    {
        byte[] datos;
        try
        {
            if (new FileInfo(ruta).Length > TorrentDownloadService.MaxTorrentBytes) throw NoValido();
            datos = await File.ReadAllBytesAsync(ruta, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogger.Warn("ImportadorTorrent", $"No se pudo leer el .torrent: {ex.Message}");
            throw NoValido();
        }
        return Guardar(datos);
    }

    public async Task<TorrentImportado> DesdeMagnetAsync(string enlace, CancellationToken ct)
    {
        if (!PareceMagnet(enlace)) throw new ImportacionTorrentException(LocalizationService.T("Desc_ImportarMagnetNoValido"));

        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(EsperaMaximaMagnet);
        byte[]? datos;
        try
        {
            datos = await _motor.ObtenerTorrentDeMagnetAsync(enlace.Trim(), limite.Token);
        }
        catch (Exception ex)
        {
            AppLogger.Info("ImportadorTorrent", $"No se obtuvieron los metadatos del magnet: {ex.Message}");
            throw new ImportacionTorrentException(LocalizationService.T("Desc_ImportarSinMetadatos"));
        }
        if (datos == null || datos.Length == 0 || datos.Length > TorrentDownloadService.MaxTorrentBytes)
            throw new ImportacionTorrentException(LocalizationService.T("Desc_ImportarSinMetadatos"));
        return Guardar(datos);
    }

    /// <summary>Borra los .torrent importados que ninguna descarga de la cola usa. Devuelve cuántos borró.</summary>
    public static int LimpiarHuerfanos(string carpeta, IReadOnlySet<string> rutasEnUso)
    {
        if (!Directory.Exists(carpeta)) return 0;
        int borrados = 0;
        foreach (string ruta in Directory.EnumerateFiles(carpeta, "*.torrent"))
        {
            if (rutasEnUso.Contains(Path.GetFullPath(ruta))) continue;
            try
            {
                File.Delete(ruta);
                borrados++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLogger.Debug("ImportadorTorrent", $"No se pudo borrar un .torrent importado: {ex.Message}");
            }
        }
        return borrados;
    }

    /// <summary>
    /// La ruta local de un .torrent importado si <paramref name="url"/> es un <c>file:</c> que apunta DENTRO de
    /// <paramref name="carpeta"/>; si no, null. Una cola manipulada no puede hacer leer al motor un archivo cualquiera.
    /// </summary>
    public static string? RutaLocalPermitida(string? url, string carpeta)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsFile) return null;
        string ruta = Path.GetFullPath(uri.LocalPath);
        string baseDir = Path.GetFullPath(carpeta).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return ruta.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase) && ruta.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)
            ? ruta : null;
    }

    private TorrentImportado Guardar(byte[] datos)
    {
        Torrent torrent;
        try
        {
            torrent = Torrent.Load(datos);
        }
        catch (Exception ex)
        {
            AppLogger.Info("ImportadorTorrent", $"No es un .torrent válido: {ex.Message}");
            throw NoValido();
        }

        // ponytail: la identidad es el SHA-256 del archivo, no el infohash: el mismo torrent por magnet y por archivo puede dar dos
        // claves (y bajarse en paralelo). Usar el infohash si eso llega a pasar.
        string hash = Convert.ToHexString(SHA256.HashData(datos)).ToLowerInvariant();
        Directory.CreateDirectory(Carpeta);
        string ruta = Path.GetFullPath(Path.Combine(Carpeta, hash + ".torrent"));
        if (!File.Exists(ruta)) File.WriteAllBytes(ruta, datos);

        var archivos = torrent.Files.Select(f => new ArchivoTorrent(f.Path, f.Length)).ToList();
        return new TorrentImportado(torrent.Name, archivos, hash, ruta);
    }

    private static ImportacionTorrentException NoValido() => new(LocalizationService.T("Desc_ImportarNoValido"));
}
