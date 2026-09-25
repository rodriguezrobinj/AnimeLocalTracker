using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;
using MonoTorrent.Client;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Selección del archivo de video dentro de un torrent — lógica pura, sin tocar
/// MonoTorrent ni la red, para poder testearse con listas simples de
/// (ruta, tamaño) en vez de objetos reales de MonoTorrent (difíciles de
/// instanciar fuera de un <see cref="ClientEngine"/> real).
/// </summary>
public static class SeleccionArchivoTorrent
{
    private static readonly string[] ExtensionesVideo = { ".mkv", ".mp4", ".avi" };

    /// <summary>
    /// El archivo de video más grande entre los del torrent (subs sueltos, .nfo,
    /// imágenes, etc. se ignoran). Null si el torrent no tiene ningún archivo con
    /// extensión de video reconocida.
    /// </summary>
    public static string? ElegirArchivoDeVideo(IEnumerable<(string Path, long Length)> archivos)
    {
        return archivos
            .Where(a => ExtensionesVideo.Contains(Path.GetExtension(a.Path), StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(a => a.Length)
            .Select(a => (string?)a.Path)
            .FirstOrDefault();
    }

    /// <summary>
    /// Fase 2a (torrents batch): entre los archivos de video del torrent, el que su
    /// propio NOMBRE DE ARCHIVO identifica como el episodio pedido (reutiliza
    /// <see cref="NyaaRssParser.ExtraerNumeroEpisodio"/> — los archivos dentro de un
    /// batch suelen nombrarse igual que releases sueltos, ej. "Anime - 07.mkv"). Si
    /// varios archivos parsean al mismo episodio (raro), el más grande. Null si
    /// ningún nombre de archivo identifica ese episodio — el llamador cae a
    /// <see cref="ElegirArchivoDeVideo"/> (el más grande de todos) en vez de fallar.
    /// </summary>
    public static string? ElegirArchivoDelEpisodio(IEnumerable<(string Path, long Length)> archivos, int numeroEpisodio)
    {
        return archivos
            .Where(a => ExtensionesVideo.Contains(Path.GetExtension(a.Path), StringComparer.OrdinalIgnoreCase))
            .Where(a => NyaaRssParser.ExtraerNumeroEpisodio(Path.GetFileName(a.Path)) == numeroEpisodio)
            .OrderByDescending(a => a.Length)
            .Select(a => (string?)a.Path)
            .FirstOrDefault();
    }
}

public class TorrentDownloadService : ITorrentDownloadService, IDisposable
{
    // Sondeo de progreso: MonoTorrent no expone un evento "progreso cambió", así que
    // se sondea Manager.Progress/Monitor.DownloadSpeed cada tanto — mismo intervalo
    // que ya usa BrowserStreamExtractor (Python) para su propio sondeo activo.
    private const int IntervaloSondeoMs = 500;

    private readonly HttpClient _httpClient;
    private readonly ClientEngine _engine;
    // Fase 2c: torrents que siguen sembrando tras completar (seguirSembrando=true) —
    // set thread-safe (el valor byte no se usa), para poder detenerlos todos al cerrar la app.
    private readonly ConcurrentDictionary<TorrentManager, byte> _sembrando = new();
    private bool _disposed;

    /// <summary>Cuántos torrents siguen sembrando ahora mismo (Fase 2c). Útil para una
    /// futura UI de "sembrado activo" y para verificar el comportamiento en pruebas
    /// reales sin exponer los <see cref="TorrentManager"/> internos.</summary>
    public int CantidadSembrando => _sembrando.Count;

    public TorrentDownloadService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        Directory.CreateDirectory(AppDataPaths.TorrentsCacheDir);
        var engineSettings = new EngineSettingsBuilder
        {
            CacheDirectory = AppDataPaths.TorrentsCacheDir
        }.ToSettings();
        _engine = new ClientEngine(engineSettings);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Dispose();
    }

    public async Task<ResultadoTorrent> DescargarAsync(
        string torrentUrl,
        string carpetaTemporal,
        string rutaDestinoEsperada,
        int numeroEpisodio,
        bool seguirSembrando = false,
        IProgress<(double Progreso, double VelocidadBps)>? progress = null,
        CancellationToken ct = default)
    {
        if (!Core.UrlSeguridad.EsUrlNyaaPermitida(torrentUrl))
            return new ResultadoTorrent(false, null, "URL de torrent no permitida.");

        TorrentManager? manager = null;
        bool dejandoSembrando = false;
        try
        {
            byte[] torrentBytes;
            using (var req = new HttpRequestMessage(HttpMethod.Get, torrentUrl))
            {
                using var res = await _httpClient.SendAsync(req, ct);
                if (!res.IsSuccessStatusCode)
                    return new ResultadoTorrent(false, null, $"No se pudo descargar el .torrent (HTTP {(int) res.StatusCode}).");
                torrentBytes = await res.Content.ReadAsByteArrayAsync(ct);
            }

            var torrent = await Torrent.LoadAsync(torrentBytes);

            Directory.CreateDirectory(carpetaTemporal);
            var settings = new TorrentSettingsBuilder { AllowDht = true, AllowPeerExchange = true }.ToSettings();
            manager = await _engine.AddAsync(torrent, carpetaTemporal, settings);

            var archivosDelTorrent = manager.Files.Select(f => (f.Path, f.Length)).ToList();
            string? rutaElegida = SeleccionArchivoTorrent.ElegirArchivoDelEpisodio(archivosDelTorrent, numeroEpisodio)
                ?? SeleccionArchivoTorrent.ElegirArchivoDeVideo(archivosDelTorrent);
            if (rutaElegida == null)
            {
                await _engine.RemoveAsync(manager);
                return new ResultadoTorrent(false, null, "El torrent no tiene ningún archivo de video reconocible.");
            }

            var archivoElegido = manager.Files.First(f => f.Path == rutaElegida);
            foreach (var archivo in manager.Files)
            {
                await manager.SetFilePriorityAsync(archivo, archivo.Path == rutaElegida ? Priority.Normal : Priority.DoNotDownload);
            }

            await manager.StartAsync();

            while (manager.Progress < 100.0)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report((manager.Progress, manager.Monitor.DownloadRate));
                await Task.Delay(IntervaloSondeoMs, ct);
            }

            if (seguirSembrando)
            {
                // Se deja el TorrentManager corriendo, sirviendo piezas a otros peers desde
                // carpetaTemporal — por eso el archivo se COPIA más abajo, no se mueve.
                _sembrando[manager] = 0;
                dejandoSembrando = true;
            }
            else
            {
                await manager.StopAsync();
                await _engine.RemoveAsync(manager);
            }
            manager = null;

            string rutaFinal = archivoElegido.DownloadCompleteFullPath;
            if (!File.Exists(rutaFinal))
                return new ResultadoTorrent(false, null, "El torrent terminó pero no se encontró el archivo descargado.");

            string? carpetaDestino = Path.GetDirectoryName(rutaDestinoEsperada);
            if (!string.IsNullOrEmpty(carpetaDestino)) Directory.CreateDirectory(carpetaDestino);
            if (File.Exists(rutaDestinoEsperada)) File.Delete(rutaDestinoEsperada);

            if (dejandoSembrando)
            {
                File.Copy(rutaFinal, rutaDestinoEsperada);
            }
            else
            {
                File.Move(rutaFinal, rutaDestinoEsperada);
            }

            return new ResultadoTorrent(true, rutaDestinoEsperada, null);
        }
        catch (OperationCanceledException)
        {
            await DetenerYQuitarAsync(manager);
            throw;
        }
        catch (Exception ex)
        {
            await DetenerYQuitarAsync(manager);
            AppLogger.Warn("TorrentDownloadService", $"Descarga por torrent falló: {ex.Message}");
            return new ResultadoTorrent(false, null, ex.Message);
        }
        finally
        {
            // Si sigue sembrando, MonoTorrent necesita que esta carpeta siga existiendo
            // (es de donde sirve las piezas a otros peers) — no se borra en ese caso.
            if (!dejandoSembrando)
            {
                try { if (Directory.Exists(carpetaTemporal)) Directory.Delete(carpetaTemporal, recursive: true); }
                catch (Exception ex) { AppLogger.Debug("TorrentDownloadService", $"No se pudo limpiar la carpeta temporal: {ex.Message}"); }
            }
        }
    }

    public async Task DetenerTodoElSeedingAsync()
    {
        foreach (var manager in _sembrando.Keys.ToList())
        {
            try
            {
                await manager.StopAsync();
                await _engine.RemoveAsync(manager);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("TorrentDownloadService", $"Error deteniendo un torrent sembrando: {ex.Message}");
            }
            finally
            {
                _sembrando.TryRemove(manager, out _);
            }
        }
    }

    private async Task DetenerYQuitarAsync(TorrentManager? manager)
    {
        if (manager == null) return;
        try
        {
            await manager.StopAsync();
            await _engine.RemoveAsync(manager);
        }
        catch (Exception ex)
        {
            AppLogger.Debug("TorrentDownloadService", $"Error deteniendo el torrent tras cancelación/fallo: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Sync-over-async deliberado: Dispose (y el handler de ProcessExit que lo llama)
        // es síncrono, pero hace falta detener el sembrado activo antes de tirar el motor
        // — nunca dejar sembrando de fondo tras cerrar la app.
        try { DetenerTodoElSeedingAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { AppLogger.Debug("TorrentDownloadService", $"Error deteniendo el sembrado al cerrar: {ex.Message}"); }
        _engine.Dispose();
        GC.SuppressFinalize(this);
    }
}
