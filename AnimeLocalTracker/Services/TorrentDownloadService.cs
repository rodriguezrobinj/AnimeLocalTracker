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

/// <summary>
/// Detecta un torrent que no avanza — lógica pura (reloj inyectado por parámetro) para poder
/// testearse sin MonoTorrent. Sin esto, un torrent sin nadie compartiéndolo esperaba para
/// siempre y ocupaba uno de los huecos de descarga de la app.
/// </summary>
public sealed class VigilanteEstancamientoTorrent
{
    /// <summary>Margen para encontrar fuentes (DHT, trackers) antes de recibir el primer dato.</summary>
    public static readonly TimeSpan MaximoSinPrimerDato = TimeSpan.FromMinutes(5);

    /// <summary>Margen sin avanzar una vez que ya llegaban datos (las fuentes se fueron).</summary>
    public static readonly TimeSpan MaximoSinAvance = TimeSpan.FromMinutes(10);

    private double _mejorProgreso;
    private DateTime _ultimoAvance;
    private bool _huboAvance;

    public VigilanteEstancamientoTorrent(double progresoInicial, DateTime ahoraUtc)
    {
        _mejorProgreso = progresoInicial;
        _ultimoAvance = ahoraUtc;
    }

    /// <summary>Registra el progreso actual; devuelve el motivo si el torrent se considera estancado, o null.</summary>
    public string? Registrar(double progreso, DateTime ahoraUtc)
    {
        if (progreso > _mejorProgreso)
        {
            _mejorProgreso = progreso;
            _ultimoAvance = ahoraUtc;
            _huboAvance = true;
            return null;
        }

        var quieto = ahoraUtc - _ultimoAvance;
        if (!_huboAvance && quieto >= MaximoSinPrimerDato)
            return $"Nadie está compartiendo este torrent (sin datos en {MaximoSinPrimerDato.TotalMinutes:0} min).";
        if (_huboAvance && quieto >= MaximoSinAvance)
            return $"El torrent dejó de avanzar ({MaximoSinAvance.TotalMinutes:0} min sin recibir datos).";
        return null;
    }
}

public class TorrentDownloadService : ITorrentDownloadService, IDisposable
{
    // Sondeo de progreso: MonoTorrent no expone un evento "progreso cambió", así que
    // se sondea Manager.Progress/Monitor.DownloadSpeed cada tanto — mismo intervalo
    // que ya usa BrowserStreamExtractor (Python) para su propio sondeo activo.
    private const int IntervaloSondeoMs = 500;

    /// <summary>SEC-03: tamaño máximo aceptado para un archivo .torrent (los reales pesan decenas de KB).</summary>
    internal const long MaxTorrentBytes = 5 * 1024 * 1024;

    /// <summary>PERF-04: tiempo máximo que el cierre de la app espera a detener el sembrado.</summary>
    private static readonly TimeSpan EsperaMaximaAlCerrar = TimeSpan.FromSeconds(3);

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
            CacheDirectory = AppDataPaths.TorrentsCacheDir,
            // Por defecto MonoTorrent intenta conectar con solo 8 personas a la vez: encontrar a
            // quien sí comparte tarda mucho en torrents con pocas semillas (lo normal en anime).
            MaximumHalfOpenConnections = 32,
            MaximumConnections = 200,
            // 5 MB por defecto: con descargas rápidas obliga a escribir en disco a trozos pequeños.
            DiskCacheBytes = 32 * 1024 * 1024,
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
        bool conservarTemporal = false;
        try
        {
            byte[] torrentBytes;
            using (var req = new HttpRequestMessage(HttpMethod.Get, torrentUrl))
            {
                using var res = await _httpClient.SendAsync(req, ct);
                if (!res.IsSuccessStatusCode)
                    return new ResultadoTorrent(false, null, $"No se pudo descargar el .torrent (HTTP {(int) res.StatusCode}).");
                // SEC-03: un .torrent real pesa KB; el tope evita que un Nyaa comprometido (o un MITM) llene la memoria.
                torrentBytes = await Core.EntradaSegura.LeerAcotadoAsync(res.Content, MaxTorrentBytes, ct);
            }

            var torrent = await Torrent.LoadAsync(torrentBytes);

            Directory.CreateDirectory(carpetaTemporal);
            var settings = new TorrentSettingsBuilder { AllowDht = true, AllowPeerExchange = true, MaximumConnections = 100 }.ToSettings();
            manager = await _engine.AddAsync(torrent, carpetaTemporal, settings);
            await AnadirRastreadoresPublicosAsync(manager, torrent);

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

            var vigilante = new VigilanteEstancamientoTorrent(manager.Progress, DateTime.UtcNow);
            while (manager.Progress < 100.0)
            {
                ct.ThrowIfCancellationRequested();

                // Un error de MonoTorrent (disco lleno, archivo bloqueado...) detiene el torrent
                // para siempre: sin esta salida el bucle esperaba un 100 % que nunca llegaría.
                if (manager.State == TorrentState.Error)
                {
                    string motivo = manager.Error?.Exception?.Message ?? "error desconocido del motor de torrent";
                    AppLogger.Warn("TorrentDownloadService", $"El torrent entró en estado de error: {motivo}");
                    await DetenerYQuitarAsync(manager);
                    manager = null;
                    return new ResultadoTorrent(false, null, $"Error del torrent: {motivo}");
                }

                string? estancado = vigilante.Registrar(manager.Progress, DateTime.UtcNow);
                if (estancado != null)
                {
                    AppLogger.Warn("TorrentDownloadService", $"{estancado} Progreso: {manager.Progress:F1} %, conexiones: {manager.OpenConnections}.");
                    await DetenerYQuitarAsync(manager);
                    manager = null;
                    return new ResultadoTorrent(false, null, estancado);
                }

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
            // SEC-03 (defensa en profundidad): la ruta sale del contenido del .torrent; nunca mover/copiar un archivo
            // que quede fuera de la carpeta temporal de esta descarga aunque MonoTorrent no lo hubiera impedido.
            if (!Core.EntradaSegura.EstaDentroDe(carpetaTemporal, rutaFinal))
            {
                AppLogger.Warn("TorrentDownloadService", "El torrent apunta a una ruta fuera de la carpeta temporal; se descarta.");
                return new ResultadoTorrent(false, null, "El torrent contiene una ruta de archivo no válida.");
            }
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
            // Pausa o cancelación: se conservan las piezas ya bajadas para que reanudar las
            // retome (MonoTorrent las verifica al volver a añadir el torrent en esta carpeta).
            // Si era una cancelación definitiva, el llamador borra la carpeta.
            conservarTemporal = true;
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
            // (es de donde sirve las piezas a otros peers) — no se borra en ese caso. Tampoco
            // tras una pausa/cancelación: ahí están las piezas que reanudar debe retomar.
            if (!dejandoSembrando && !conservarTemporal)
            {
                try { if (Directory.Exists(carpetaTemporal)) Directory.Delete(carpetaTemporal, recursive: true); }
                catch (Exception ex) { AppLogger.Debug("TorrentDownloadService", $"No se pudo limpiar la carpeta temporal: {ex.Message}"); }
            }
        }
    }

    /// <summary>
    /// Rastreadores públicos veteranos que se suman a los del .torrent (como la opción "añadir
    /// rastreadores automáticamente" de qBittorrent): el .torrent de Nyaa trae pocos, y más
    /// rastreadores = más personas encontradas que comparten el mismo archivo.
    /// </summary>
    internal static readonly string[] RastreadoresPublicos =
    {
        "udp://tracker.opentrackr.org:1337/announce",
        "udp://open.stealth.si:80/announce",
        "udp://tracker.torrent.eu.org:451/announce",
        "udp://exodus.desync.com:6969/announce",
        "udp://open.demonii.com:1337/announce",
        "http://nyaa.tracker.wf:7777/announce",
    };

    private static async Task AnadirRastreadoresPublicosAsync(TorrentManager manager, Torrent torrent)
    {
        // Un torrent privado solo puede usar sus propios rastreadores (reglas del sitio que lo publica).
        if (torrent.IsPrivate) return;

        var yaIncluidos = new HashSet<string>(
            torrent.AnnounceUrls.SelectMany(nivel => nivel), StringComparer.OrdinalIgnoreCase);
        foreach (var url in RastreadoresPublicos.Where(u => !yaIncluidos.Contains(u)))
        {
            try { await manager.TrackerManager.AddTrackerAsync(new Uri(url)); }
            catch (Exception ex) { AppLogger.Debug("TorrentDownloadService", $"No se pudo añadir el rastreador {url}: {ex.Message}"); }
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
        // PERF-04: con tope de espera; si MonoTorrent se cuelga al detener, el cierre de la app no se queda colgado
        // (el proceso termina igualmente y el sembrado muere con él).
        try
        {
            var detener = Task.Run(DetenerTodoElSeedingAsync);
            if (!detener.Wait(EsperaMaximaAlCerrar))
                AppLogger.Warn("TorrentDownloadService", $"Detener el sembrado tardó más de {EsperaMaximaAlCerrar.TotalSeconds:0} s; se continúa con el cierre.");
        }
        catch (Exception ex) { AppLogger.Debug("TorrentDownloadService", $"Error deteniendo el sembrado al cerrar: {ex.GetBaseException().Message}"); }
        _engine.Dispose();
        GC.SuppressFinalize(this);
    }
}
