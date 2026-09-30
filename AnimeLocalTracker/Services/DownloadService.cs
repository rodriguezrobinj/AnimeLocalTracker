using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Services.Python;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Win32.SafeHandles;

namespace AnimeLocalTracker.Services;

public class DownloadService : IDownloadService
{
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
    // Tamaño de cada trozo de la cola de descarga: lo bastante pequeño para repartir la carga
    // entre conexiones y hacer barato un reintento, lo bastante grande para no saturar de peticiones.
    private const long TamanoTrozoBytes = 4L * 1024 * 1024;
    private const int MinimoTrozos = 6;
    private const int MaxReintentosPorTrozo = 4;
    private const int ConexionesTotalesObjetivo = 12;
    private const int MinConexionesPorDescarga = 3;
    private const int MaxConexionesPorDescarga = 8;
    // Ajuste automático (fase 4): una descarga puede subir por encima del reparto justo si medir
    // demuestra que va más rápido, sin pasar de estos topes (por descarga y entre todas).
    private const int TechoAbsolutoConexionesPorDescarga = 16;
    private const int PresupuestoMaximoConexiones = 32;
    // Sondeo inicial (tamaño/rangos). Medido en uso real: a3.mp4upload.com tarda 14–26 s solo en la
    // negociación segura (TLS); con 6 s el sondeo fallaba siempre y se caía a UNA conexión (~85 KB/s).
    internal TimeSpan TiempoMaximoSondeo { get; init; } = TimeSpan.FromSeconds(45);
    // Intentos seguidos en los que el servidor no respondió a tiempo sin llegar a mandar datos:
    // pasado esto se considera caído (se prueba torrent si está activado) en vez de agotar los 5 reintentos.
    private const int MaxIntentosSinRespuesta = 2;
    // Reintentos de un sondeo rechazado con 403 por exceso de conexiones (cada uno espera turno antes).
    private const int MaxSondeosRechazadosPorTope = 10;
    /// <summary>
    /// Si el servidor tarda al menos esto en contestar cada petición, se piden varios trozos por
    /// petición (ver <see cref="AgrupadorTrozos"/>). Los tests lo acortan.
    /// </summary>
    internal TimeSpan UmbralServidorLento { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Cada cuánto se mide la velocidad para decidir si probar más conexiones (los tests lo acortan).</summary>
    internal TimeSpan VentanaMedicionConexiones { get; init; } = TimeSpan.FromSeconds(4);
    // Torrents de Nyaa que la descarga automática prueba (en orden de preferencia) antes de rendirse.
    private const int MaxCandidatosTorrentAutomaticos = 2;
    // Cada cuánto un trabajador sin turno vuelve a mirar si el reparto le deja trabajar.
    private static readonly TimeSpan IntervaloReparto = TimeSpan.FromMilliseconds(500);
    private const int TamanoBufferLectura = 128 * 1024;
    private static readonly TimeSpan TiempoMaximoInactividad = TimeSpan.FromSeconds(60);
    // Tope a la espera que pida un servidor saturado (Retry-After), para no quedar parado minutos.
    private static readonly TimeSpan EsperaMaximaRetryAfter = TimeSpan.FromSeconds(60);
    private const int LimiteDescargasPorDefecto = 3;
    // FUN-017: máximo de reintentos automáticos ante cortes de red transitorios antes de abandonar.
    private const int MaxReintentosDescargaTransitoria = 5;
    // Sin internet se espera en tramos de un minuto sin gastar reintentos; tras ~1 h se abandona como antes.
    private static readonly TimeSpan EsperaMaximaSinConexion = TimeSpan.FromMinutes(1);
    private const int MaxEsperasSinConexion = 60;
    // SEC-03: tope de seguridad por archivo en el modo secuencial (el segmentado ya lo acota).
    private const long MaxArchivoDescargaBytes = 35L * 1024 * 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly IDownloadStateStore _stateStore;
    /// <summary>Archivo con la cola de descargas (null = no se guarda; así las pruebas nunca escriben en los datos del usuario).</summary>
    private readonly string? _rutaColaPendiente;
    private readonly object _lockCola = new();
    private readonly IVideoSourceResolver _sourceResolver;
    private readonly ISettingsService? _settingsService;
    private readonly IPythonBridgeService? _pythonBridge;
    private readonly IDatabaseService? _database;
    private readonly INyaaSourceService? _nyaaSourceService;
    private readonly ITorrentDownloadService? _torrentDownloadService;
    private readonly IConectividadRed? _conectividad;
    private readonly ConcurrentDictionary<string, DownloadState> _activeDownloads = new();

    // Gestor de slots de concurrencia (redimensionable en caliente según DescargasSimultaneas)
    private readonly object _slotLock = new();
    private readonly List<(string Key, TaskCompletionSource<bool> Tcs)> _slotWaiters = new();
    private int _slotsActivos;
    private int _limiteDescargas = LimiteDescargasPorDefecto;
    // Descargas transfiriendo por trozos ahora mismo (reparto de conexiones en caliente).
    private int _descargasHttpActivas;
    // Conexiones abiertas y tope aprendido por servidor de video, compartido entre TODAS las descargas.
    private readonly LimitadorPorServidor _limitadorServidores = new();
    // Último tope avisado en el registro por servidor (el aviso solo se repite si cambia).
    private readonly ConcurrentDictionary<string, int> _ultimoTopeAvisado = new(StringComparer.OrdinalIgnoreCase);

    private long _ordenCounter = 0;

    private class DownloadState
    {
        public long Orden { get; set; }
        public int AniListId { get; set; }
        public string AnimeTitulo { get; set; } = string.Empty;
        public List<string> Titulos { get; set; } = new();
        public int NumeroEpisodio { get; set; }
        public double Progreso { get; set; }
        public string RutaDestino { get; set; } = string.Empty;
        /// <summary>Fuente que efectivamente resolvió el episodio: "AnimeAv1" (por defecto) o "Nyaa" (torrent, Fase 1c).</summary>
        public string Fuente { get; set; } = "AnimeAv1";
        public string RutaTemporal { get; set; } = string.Empty;
        public string? VideoUrl { get; set; }
        public bool IsPaused { get; set; }
        public CancellationTokenSource Cts { get; set; } = new();
        public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;
        public string CarpetaDestino { get; set; } = string.Empty;
        /// <summary>True hasta que la descarga obtiene un slot (o mientras espera uno tras reanudar).</summary>
        public volatile bool EnCola = true;
        public int Reintentos { get; set; }
        /// <summary>Iniciada por la descarga automática de episodios nuevos (sus fallos "no encontrado" no van al historial).</summary>
        public bool Automatica { get; set; }
        /// <summary>El usuario pidió saltar la cola antes de que la descarga llegara a registrarse como waiter.</summary>
        public volatile bool Priorizada;
        /// <summary>Torrent en curso (elegido a mano o hallado en Nyaa): reanudar debe seguir con él, no volver a AnimeAv1.</summary>
        public CandidatoTorrent? Torrent { get; set; }
        /// <summary>Esperas de hasta un minuto hechas por falta de internet (acotadas por <see cref="MaxEsperasSinConexion"/>).</summary>
        public int EsperasSinConexion { get; set; }
    }

    public DownloadService(
        IHttpClientFactory httpClientFactory,
        IDownloadStateStore? stateStore = null,
        IVideoSourceResolver? sourceResolver = null,
        ISettingsService? settingsService = null,
        IPythonBridgeService? pythonBridge = null,
        IDatabaseService? database = null,
        INyaaSourceService? nyaaSourceService = null,
        ITorrentDownloadService? torrentDownloadService = null,
        IConectividadRed? conectividad = null,
        string? rutaColaPendiente = null)
    {
        _rutaColaPendiente = rutaColaPendiente;
        _database = database;
        _conectividad = conectividad;
        _httpClient = httpClientFactory.CreateClient("Downloader");
        _stateStore = stateStore ?? new DownloadStateStore();
        _sourceResolver = sourceResolver ?? new AnimeAv1VideoSourceResolver(_httpClient);
        _pythonBridge = pythonBridge;
        _settingsService = settingsService;
        _nyaaSourceService = nyaaSourceService;
        _torrentDownloadService = torrentDownloadService;

        if (settingsService != null)
        {
            var config = settingsService.ObtenerConfiguracion();
            if (config != null && config.DescargasSimultaneas > 0)
            {
                ActualizarLimiteDescargas(config.DescargasSimultaneas);
            }

            settingsService.ConfiguracionModificada += configNueva =>
            {
                if (configNueva?.DescargasSimultaneas > 0)
                {
                    ActualizarLimiteDescargas(configNueva.DescargasSimultaneas);
                }
            };
        }
    }

    /// <summary>
    /// Ajusta el número máximo de descargas simultáneas. Redimensiona el gestor
    /// de slots en caliente: las descargas activas no se interrumpen y los
    /// pendientes en cola se liberan si el nuevo límite permite más concurrentes.
    /// </summary>
    public void ActualizarLimiteDescargas(int nuevoLimite)
    {
        int limite = Math.Max(1, nuevoLimite);

        TaskCompletionSource<bool>[] liberar;
        lock (_slotLock)
        {
            _limiteDescargas = limite;
            liberar = DespacharSlotsPendientesLocked();
        }

        foreach (var tcs in liberar)
        {
            tcs.TrySetResult(true);
        }
    }

    /// <summary>
    /// Dentro del lock: concede slots a los waiters en orden FIFO mientras
    /// haya huecos disponibles. Devuelve los TCS que deben completarse FUERA
    /// del lock (para no ejecutar continuaciones bajo exclusión mutua).
    /// </summary>
    private TaskCompletionSource<bool>[] DespacharSlotsPendientesLocked()
    {
        var concedidos = new List<TaskCompletionSource<bool>>();
        while (_slotWaiters.Count > 0 && _slotsActivos < _limiteDescargas)
        {
            var waiter = _slotWaiters[0];
            _slotWaiters.RemoveAt(0);
            _slotsActivos++;
            concedidos.Add(waiter.Tcs);
        }
        return concedidos.ToArray();
    }

    /// <summary>
    /// Espera un slot de descarga (bloquea si ya hay el máximo simultáneo).
    /// Respetuoso con la cancelación: si el token se cancela mientras espera,
    /// el slot no se consume y el waiter se descarta de la cola.
    /// </summary>
    private async Task<bool> AdquirirSlotAsync(string key, DownloadState state, CancellationToken ct)
    {
        TaskCompletionSource<bool>? tcs = null;
        lock (_slotLock)
        {
            if (_slotsActivos < _limiteDescargas)
            {
                _slotsActivos++;
                return true;
            }

            tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            // La bandera se lee DENTRO del lock: PriorizarDescarga puede activarla justo antes de que esta descarga se encole.
            if (state.Priorizada) _slotWaiters.Insert(0, (key, tcs));
            else _slotWaiters.Add((key, tcs));
        }

        using var reg = ct.Register(() => tcs.TrySetCanceled());
        try
        {
            await tcs.Task;
            return true;
        }
        catch (OperationCanceledException)
        {
            // Si se canceló mientras esperaba, retirar de la cola para no perder un slot futuro
            lock (_slotLock)
            {
                _slotWaiters.RemoveAll(w => ReferenceEquals(w.Tcs, tcs));
            }
            throw;
        }
    }

    /// <summary>Adelanta una descarga en espera al primer puesto de la cola. False si no está esperando un slot.</summary>
    public bool PriorizarDescarga(int aniListId, int numeroEpisodio)
    {
        string key = $"{aniListId}_{numeroEpisodio}";
        lock (_slotLock)
        {
            int idx = _slotWaiters.FindIndex(w => w.Key == key);
            if (idx < 0)
            {
                // Aún no llegó a la cola (se acaba de iniciar): se recuerda para que entre por delante.
                if (_activeDownloads.TryGetValue(key, out var pendiente) && pendiente.EnCola && !pendiente.IsPaused)
                {
                    pendiente.Priorizada = true;
                    pendiente.Orden = _activeDownloads.Values.Min(d => d.Orden) - 1;
                    return true;
                }
                return false;
            }
            if (idx > 0)
            {
                var w = _slotWaiters[idx];
                _slotWaiters.RemoveAt(idx);
                _slotWaiters.Insert(0, w);
            }
        }

        if (_activeDownloads.TryGetValue(key, out var state))
        {
            var minOrden = _activeDownloads.Values.Min(d => d.Orden);
            state.Orden = minOrden - 1;
        }
        return true;
    }

    private void LiberarSlot()
    {
        TaskCompletionSource<bool>[] liberar;
        lock (_slotLock)
        {
            if (_slotsActivos > 0) _slotsActivos--;
            liberar = DespacharSlotsPendientesLocked();
        }

        foreach (var tcs in liberar)
        {
            tcs.TrySetResult(true);
        }
    }

    public bool EstaDescargando(int aniListId, int numeroEpisodio, out double progreso)
    {
        string key = $"{aniListId}_{numeroEpisodio}";
        if (_activeDownloads.TryGetValue(key, out var state))
        {
            progreso = state.Progreso;
            return true;
        }
        progreso = 0;
        return false;
    }

    public void CancelarDescarga(int aniListId, int numeroEpisodio)
    {
        string key = $"{aniListId}_{numeroEpisodio}";
        if (_activeDownloads.TryRemove(key, out var state))
        {
            try { state.Cts.Cancel(); } catch { }
            LimpiarTemporalesSiNoHayTareaViva(state, key);
            WeakReferenceMessenger.Default.Send(new DescargaProgresoMensaje(aniListId, numeroEpisodio, 0, isDownloading: false, isCompleted: false, isPaused: false, "", "Descarga cancelada", state.AnimeTitulo));
            GuardarColaPendiente();
        }
    }

    public void CancelarTodas()
    {
        foreach (var kvp in _activeDownloads.ToList())
        {
            if (_activeDownloads.TryRemove(kvp.Key, out var state))
            {
                try { state.Cts.Cancel(); } catch { }
                LimpiarTemporalesSiNoHayTareaViva(state, kvp.Key);
                WeakReferenceMessenger.Default.Send(new DescargaProgresoMensaje(state.AniListId, state.NumeroEpisodio, 0, isDownloading: false, isCompleted: false, isPaused: false, "", "Descarga cancelada", state.AnimeTitulo));
            }
        }
        GuardarColaPendiente();
    }

    public void PausarDescarga(int aniListId, int numeroEpisodio)
    {
        string key = $"{aniListId}_{numeroEpisodio}";
        if (_activeDownloads.TryGetValue(key, out var state))
        {
            if (state.IsPaused) return;
            state.IsPaused = true;
            try { state.Cts.Cancel(); } catch { }

            WeakReferenceMessenger.Default.Send(new DescargaProgresoMensaje(aniListId, numeroEpisodio, state.Progreso, isDownloading: true, isCompleted: false, isPaused: true, state.RutaDestino, null, state.AnimeTitulo));
            GuardarColaPendiente();
        }
    }

    public void PausarTodas()
    {
        foreach (var kvp in _activeDownloads)
        {
            var state = kvp.Value;
            if (!state.IsPaused)
            {
                state.IsPaused = true;
                try { state.Cts.Cancel(); } catch { }
                WeakReferenceMessenger.Default.Send(new DescargaProgresoMensaje(state.AniListId, state.NumeroEpisodio, state.Progreso, isDownloading: true, isCompleted: false, isPaused: true, state.RutaDestino, null, state.AnimeTitulo));
            }
        }
        GuardarColaPendiente();
    }

    public void ReanudarDescarga(int aniListId, int numeroEpisodio)
    {
        string key = $"{aniListId}_{numeroEpisodio}";
        if (_activeDownloads.TryGetValue(key, out var state) && state.IsPaused)
        {
            state.IsPaused = false;
            state.Cts = new CancellationTokenSource();
            state.EnCola = true;
            WeakReferenceMessenger.Default.Send(new DescargaProgresoMensaje(aniListId, numeroEpisodio, state.Progreso, isDownloading: true, isCompleted: false, isPaused: false, state.RutaDestino, null, state.AnimeTitulo));
            RelanzarDescarga(state, key);
            GuardarColaPendiente();
        }
    }

    /// <summary>
    /// Reanuda por el mismo camino por el que iba la descarga: un torrent (elegido a mano o
    /// hallado en Nyaa) sigue con ese torrent y retoma sus piezas; si no, el bucle HTTP.
    /// </summary>
    private void RelanzarDescarga(DownloadState state, string key)
    {
        if (state.Torrent is CandidatoTorrent torrent && _torrentDownloadService != null)
        {
            EjecutarDescargaTorrentAsync(state, key, torrent);
        }
        else
        {
            EjecutarBucleDescargaAsync(state);
        }
    }

    /// <summary>
    /// Cancelar la saca de la lista antes de detenerla; pausar la deja. Se decide por eso y no por
    /// IsPaused, que Reanudar vuelve a poner en false mientras esta tarea aún se detiene.
    /// </summary>
    private bool FueCanceladaDefinitivamente(DownloadState state, string key)
        => !_activeDownloads.TryGetValue(key, out var actual) || !ReferenceEquals(actual, state);

    private static string RutaCarpetaTemporalTorrent(string key)
        => Path.Combine(Path.GetTempPath(), "AnimeLocalTrackerTorrents", key);

    /// <summary>
    /// Borra las piezas de un torrent cancelado de forma definitiva. Solo se llama cuando ninguna
    /// tarea sigue usando la carpeta: tras detenerse el torrent, o al cancelar uno ya en pausa.
    /// </summary>
    private static void EliminarTemporalTorrent(string key)
    {
        string carpeta = RutaCarpetaTemporalTorrent(key);
        try
        {
            if (Directory.Exists(carpeta)) Directory.Delete(carpeta, recursive: true);
        }
        catch (Exception ex)
        {
            AppLogger.Debug("DownloadService", $"No se pudo borrar la carpeta temporal del torrent cancelado: {ex.Message}");
        }
    }

    /// <summary>
    /// Al cancelar: una descarga en pausa no tiene tarea viva, así que sus temporales se borran aquí.
    /// Si está en marcha (o en cola), los borra su propia tarea al terminar de detenerse: hacerlo
    /// aquí fallaba con "el archivo está en uso" porque las conexiones aún lo tenían abierto.
    /// </summary>
    private void LimpiarTemporalesSiNoHayTareaViva(DownloadState state, string key)
    {
        if (!state.IsPaused) return;
        _stateStore.EliminarArchivosTemporales(state.RutaTemporal);
        if (state.Torrent != null) EliminarTemporalTorrent(key);
    }

    public void ReanudarTodas()
    {
        foreach (var kvp in _activeDownloads)
        {
            var state = kvp.Value;
            if (state.IsPaused)
            {
                state.IsPaused = false;
                state.Cts = new CancellationTokenSource();
                state.EnCola = true;
                WeakReferenceMessenger.Default.Send(new DescargaProgresoMensaje(state.AniListId, state.NumeroEpisodio, state.Progreso, isDownloading: true, isCompleted: false, isPaused: false, state.RutaDestino, null, state.AnimeTitulo));
                RelanzarDescarga(state, kvp.Key);
            }
        }
        GuardarColaPendiente();
    }

    /// <summary>Una descarga de la cola tal como se guarda en disco.</summary>
    internal sealed record DescargaPendiente(int AniListId, string AnimeTitulo, string CarpetaDestino, int NumeroEpisodio,
        List<string> Titulos, bool Automatica, bool Pausada, double Progreso, long Orden, CandidatoTorrent? Torrent);

    /// <summary>
    /// Guarda la cola en disco tras cada cambio: antes, cerrar la app (o que Windows la cerrara) perdía la lista de descargas en
    /// curso o esperando conexión; lo ya bajado sí quedaba (.downloading + .state) pero nadie lo retomaba.
    /// </summary>
    private void GuardarColaPendiente()
    {
        if (_rutaColaPendiente == null) return;
        try
        {
            var lista = _activeDownloads.Values.OrderBy(s => s.Orden)
                .Select(s => new DescargaPendiente(s.AniListId, s.AnimeTitulo, s.CarpetaDestino, s.NumeroEpisodio, s.Titulos, s.Automatica,
                    s.IsPaused, s.Progreso, s.Orden, s.Torrent))
                .ToList();
            string json = System.Text.Json.JsonSerializer.Serialize(lista);
            lock (_lockCola)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_rutaColaPendiente)!);
                string temporal = _rutaColaPendiente + ".tmp";
                File.WriteAllText(temporal, json);
                File.Move(temporal, _rutaColaPendiente, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("DownloadService", $"No se pudo guardar la cola de descargas: {ex.Message}");
        }
    }

    /// <summary>
    /// Al abrir la app: vuelve a poner en la cola lo que quedó pendiente. Las pausadas siguen en pausa; el resto se reanuda
    /// desde donde iba (y si no hay internet, espera a que vuelva como cualquier descarga).
    /// </summary>
    public int RestaurarColaPendiente()
    {
        if (_rutaColaPendiente == null || !File.Exists(_rutaColaPendiente)) return 0;

        List<DescargaPendiente>? pendientes;
        try
        {
            lock (_lockCola) pendientes = System.Text.Json.JsonSerializer.Deserialize<List<DescargaPendiente>>(File.ReadAllText(_rutaColaPendiente));
        }
        catch (Exception ex)
        {
            AppLogger.Warn("DownloadService", $"No se pudo leer la cola de descargas guardada: {ex.Message}");
            return 0;
        }

        int restauradas = 0;
        foreach (var p in (pendientes ?? new List<DescargaPendiente>()).OrderBy(p => p.Orden))
        {
            if (p.AniListId <= 0 || p.NumeroEpisodio <= 0 || string.IsNullOrWhiteSpace(p.CarpetaDestino)) continue;
            if (p.Torrent != null && _torrentDownloadService == null) continue;

            string key = $"{p.AniListId}_{p.NumeroEpisodio}";
            var state = new DownloadState
            {
                Orden = Interlocked.Increment(ref _ordenCounter),
                AniListId = p.AniListId,
                AnimeTitulo = p.AnimeTitulo,
                Titulos = p.Titulos is { Count: > 0 } ? p.Titulos : ConstruirListaTitulos(p.AnimeTitulo, null),
                NumeroEpisodio = p.NumeroEpisodio,
                Progreso = p.Progreso,
                RutaDestino = Path.Combine(p.CarpetaDestino, $"Episodio {p.NumeroEpisodio:D2}.mp4"),
                CarpetaDestino = p.CarpetaDestino,
                Automatica = p.Automatica,
                Torrent = p.Torrent,
                IsPaused = p.Pausada
            };
            state.RutaTemporal = state.RutaDestino + ".downloading";

            if (File.Exists(state.RutaDestino)) continue; // ya terminó (la app se cerró justo al acabar)
            if (!_activeDownloads.TryAdd(key, state)) continue;
            restauradas++;

            WeakReferenceMessenger.Default.Send(new DescargaProgresoMensaje(state.AniListId, state.NumeroEpisodio, state.Progreso, isDownloading: true, isCompleted: false,
                isPaused: state.IsPaused, state.RutaDestino, null, state.AnimeTitulo, enCola: !state.IsPaused));
            if (!state.IsPaused)
            {
                if (!Directory.Exists(state.CarpetaDestino)) Directory.CreateDirectory(state.CarpetaDestino);
                RelanzarDescarga(state, key);
            }
        }

        GuardarColaPendiente();
        if (restauradas > 0) AppLogger.Info("DownloadService", $"Cola de descargas restaurada: {restauradas} pendiente(s) de la sesión anterior.");
        return restauradas;
    }

    public IReadOnlyList<AnimeLocalTracker.Models.DescargaItem> ObtenerDescargasActivas()
    {
        return _activeDownloads.Values
            .OrderBy(s => s.Orden)
            .Select(s => new AnimeLocalTracker.Models.DescargaItem
            {
                AniListId = s.AniListId,
                AnimeTitulo = s.AnimeTitulo,
                NumeroEpisodio = s.NumeroEpisodio,
                Fuente = s.Fuente,
                Progreso = s.Progreso,
                IsDownloading = true,
                IsCompleted = false,
                IsPaused = s.IsPaused,
                RutaArchivo = s.RutaDestino,
                EnCola = s.EnCola && !s.IsPaused,
                Orden = s.Orden,
                Reintentos = s.Reintentos
            })
            .ToList();
    }

    public Task IniciarDescargaEpisodioAsync(int aniListId, string animeTitulo, string carpetaDestino, int numeroEpisodio, IEnumerable<string>? titulosAlternativos = null)
        => IniciarInterno(aniListId, animeTitulo, carpetaDestino, numeroEpisodio, titulosAlternativos, automatica: false);

    public Task IniciarDescargaAutomaticaAsync(int aniListId, string animeTitulo, string carpetaDestino, int numeroEpisodio, IEnumerable<string>? titulosAlternativos = null)
        => IniciarInterno(aniListId, animeTitulo, carpetaDestino, numeroEpisodio, titulosAlternativos, automatica: true);

    /// <summary>
    /// Fase 2d: descarga por torrent el candidato que el usuario eligió a mano (desde
    /// el selector de la ficha del anime), sin pasar por el resolver HTTP ni por la
    /// búsqueda automática de Nyaa — va directo a <see cref="ITorrentDownloadService"/>
    /// con ese candidato. No-op si ya hay una descarga activa para ese episodio.
    /// </summary>
    public Task IniciarDescargaTorrentManualAsync(int aniListId, string animeTitulo, string carpetaDestino, int numeroEpisodio, CandidatoTorrent candidatoElegido, IEnumerable<string>? titulosAlternativos = null)
    {
        if (_torrentDownloadService == null) return Task.CompletedTask;

        string key = $"{aniListId}_{numeroEpisodio}";
        if (_activeDownloads.ContainsKey(key)) return Task.CompletedTask;

        var state = new DownloadState
        {
            Orden = Interlocked.Increment(ref _ordenCounter),
            AniListId = aniListId,
            AnimeTitulo = animeTitulo,
            Titulos = ConstruirListaTitulos(animeTitulo, titulosAlternativos),
            NumeroEpisodio = numeroEpisodio,
            Progreso = 0,
            RutaDestino = Path.Combine(carpetaDestino, $"Episodio {numeroEpisodio:D2}.mp4"),
            Automatica = false,
            Torrent = candidatoElegido,
        };
        state.RutaTemporal = state.RutaDestino + ".downloading";
        state.CarpetaDestino = carpetaDestino;

        if (!_activeDownloads.TryAdd(key, state)) return Task.CompletedTask;
        GuardarColaPendiente();
        if (!Directory.Exists(carpetaDestino)) Directory.CreateDirectory(carpetaDestino);

        WeakReferenceMessenger.Default.Send(new DescargaProgresoMensaje(aniListId, numeroEpisodio, 0, isDownloading: true, isCompleted: false, isPaused: false, "", null, animeTitulo, enCola: true));

        EjecutarDescargaTorrentAsync(state, key, candidatoElegido);
        return Task.CompletedTask;
    }

    private static List<string> ConstruirListaTitulos(string animeTitulo, IEnumerable<string>? titulosAlternativos)
    {
        var todosLosTitulos = new List<string> { animeTitulo };
        if (titulosAlternativos != null)
        {
            foreach (var alt in titulosAlternativos)
            {
                if (!string.IsNullOrWhiteSpace(alt) && !todosLosTitulos.Contains(alt, StringComparer.OrdinalIgnoreCase))
                {
                    todosLosTitulos.Add(alt);
                }
            }
        }
        return todosLosTitulos;
    }

    private Task IniciarInterno(int aniListId, string animeTitulo, string carpetaDestino, int numeroEpisodio, IEnumerable<string>? titulosAlternativos, bool automatica)
    {
        string key = $"{aniListId}_{numeroEpisodio}";
        if (_activeDownloads.ContainsKey(key)) return Task.CompletedTask;

        var todosLosTitulos = ConstruirListaTitulos(animeTitulo, titulosAlternativos);

        var state = new DownloadState
        {
            Orden = Interlocked.Increment(ref _ordenCounter),
            AniListId = aniListId,
            AnimeTitulo = animeTitulo,
            Titulos = todosLosTitulos,
            NumeroEpisodio = numeroEpisodio,
            Progreso = 0,
            RutaDestino = Path.Combine(carpetaDestino, $"Episodio {numeroEpisodio:D2}.mp4"),
            Automatica = automatica
        };
        state.RutaTemporal = state.RutaDestino + ".downloading";
        state.CarpetaDestino = carpetaDestino;

        if (!_activeDownloads.TryAdd(key, state)) return Task.CompletedTask;
        GuardarColaPendiente();

        if (!Directory.Exists(carpetaDestino))
        {
            Directory.CreateDirectory(carpetaDestino);
        }

        WeakReferenceMessenger.Default.Send(new DescargaProgresoMensaje(aniListId, numeroEpisodio, 0, isDownloading: true, isCompleted: false, isPaused: false, "", null, animeTitulo, enCola: true));

        EjecutarBucleDescargaAsync(state);
        return Task.CompletedTask;
    }

    /// <summary>
    /// True si Windows indica que no hay internet y aún queda margen para esperarlo. El margen
    /// (<see cref="MaxEsperasSinConexion"/> esperas de hasta un minuto) evita esperar para siempre
    /// si el indicador de Windows se equivoca (redes que bloquean su comprobación).
    /// </summary>
    private bool PuedeEsperarConexion(DownloadState state)
        => _conectividad != null && state.EsperasSinConexion < MaxEsperasSinConexion && !_conectividad.HayInternet;

    /// <summary>
    /// Si no hay internet, marca la descarga como "sin conexión" en la UI y espera hasta un minuto
    /// a que vuelva (cancelable con pausa/cancelar). Tras cada espera se vuelve a intentar.
    /// </summary>
    private async Task EsperarConexionSiHaceFaltaAsync(DownloadState state, CancellationToken ct)
    {
        if (!PuedeEsperarConexion(state)) return;

        state.EsperasSinConexion++;
        WeakReferenceMessenger.Default.Send(new DescargaProgresoMensaje(state.AniListId, state.NumeroEpisodio, state.Progreso, isDownloading: true, isCompleted: false, isPaused: false, state.RutaDestino, null, state.AnimeTitulo, reintentos: state.Reintentos, sinConexion: true));

        if (await _conectividad!.EsperarInternetAsync(EsperaMaximaSinConexion, ct))
        {
            AppLogger.Info("DownloadService", $"Volvió la conexión: se reanuda '{state.AnimeTitulo}' Ep {state.NumeroEpisodio}.");
            WeakReferenceMessenger.Default.Send(new DescargaProgresoMensaje(state.AniListId, state.NumeroEpisodio, state.Progreso, isDownloading: true, isCompleted: false, isPaused: false, state.RutaDestino, null, state.AnimeTitulo, reintentos: state.Reintentos));
        }
    }

    private void EjecutarBucleDescargaAsync(DownloadState state)
    {
        string key = $"{state.AniListId}_{state.NumeroEpisodio}";

        // El token de ESTA ejecución: Reanudar crea otro en state.Cts, y esta tarea (que puede seguir
        // deteniéndose tras la pausa) no debe confundir el nuevo con el suyo.
        var ct = state.Cts.Token;
        _ = Task.Run(async () =>
        {
            bool slotAdquirido = false;
            int reintentosResolucion = 0;
            int reintentosDescarga = 0;
            int intentosSinRespuesta = 0;
            double progresoAlUltimoCorte = state.Progreso;
            try
            {
                await AdquirirSlotAsync(key, state, ct);
                slotAdquirido = true;
                state.EnCola = false;

                if (ct.IsCancellationRequested) return;

                WeakReferenceMessenger.Default.Send(new DescargaProgresoMensaje(state.AniListId, state.NumeroEpisodio, state.Progreso, isDownloading: true, isCompleted: false, isPaused: false, state.RutaDestino, null, state.AnimeTitulo, enCola: false, reintentos: state.Reintentos));

                IProgress<(double Progress, double Speed)>? progress = null;
                bool descargaCompletada = false;

                // El enlace ya resuelto puede ser rechazado por el servidor (firma caducada,
                // 403/404/410): se re-resuelve UNA vez con un enlace nuevo antes de fallar.
                while (!descargaCompletada)
                {
                    if (ct.IsCancellationRequested) return;

                    if (string.IsNullOrEmpty(state.VideoUrl))
                    {
                        // Sin internet la búsqueda no encuentra nada y el episodio se daría por inexistente.
                        await EsperarConexionSiHaceFaltaAsync(state, ct);

                        var configuracion = _settingsService?.ObtenerConfiguracion();
                        string? audioPreferido = configuracion?.PreferenciaAudioAnimeAv1;
                        string? servidorPreferido = configuracion?.ServidorPreferidoAnimeAv1;
                        state.VideoUrl = await _sourceResolver.BuscarUrlEpisodioAsync(state.Titulos, state.NumeroEpisodio, state.AniListId, audioPreferido, servidorPreferido, ct);
                        if (string.IsNullOrEmpty(state.VideoUrl))
                        {
                            if (ct.IsCancellationRequested) return;

                            // La red se cayó durante la búsqueda: repetirla cuando vuelva, no darlo por no encontrado.
                            if (PuedeEsperarConexion(state)) continue;

                            // Último recurso (opt-in, Fase 1c): buscar y descargar por torrent en
                            // Nyaa.si antes de darlo por no encontrado.
                            if (configuracion?.BusquedaTorrentHabilitada == true && _nyaaSourceService != null && _torrentDownloadService != null)
                            {
                                if (await IntentarDescargaTorrentAsync(state, key, ct)) return;
                                if (ct.IsCancellationRequested) return;
                            }

                            // FUN-016: trazabilidad del ciclo de descarga en app.log (antes solo Debug)
                            AppLogger.Warn("DownloadService", $"No se encontró enlace para '{state.AnimeTitulo}' Ep {state.NumeroEpisodio}.");
                            _activeDownloads.TryRemove(key, out _); GuardarColaPendiente();
                            string errorNoEncontrado = $"No se encontró el episodio {state.NumeroEpisodio} en el servidor.";
                            WeakReferenceMessenger.Default.Send(new DescargaProgresoMensaje(state.AniListId, state.NumeroEpisodio, 0, isDownloading: false, isCompleted: false, isPaused: false, "", errorNoEncontrado, state.AnimeTitulo));
                            // Descarga automática: el episodio puede tardar horas en aparecer en el servidor; cada intento
                            // fallido no debe llenar el historial (el monitor reintenta con espera creciente).
                            if (!state.Automatica) RegistrarEnHistorial(state, completada: false, errorNoEncontrado);
                            return;
                        }
                    }

                    if (ct.IsCancellationRequested) return;

                    progress ??= new Progress<(double Progress, double Speed)>(p =>
                    {
                        state.Progreso = p.Progress;
                        string speedText = FormatearVelocidad(p.Speed);
                        WeakReferenceMessenger.Default.Send(new DescargaProgresoMensaje(state.AniListId, state.NumeroEpisodio, p.Progress, isDownloading: true, isCompleted: false, isPaused: false, state.RutaDestino, null, state.AnimeTitulo, speedText, velocidadBps: p.Speed, reintentos: state.Reintentos));
                    });

                    try
                    {
                        await DownloadVideoAsync(state.VideoUrl, state.RutaTemporal, progress, ct);

                        // Un enlace caducado a veces devuelve una página de error con código 200:
                        // si lo descargado no es un video se descarta y se repite limpio.
                        if (!Core.UrlSeguridad.EsUrlManifiestoStreaming(state.VideoUrl) && !ArchivoPareceVideo(state.RutaTemporal))
                        {
                            _stateStore.EliminarArchivosTemporales(state.RutaTemporal);
                            throw new IOException("El archivo descargado no es un video válido (el servidor devolvió una página de error).");
                        }
                        descargaCompletada = true;
                    }
                    catch (Exception ex) when (EsRechazoDeEnlace(ex) && reintentosResolucion == 0 && !ct.IsCancellationRequested)
                    {
                        reintentosResolucion++;
                        AppLogger.Warn("DownloadService", $"El servidor rechazó el enlace de '{state.AnimeTitulo}' Ep {state.NumeroEpisodio} ({(int?)((HttpRequestException)ex).StatusCode}): re-resolviendo el episodio (intento 2).");
                        // El enlace nuevo suele apuntar al mismo archivo: lo bajado por trozos se conserva y se
                        // reanuda (si el archivo resulta ser otro, el tamaño no coincide y se empieza limpio).
                        // El modo secuencial no tiene esa comprobación, así que su parcial sí se descarta.
                        if (!File.Exists(state.RutaTemporal + ".state")) EliminarParcialSeguro(state.RutaTemporal);
                        state.VideoUrl = null;
                    }
                    // FUN-017: los cortes de red (timeouts, conexión reiniciada, inactividad) son
                    // habituales en servidores de streaming gratuitos y no implican que el enlace
                    // sea inválido. Se reintenta varias veces conservando el progreso (el .state y
                    // el archivo parcial no se borran) en vez de abandonar la descarga a la primera.
                    catch (Exception ex) when (EsErrorTransitorioDeRed(ex) && !ct.IsCancellationRequested)
                    {
                        // Sin internet (wifi caído, router reiniciándose) no se gastan reintentos:
                        // se espera a que vuelva la red y se sigue desde donde iba.
                        if (PuedeEsperarConexion(state))
                        {
                            AppLogger.Warn("DownloadService", $"Sin conexión a internet descargando '{state.AnimeTitulo}' Ep {state.NumeroEpisodio} ({ex.Message}): se espera a que vuelva la red sin perder el progreso.");
                            await EsperarConexionSiHaceFaltaAsync(state, ct);
                            continue;
                        }

                        // Si la descarga avanzó desde el corte anterior, son cortes sueltos de un servidor
                        // inestable (no un fallo persistente): no deben sumar hacia el abandono.
                        bool avanzo = state.Progreso > progresoAlUltimoCorte;
                        if (avanzo) { reintentosDescarga = 0; intentosSinRespuesta = 0; }
                        progresoAlUltimoCorte = state.Progreso;

                        // Un servidor que ni siquiera contesta a tiempo (caído o saturado) no suele
                        // recuperarse en el minuto siguiente: tras un par de intentos se da por perdido.
                        if (!avanzo && EsTiempoDeEsperaAgotado(ex)) intentosSinRespuesta++;

                        if (reintentosDescarga >= MaxReintentosDescargaTransitoria || intentosSinRespuesta >= MaxIntentosSinRespuesta)
                        {
                            // Antes de rendirse: si el usuario tiene activada la búsqueda por torrent, el
                            // mismo episodio suele estar en Nyaa aunque el servidor de video esté caído.
                            if (await IntentarTorrentTrasFalloHttpAsync(state, key, ex, ct)) return;
                            throw;
                        }

                        reintentosDescarga++;
                        state.Reintentos = reintentosDescarga;
                        var espera = TimeSpan.FromSeconds(Math.Min(2 * reintentosDescarga, 15));
                        AppLogger.Warn("DownloadService", $"Fallo transitorio de red descargando '{state.AnimeTitulo}' Ep {state.NumeroEpisodio} (intento {reintentosDescarga}/{MaxReintentosDescargaTransitoria}): {ex.Message}. Reintentando en {espera.TotalSeconds:F0}s sin perder el progreso.");
                        WeakReferenceMessenger.Default.Send(new DescargaProgresoMensaje(state.AniListId, state.NumeroEpisodio, state.Progreso, isDownloading: true, isCompleted: false, isPaused: false, state.RutaDestino, null, state.AnimeTitulo, reintentos: reintentosDescarga));
                        await Task.Delay(espera, ct);
                    }
                }

                if (File.Exists(state.RutaDestino)) File.Delete(state.RutaDestino);
                File.Move(state.RutaTemporal, state.RutaDestino);
                _stateStore.EliminarArchivosTemporales(state.RutaTemporal);

                _activeDownloads.TryRemove(key, out _); GuardarColaPendiente();
                WeakReferenceMessenger.Default.Send(new DescargaProgresoMensaje(state.AniListId, state.NumeroEpisodio, 100, isDownloading: false, isCompleted: true, isPaused: false, state.RutaDestino, null, state.AnimeTitulo));
                RegistrarEnHistorial(state, completada: true, null);
            }
            // Solo una pausa o cancelación del usuario es "interrupción". Un tiempo de espera agotado
            // también llega como OperationCanceledException (TaskCanceledException), y antes caía aquí:
            // la descarga desaparecía de la lista sin error ni rastro en el historial.
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                bool cancelada = FueCanceladaDefinitivamente(state, key);
                AppLogger.Info("DownloadService", $"Descarga interrumpida: {state.AnimeTitulo} Ep {state.NumeroEpisodio}. Pausado: {!cancelada}");
                if (cancelada)
                {
                    _stateStore.EliminarArchivosTemporales(state.RutaTemporal);
                    if (state.Torrent != null) EliminarTemporalTorrent(key);
                }
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested)
                {
                    AppLogger.Debug("DownloadService", $"Descarga pausada generó excepción esperada: {ex.Message}");
                    return;
                }

                AppLogger.Error("DownloadService", $"Error descargando {state.AnimeTitulo} Ep {state.NumeroEpisodio}", ex);
                // FUN-017: no se borra el archivo parcial ni el .state aquí — ya se agotaron los
                // reintentos automáticos, pero conservar el progreso permite que un reintento manual
                // del usuario retome la descarga en vez de empezar desde cero.
                _activeDownloads.TryRemove(key, out _); GuardarColaPendiente();
                string error = DescribirErrorParaUsuario(ex);
                WeakReferenceMessenger.Default.Send(new DescargaProgresoMensaje(state.AniListId, state.NumeroEpisodio, 0, isDownloading: false, isCompleted: false, isPaused: false, "", error, state.AnimeTitulo));
                RegistrarEnHistorial(state, completada: false, error);
            }
            finally
            {
                if (slotAdquirido)
                {
                    LiberarSlot();
                }
            }
        });
    }

    /// <summary>Tiempo de espera agotado (conectar, negociar la conexión segura o esperar respuesta), no una pausa del usuario.</summary>
    private static bool EsTiempoDeEsperaAgotado(Exception ex)
        => ex is OperationCanceledException || ex.InnerException is TimeoutException or OperationCanceledException;

    /// <summary>"The operation was canceled." no le dice nada al usuario: los tiempos agotados se explican.</summary>
    private static string DescribirErrorParaUsuario(Exception ex)
        => EsTiempoDeEsperaAgotado(ex) ? LocalizationService.T("Desc_ErrorServidorNoResponde") : ex.Message;

    /// <summary>
    /// El servidor de video (MP4Upload) falló del todo: si la búsqueda por torrent está activada se
    /// prueba Nyaa antes de dar la descarga por fallida. True si el torrent la completó.
    /// </summary>
    private async Task<bool> IntentarTorrentTrasFalloHttpAsync(DownloadState state, string key, Exception fallo, CancellationToken ct)
    {
        var configuracion = _settingsService?.ObtenerConfiguracion();
        if (configuracion?.BusquedaTorrentHabilitada != true || _nyaaSourceService == null || _torrentDownloadService == null) return false;

        AppLogger.Warn("DownloadService", $"El servidor de video falló para '{state.AnimeTitulo}' Ep {state.NumeroEpisodio} ({fallo.Message}): se prueba por torrent (Nyaa).");
        if (!await IntentarDescargaTorrentAsync(state, key, ct)) return false;

        // Lo bajado por HTTP ya no sirve: el episodio llegó completo por torrent.
        _stateStore.EliminarArchivosTemporales(state.RutaTemporal);
        return true;
    }

    /// <summary>
    /// Último recurso (Fase 1c, opt-in): busca el episodio en Nyaa.si y, si hay un
    /// release de un solo episodio con semillas suficientes, lo descarga por
    /// BitTorrent directo a <see cref="DownloadState.RutaDestino"/> (el motor de
    /// torrent maneja su propia carpeta temporal, no <see cref="DownloadState.RutaTemporal"/>).
    /// True si terminó de descargar con éxito (ya registró el historial y avisó a la
    /// UI); false si no encontró nada o la descarga falló — el llamador cae al
    /// mensaje de "no encontrado" habitual.
    /// </summary>
    private async Task<bool> IntentarDescargaTorrentAsync(DownloadState state, string key, CancellationToken ct)
    {
        var configuracion = _settingsService?.ObtenerConfiguracion();

        List<CandidatoTorrent> candidatos;
        try
        {
            candidatos = await _nyaaSourceService!.BuscarCandidatosAsync(
                state.Titulos, state.NumeroEpisodio,
                configuracion?.GrupoFansubPreferidoTorrent, configuracion?.ResolucionPreferidaTorrent,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            AppLogger.Debug("DownloadService", $"Búsqueda en Nyaa falló para '{state.AnimeTitulo}' Ep {state.NumeroEpisodio}: {ex.Message}");
            return false;
        }

        // Si el mejor candidato no arranca (sin fuentes reales pese a las semillas que anuncia Nyaa,
        // o un error del torrent), se prueba el siguiente en vez de rendirse a la primera.
        foreach (var candidato in candidatos.Take(MaxCandidatosTorrentAutomaticos))
        {
            AppLogger.Info("DownloadService", $"'{state.AnimeTitulo}' Ep {state.NumeroEpisodio}: probando Nyaa/torrent \"{candidato.Titulo}\" ({candidato.Seeders} semillas).");
            state.Torrent = candidato;
            var (exito, motivo) = await DescargarTorrentYCompletarAsync(state, key, candidato, configuracion?.SeguirSembrandoTorrents ?? false, ct);
            if (exito) return true;
            if (ct.IsCancellationRequested) return false;
            AppLogger.Info("DownloadService", $"El torrent \"{candidato.Titulo}\" no funcionó ({motivo}).");
        }

        state.Torrent = null;
        return false;
    }

    /// <summary>
    /// Fase 2d: descarga el candidato de torrent YA ELEGIDO (el usuario lo escogió a
    /// mano, sin pasar por la búsqueda automática de Nyaa ni por el resolver HTTP) y,
    /// si tiene éxito, marca el historial/UI. También retoma un torrent en pausa (manual o
    /// hallado en Nyaa). Comparte el mismo tramo final que
    /// <see cref="IntentarDescargaTorrentAsync"/> (<see cref="DescargarTorrentYCompletarAsync"/>)
    /// para no duplicar la lógica de "descargar y completar".
    /// </summary>
    private void EjecutarDescargaTorrentAsync(DownloadState state, string key, CandidatoTorrent candidato)
    {
        var ct = state.Cts.Token; // ver EjecutarBucleDescargaAsync
        _ = Task.Run(async () =>
        {
            bool slotAdquirido = false;
            try
            {
                await AdquirirSlotAsync(key, state, ct);
                slotAdquirido = true;
                state.EnCola = false;

                if (ct.IsCancellationRequested) return;

                WeakReferenceMessenger.Default.Send(new DescargaProgresoMensaje(state.AniListId, state.NumeroEpisodio, state.Progreso, isDownloading: true, isCompleted: false, isPaused: false, state.RutaDestino, null, state.AnimeTitulo, enCola: false));

                bool seguirSembrando = _settingsService?.ObtenerConfiguracion()?.SeguirSembrandoTorrents ?? false;
                var (exito, motivo) = await DescargarTorrentYCompletarAsync(state, key, candidato, seguirSembrando, ct);
                if (!exito)
                {
                    _activeDownloads.TryRemove(key, out _); GuardarColaPendiente();
                    string error = string.IsNullOrWhiteSpace(motivo) ? "No se pudo descargar el torrent elegido." : motivo;
                    WeakReferenceMessenger.Default.Send(new DescargaProgresoMensaje(state.AniListId, state.NumeroEpisodio, 0, isDownloading: false, isCompleted: false, isPaused: false, "", error, state.AnimeTitulo));
                    RegistrarEnHistorial(state, completada: false, error);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                bool cancelada = FueCanceladaDefinitivamente(state, key);
                AppLogger.Info("DownloadService", $"Descarga por torrent interrumpida: {state.AnimeTitulo} Ep {state.NumeroEpisodio}. Pausado: {!cancelada}");
                if (cancelada) EliminarTemporalTorrent(key);
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) return;
                AppLogger.Error("DownloadService", $"Error en descarga por torrent (manual) {state.AnimeTitulo} Ep {state.NumeroEpisodio}", ex);
                _activeDownloads.TryRemove(key, out _); GuardarColaPendiente();
                string error = DescribirErrorParaUsuario(ex);
                WeakReferenceMessenger.Default.Send(new DescargaProgresoMensaje(state.AniListId, state.NumeroEpisodio, 0, isDownloading: false, isCompleted: false, isPaused: false, "", error, state.AnimeTitulo));
                RegistrarEnHistorial(state, completada: false, error);
            }
            finally
            {
                if (slotAdquirido) LiberarSlot();
            }
        });
    }

    /// <summary>
    /// Tramo común a la búsqueda automática (<see cref="IntentarDescargaTorrentAsync"/>)
    /// y a la elección manual (<see cref="EjecutarDescargaTorrentManualAsync"/>): pide
    /// la descarga a <see cref="ITorrentDownloadService"/>, reporta progreso, y si
    /// tiene éxito marca la descarga como completada (Fuente="Nyaa", mensaje de UI,
    /// historial). Exito=true si terminó; si no, Error explica por qué (para mostrarlo al usuario).
    /// </summary>
    private async Task<(bool Exito, string? Error)> DescargarTorrentYCompletarAsync(DownloadState state, string key, CandidatoTorrent candidato, bool seguirSembrando, CancellationToken ct)
    {
        var progress = new Progress<(double Progreso, double VelocidadBps)>(p =>
        {
            state.Progreso = p.Progreso;
            string speedText = FormatearVelocidad(p.VelocidadBps);
            WeakReferenceMessenger.Default.Send(new DescargaProgresoMensaje(state.AniListId, state.NumeroEpisodio, p.Progreso, isDownloading: true, isCompleted: false, isPaused: false, state.RutaDestino, null, state.AnimeTitulo, speedText, velocidadBps: p.VelocidadBps));
        });

        string carpetaTemporalTorrent = RutaCarpetaTemporalTorrent(key);
        ResultadoTorrent resultado;
        try
        {
            resultado = await _torrentDownloadService!.DescargarAsync(
                candidato.TorrentUrl, carpetaTemporalTorrent, state.RutaDestino, state.NumeroEpisodio,
                seguirSembrando: seguirSembrando,
                progress: progress, ct: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            AppLogger.Warn("DownloadService", $"Descarga por torrent falló para '{state.AnimeTitulo}' Ep {state.NumeroEpisodio}: {ex.Message}");
            return (false, ex.Message);
        }

        if (!resultado.Exito)
        {
            AppLogger.Warn("DownloadService", $"Torrent no se pudo descargar para '{state.AnimeTitulo}' Ep {state.NumeroEpisodio}: {resultado.Error}");
            return (false, resultado.Error);
        }

        state.Fuente = "Nyaa";
        _activeDownloads.TryRemove(key, out _); GuardarColaPendiente();
        WeakReferenceMessenger.Default.Send(new DescargaProgresoMensaje(state.AniListId, state.NumeroEpisodio, 100, isDownloading: false, isCompleted: true, isPaused: false, state.RutaDestino, null, state.AnimeTitulo));
        RegistrarEnHistorial(state, completada: true, null);
        return (true, null);
    }

    /// <summary>
    /// Guarda el resultado FINAL (éxito o fallo definitivo) para el historial de la pestaña Descargas.
    /// Fire-and-forget: un fallo de BD nunca debe afectar a la descarga. Cancelar/pausar no se registra.
    /// </summary>
    private void RegistrarEnHistorial(DownloadState state, bool completada, string? error)
    {
        if (_database == null) return;

        _ = Task.Run(async () =>
        {
            try
            {
                long tamano = 0;
                if (completada)
                {
                    try { tamano = new FileInfo(state.RutaDestino).Length; } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }

                await _database.GuardarDescargaHistorialAsync(new AnimeLocalTracker.Models.DescargaHistorial
                {
                    AniListId = state.AniListId,
                    AnimeTitulo = state.AnimeTitulo,
                    NumeroEpisodio = state.NumeroEpisodio,
                    CarpetaDestino = string.IsNullOrEmpty(state.CarpetaDestino) ? (Path.GetDirectoryName(state.RutaDestino) ?? string.Empty) : state.CarpetaDestino,
                    RutaArchivo = completada ? state.RutaDestino : string.Empty,
                    TamanoBytes = tamano,
                    FechaUtc = DateTime.UtcNow,
                    Completada = completada,
                    Error = completada ? null : error,
                    TitulosAlternativos = string.Join(" | ", state.Titulos.Skip(1))
                });

                WeakReferenceMessenger.Default.Send(new DescargaHistorialActualizadoMensaje());
            }
            catch (Exception ex)
            {
                AppLogger.Warn("DownloadService", $"No se pudo guardar el historial de descargas: {ex.Message}");
            }
        });
    }

    public async Task<string?> GetVideoUrlAsync(string pageUrl, CancellationToken cancellationToken = default)
    {
        return await _sourceResolver.GetVideoUrlAsync(pageUrl, cancellationToken);
    }

    /// <summary>
    /// Descarga un stream HLS/DASH con yt-dlp en el daemon (bloqueante, sin progreso
    /// incremental en esta fase). yt-dlp puede añadir la extensión real al outtmpl:
    /// se normaliza al destino esperado.
    /// </summary>
    private async Task DescargarManifiestoConDaemonAsync(string videoUrl, string destinationPath, CancellationToken cancellationToken)
    {
        var resultado = await _pythonBridge!.ExecuteCommandOneShotAsync<object, DownloadStreamResult>(
            "download-stream",
            new { url = videoUrl, output_path = destinationPath },
            cancellationToken);

        if (resultado == null || !resultado.Success)
        {
            throw new InvalidOperationException($"No se pudo descargar el stream (HLS): {resultado?.Error ?? "respuesta vacía del daemon"}");
        }

        // yt-dlp escribe en outtmpl + extensión real → mover al destino esperado
        if (!string.IsNullOrEmpty(resultado.RutaArchivo) &&
            !resultado.RutaArchivo.Equals(destinationPath, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(resultado.RutaArchivo))
        {
            if (File.Exists(destinationPath)) File.Delete(destinationPath);
            File.Move(resultado.RutaArchivo, destinationPath);
        }
        else if (!File.Exists(destinationPath))
        {
            throw new InvalidOperationException("El daemon no generó el archivo esperado.");
        }
    }

    /// <summary>DTO de la respuesta del daemon para download-stream. Público para testeo.</summary>
    public class DownloadStreamResult
    {
        public bool Success { get; set; }
        public string? RutaArchivo { get; set; }
        public string? Error { get; set; }
    }

    public async Task DownloadVideoAsync(string videoUrl, string destinationPath, IProgress<(double Progress, double Speed)>? progress = null, CancellationToken cancellationToken = default)
    {
        // Hardening INT-01 (defensa en profundidad): el punto de descarga solo acepta
        // https sin credenciales, venga la URL de donde venga (scraper, yt-dlp o entrada).
        if (!Core.UrlSeguridad.EsUrlDescargaHttpSegura(videoUrl))
        {
            AppLogger.Warn("DownloadService", "URL de video rechazada: no es https o contiene credenciales embebidas.");
            throw new InvalidOperationException("La URL del video no es segura (solo se admiten enlaces https).");
        }

        // Fase 2: los manifiestos HLS/DASH no son archivos directos — se descargan
        // con yt-dlp en el daemon (segmentos, encriptación y merge los maneja yt-dlp)
        if (_pythonBridge != null && Core.UrlSeguridad.EsUrlManifiestoStreaming(videoUrl))
        {
            await DescargarManifiestoConDaemonAsync(videoUrl, destinationPath, cancellationToken);
            return;
        }

        var dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // Obtener tamaño y verificar soporte de rangos
        long totalBytes = -1;
        bool supportsRanges = false;

        // Reanudación: si hay un archivo parcial con su .state, el tamaño y el soporte de rangos ya se conocen de la
        // primera vez. Los sondeos HEAD/GET (6 s) fallan a menudo con mp4upload; si fallaban al reanudar, se caía al
        // modo secuencial, el servidor devolvía 200 al Range y la descarga volvía a empezar desde cero.
        long totalGuardado = LeerTotalDeEstadoGuardado(destinationPath);
        bool reanudandoSegmentada = totalGuardado > 3 * 1024 * 1024;
        if (reanudandoSegmentada)
        {
            totalBytes = totalGuardado;
            supportsRanges = true;
            AppLogger.Info("DownloadService", $"Reanudando descarga segmentada ({totalGuardado / (1024 * 1024)} MB) sin volver a sondear el servidor.");
        }

        if (!reanudandoSegmentada)
        {
            (totalBytes, supportsRanges) = await SondearServidorAsync(videoUrl, cancellationToken);
        }

        // Descarga segmentada en paralelo si el servidor soporta Range y conocemos el tamaño (> 3MB)
        if (supportsRanges && totalBytes > 3 * 1024 * 1024)
        {
            try
            {
                await DownloadSegmentedParallelAsync(videoUrl, destinationPath, totalBytes, progress, cancellationToken);
            }
            catch (RangoInvalidoException ex)
            {
                // El servidor prometió rangos pero no los cumple (o el archivo cambió): seguir
                // por rangos corrompería el video, así que se descarga completo en una sola conexión.
                AppLogger.Warn("DownloadService", $"{ex.Message} Se reinicia la descarga en modo secuencial.");
                _stateStore.EliminarArchivosTemporales(destinationPath);
                await DescargarSecuencialConTurnoAsync(videoUrl, destinationPath, -1, progress, cancellationToken);
            }
        }
        else
        {
            await DescargarSecuencialConTurnoAsync(videoUrl, destinationPath, totalBytes, progress, cancellationToken);
        }
    }

    private readonly record struct RespuestaSondeo(bool Exito, bool Parcial, long Total, bool AnunciaRangos, System.Net.HttpStatusCode? Estado = null);

    /// <summary>
    /// Tamaño del video y soporte de rangos. HEAD y GET Range(0,0) se lanzan A LA VEZ (antes uno
    /// detrás de otro, hasta 12 s cuando mp4upload no contestaba al HEAD): el GET con 206 lo dice
    /// todo, así que si llega primero no se espera al HEAD; si no, se combinan como antes.
    /// </summary>
    private async Task<(long Total, bool AdmiteRangos)> SondearServidorAsync(string videoUrl, CancellationToken cancellationToken)
    {
        // El plazo de cada sondeo (TiempoMaximoSondeo) corre desde que tiene conexión, no mientras
        // espera turno: con el servidor al tope por otras descargas, esperar turno no es un fallo.
        using var cancelarHead = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Si el servidor ya demostró limitar las conexiones simultáneas (otra descarga suya en marcha),
        // no se gasta una en el HEAD: el GET con rango basta para saber tamaño y soporte de rangos.
        string servidor = new Uri(videoUrl).Authority;
        bool servidorLimitado = _limitadorServidores.Tope(servidor, DateTime.UtcNow) != null;
        var head = servidorLimitado
            ? Task.FromResult(default(RespuestaSondeo))
            : SondearConTurnoAsync(videoUrl, servidor, HttpMethod.Head, conRango: false, cancelarHead.Token);
        var rango = SondearConTurnoAsync(videoUrl, servidor, HttpMethod.Get, conRango: true, cancellationToken);

        var respuestaRango = await rango;
        if (respuestaRango.Parcial && respuestaRango.Total > 0)
        {
            cancelarHead.Cancel(); // el HEAD ya no aporta nada
            await head;
            return (respuestaRango.Total, true);
        }

        var respuestaHead = await head;
        long total = respuestaHead.Exito ? respuestaHead.Total : -1;
        bool rangos = respuestaHead.Exito && respuestaHead.AnunciaRangos;

        if (total <= 0 || !rangos)
        {
            if (respuestaRango.Parcial)
            {
                rangos = true;
                if (respuestaRango.Total > 0) total = respuestaRango.Total;
            }
            else if (respuestaRango.Exito && total <= 0)
            {
                total = respuestaRango.Total;
            }
        }
        return (total, rangos);
    }

    /// <summary>
    /// Sondeo que respeta el tope de conexiones del servidor (ver <see cref="LimitadorPorServidor"/>):
    /// espera turno (sin límite de tiempo: solo la cancelación de la descarga lo corta) y, si recibe 403
    /// por exceso de conexiones, lo reintenta en vez de darlo por fallido (un sondeo fallido deja la
    /// descarga en UNA sola conexión). Uso real: con 4 episodios de a3.mp4upload.com a la vez, el sondeo
    /// del quinto agotó sus 45 s esperando turno y acabó en un falso "enlace rechazado".
    /// </summary>
    private async Task<RespuestaSondeo> SondearConTurnoAsync(string videoUrl, string servidor, HttpMethod metodo, bool conRango, CancellationToken ct)
    {
        try
        {
            for (int intento = 1; ; intento++)
            {
                var conexion = await AbrirConexionAsync(servidor, ct);
                RespuestaSondeo respuesta;
                bool rechazoPorTope;
                try
                {
                    using var plazo = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    plazo.CancelAfter(TiempoMaximoSondeo);
                    respuesta = await SondearAsync(videoUrl, metodo, conRango, plazo.Token);
                    if (respuesta.Exito) conexion.MarcarTransfiriendo(DateTime.UtcNow); // prueba de que el enlace funciona
                    rechazoPorTope = respuesta.Estado == System.Net.HttpStatusCode.Forbidden
                                     && _limitadorServidores.RegistrarRechazo(conexion, DateTime.UtcNow) != null;
                }
                finally
                {
                    conexion.Cerrar();
                }

                if (!rechazoPorTope || intento >= MaxSondeosRechazadosPorTope) return respuesta;
                await Task.Delay(EsperaConVariacion(TimeSpan.FromSeconds(1)), ct);
            }
        }
        catch (OperationCanceledException)
        {
            return default; // cancelación: la descarga la detectará enseguida
        }
    }

    private async Task<RespuestaSondeo> SondearAsync(string videoUrl, HttpMethod metodo, bool conRango, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(metodo, videoUrl);
            req.Headers.Add("User-Agent", UserAgent);
            req.Headers.Add("Referer", "https://www.mp4upload.com/");
            if (conRango) req.Headers.Range = new RangeHeaderValue(0, 0);

            using var res = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (res.StatusCode == System.Net.HttpStatusCode.PartialContent)
            {
                return new RespuestaSondeo(true, true, res.Content.Headers.ContentRange?.Length ?? -1, true);
            }
            if (!res.IsSuccessStatusCode) return new RespuestaSondeo(false, false, -1, false, res.StatusCode);

            bool anunciaRangos = res.Headers.AcceptRanges.Contains("bytes") || res.Content.Headers.ContentRange != null;
            return new RespuestaSondeo(true, false, res.Content.Headers.ContentLength ?? -1, anunciaRangos);
        }
        catch (Exception ex)
        {
            AppLogger.Debug("DownloadService", $"Sondeo {metodo}{(conRango ? " Range(0,0)" : "")} para '{SanitizarUrlParaLog(videoUrl)}' omitido/timeout: {ex.Message}");
            return default;
        }
    }

    private static bool ParcialCoincideConTamano(string destinationPath, long totalBytes)
    {
        try
        {
            var info = new FileInfo(destinationPath);
            return info.Exists && info.Length == totalBytes;
        }
        catch (Exception ex)
        {
            AppLogger.Debug("DownloadService", $"No se pudo comprobar el archivo parcial: {ex.Message}");
            return false;
        }
    }

    /// <summary>Tamaño total guardado en el .state de una descarga segmentada previa (0 si no hay o no es válido).</summary>
    private static long LeerTotalDeEstadoGuardado(string destinationPath)
    {
        try
        {
            string statePath = destinationPath + ".state";
            if (!File.Exists(statePath) || !File.Exists(destinationPath)) return 0;
            var info = System.Text.Json.JsonSerializer.Deserialize<DownloadStateInfo>(File.ReadAllText(statePath));
            if (info == null || info.TotalBytes <= 0 || info.Segments.Count == 0) return 0;
            // El archivo preasignado tiene ya el tamaño total; si no coincide, el estado no es fiable.
            return new FileInfo(destinationPath).Length == info.TotalBytes ? info.TotalBytes : 0;
        }
        catch (Exception ex)
        {
            AppLogger.Debug("DownloadService", $"No se pudo leer el estado de reanudación: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Descarga por cola de trozos: el archivo se divide en trozos de ~4 MB y N conexiones toman
    /// el siguiente trozo pendiente. Así ninguna conexión lenta retrasa el final (velocidad más
    /// estable), un corte solo repite unos MB (reintento por trozo) y el progreso se guarda cada
    /// pocos segundos para reanudar aunque la app se cierre de golpe.
    /// </summary>
    private async Task DownloadSegmentedParallelAsync(string videoUrl, string destinationPath, long totalBytes, IProgress<(double Progress, double Speed)>? progress, CancellationToken cancellationToken)
    {
        const long MaxPreallocLimitBytes = 35L * 1024 * 1024 * 1024; // 35 GB por archivo de episodio
        if (totalBytes > MaxPreallocLimitBytes)
        {
            throw new InvalidOperationException($"El tamaño declarado del video ({totalBytes / (1024 * 1024)} MB) supera el límite de seguridad de 35 GB.");
        }

        string statePath = destinationPath + ".state";
        int totalTrozos = (int)Math.Max(MinimoTrozos, (totalBytes + TamanoTrozoBytes - 1) / TamanoTrozoBytes);

        // Un .state solo describe el archivo parcial junto al que se guardó. Si ese parcial desapareció
        // (lo borró alguien mientras la descarga estaba en pausa o esperando un reintento) o no mide lo
        // que debe, reutilizarlo daría por descargados trozos que en el archivo nuevo serían ceros.
        if (File.Exists(statePath) && !ParcialCoincideConTamano(destinationPath, totalBytes))
        {
            AppLogger.Warn("DownloadService", "El archivo parcial no coincide con su estado guardado (falta o cambió de tamaño): la descarga se reinicia limpia para no dejar huecos.");
            try { File.Delete(statePath); }
            catch (Exception ex) { AppLogger.Debug("DownloadService", $"No se pudo borrar el estado huérfano: {ex.Message}"); }
        }

        DownloadStateInfo stateInfo = await _stateStore.CargarOInicializarAsync(statePath, totalBytes, totalTrozos);
        var trozos = stateInfo.Segments;

        // Verificar espacio libre en la unidad de destino
        try
        {
            var driveRoot = Path.GetPathRoot(Path.GetFullPath(destinationPath)) ?? "C:\\";
            var driveInfo = new DriveInfo(driveRoot);
            if (driveInfo.IsReady && driveInfo.AvailableFreeSpace < totalBytes + 100 * 1024 * 1024)
            {
                throw new DownloadAbortDefinitivoException($"Espacio insuficiente en disco para descargar el archivo. Se requieren {totalBytes / (1024 * 1024)} MB y solo hay {driveInfo.AvailableFreeSpace / (1024 * 1024)} MB libres.");
            }
        }
        catch (IOException) { throw; }
        catch (Exception ex)
        {
            AppLogger.Debug("DownloadService", $"Comprobación de espacio libre omitida: {ex.Message}");
        }

        bool shouldPreAlloc = !File.Exists(destinationPath);
        using (var preAlloc = new FileStream(destinationPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite, 4096, useAsync: true))
        {
            if (shouldPreAlloc || preAlloc.Length != totalBytes)
            {
                // SEC-06: el tamaño declarado proviene del servidor remoto (Content-Length/Content-Range).
                // Acotarlo evita que un servidor malicioso fuerce la reserva de todo el disco.
                preAlloc.SetLength(totalBytes);
            }
        }

        using SafeFileHandle fileHandle = File.OpenHandle(destinationPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, FileOptions.Asynchronous);

        var acumulador = new ProgresoAgregado(progress, totalBytes, trozos.Sum(s => s.CurrentOffset - s.Start));
        int siguienteTrozo = -1;
        // Se crean trabajadores para el máximo posible, pero solo trabajan los que el reparto actual
        // permite (ver ConexionesPermitidas): así una descarga que empieza sola usa todas sus
        // conexiones y cede parte cuando arrancan otras, sin reiniciar nada.
        int conexiones = Math.Min(TechoAbsolutoConexionesPorDescarga, trozos.Count);
        var control = new ControlConexionesDescarga();
        var agrupador = new AgrupadorTrozos(UmbralServidorLento);
        string servidor = new Uri(videoUrl).Authority;
        Interlocked.Increment(ref _descargasHttpActivas);

        using var trabajoCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var guardadorCts = CancellationTokenSource.CreateLinkedTokenSource(trabajoCts.Token);

        // Guardado periódico: si la app se cierra o se cae, la próxima vez se retoma desde aquí.
        var guardador = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            try
            {
                while (await timer.WaitForNextTickAsync(guardadorCts.Token))
                {
                    try { await _stateStore.GuardarAsync(statePath, stateInfo); }
                    catch (Exception ex) { AppLogger.Debug("DownloadService", $"Guardado periódico del estado omitido: {ex.Message}"); }
                }
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);

        // Tramo final: quedan menos trozos por repartir que conexiones (la velocidad baja sola).
        bool EnFinal() => trozos.Count - (Volatile.Read(ref siguienteTrozo) + 1) < ConexionesPermitidas(control);
        var ajustador = Task.Run(() => AjustarConexionesAsync(control, acumulador, servidor, agrupador, EnFinal, guardadorCts.Token), CancellationToken.None);

        var trabajadores = new Task[conexiones];
        for (int i = 0; i < conexiones; i++)
        {
            int indiceTrabajador = i;
            trabajadores[i] = Task.Run(async () =>
            {
                try
                {
                    while (!trabajoCts.IsCancellationRequested)
                    {
                        // Arranque prudente: sin pruebas recientes de que el enlace funciona (al reanudar no se
                        // sondea), primero va una sola conexión. Si las 8 se abrieran a la vez y el servidor
                        // rechazara las de más con 403, no habría forma de distinguirlo de un enlace caducado.
                        if (indiceTrabajador > 0 && !_limitadorServidores.HayExitoReciente(servidor, DateTime.UtcNow))
                        {
                            if (Volatile.Read(ref siguienteTrozo) + 1 >= trozos.Count) return;
                            await Task.Delay(IntervaloReparto, trabajoCts.Token);
                            continue;
                        }

                        if (indiceTrabajador >= ConexionesPermitidas(control))
                        {
                            // Sin turno ahora mismo: si ya no quedan trozos por repartir no hace falta esperar.
                            if (Volatile.Read(ref siguienteTrozo) + 1 >= trozos.Count) return;
                            await Task.Delay(IntervaloReparto, trabajoCts.Token);
                            continue;
                        }

                        // Servidor lento en conectar → varios trozos por petición (ver AgrupadorTrozos).
                        int restantes = trozos.Count - (Volatile.Read(ref siguienteTrozo) + 1);
                        int cuantos = agrupador.TrozosPorPeticion(restantes, ConexionesPermitidas(control));
                        int ultimo = Interlocked.Add(ref siguienteTrozo, cuantos);
                        int primero = ultimo - cuantos + 1;
                        if (primero >= trozos.Count) return;

                        foreach (var tramo in AgrupadorTrozos.ArmarTramos(trozos, primero, Math.Min(ultimo, trozos.Count - 1)))
                        {
                            await DescargarTramoConReintentosAsync(videoUrl, servidor, fileHandle, tramo, totalBytes, acumulador, control, agrupador, trabajoCts.Token);
                        }
                    }
                }
                catch
                {
                    // Un trozo agotó sus reintentos: se detiene al resto para no dejar tareas
                    // escribiendo en el archivo mientras se cierra o se reintenta la descarga.
                    trabajoCts.Cancel();
                    throw;
                }
            }, CancellationToken.None);
        }

        try { await Task.WhenAll(trabajadores); }
        catch { /* se analiza abajo, cuando TODOS los trabajadores ya terminaron */ }
        finally { Interlocked.Decrement(ref _descargasHttpActivas); }

        guardadorCts.Cancel();
        await guardador;
        await ajustador;

        var falloReal = trabajadores
            .Where(t => t.IsFaulted)
            .Select(t => t.Exception!.InnerExceptions[0])
            .FirstOrDefault(e => e is not OperationCanceledException);

        if (falloReal != null || cancellationToken.IsCancellationRequested)
        {
            // Pausa, cancelación o corte: se conserva el progreso para poder reanudar.
            await _stateStore.GuardarAsync(statePath, stateInfo);
            if (falloReal != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(falloReal).Throw();
            cancellationToken.ThrowIfCancellationRequested();
        }

        // Verificación final: nunca dar por buena una descarga con huecos o de tamaño distinto.
        if (trozos.Any(t => t.CurrentOffset <= t.End))
        {
            throw new IOException("La descarga terminó con trozos pendientes.");
        }

        RandomAccess.FlushToDisk(fileHandle);
        long tamanoReal = RandomAccess.GetLength(fileHandle);
        if (tamanoReal != totalBytes)
        {
            _stateStore.EliminarArchivosTemporales(destinationPath);
            throw new IOException($"El archivo descargado mide {tamanoReal} bytes y se esperaban {totalBytes}: se reinicia la descarga.");
        }

        progress?.Report((100.0, 0));
    }

    /// <summary>
    /// Descarga un tramo de trozos consecutivos (normalmente uno; varios si el servidor tarda en
    /// conectar), reintentando desde el último byte recibido si se corta.
    /// </summary>
    private async Task DescargarTramoConReintentosAsync(string videoUrl, string servidor, SafeFileHandle fileHandle, IReadOnlyList<SegmentState> tramo, long totalBytes, ProgresoAgregado acumulador, ControlConexionesDescarga control, AgrupadorTrozos agrupador, CancellationToken ct)
    {
        int intentosFallidos = 0;
        while (tramo[^1].CurrentOffset <= tramo[^1].End)
        {
            long bytesAntes = BytesRecibidos(tramo);
            int completosAntes = TrozosCompletos(tramo);
            try
            {
                // Turno con el servidor: si ya demostró no aceptar más conexiones a la vez, se espera a que se libere una.
                var conexion = await AbrirConexionAsync(servidor, ct);
                try
                {
                    await DescargarTramoAsync(videoUrl, fileHandle, tramo, totalBytes, acumulador, agrupador, conexion, ct);
                }
                // El filtro se evalúa ANTES del finally de abajo, con esta conexión aún abierta:
                // 403 con otras conexiones recibiendo datos = el servidor limita conexiones, no un enlace caducado.
                catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden
                                                      && ex is not ServidorSaturadoException
                                                      && !ct.IsCancellationRequested
                                                      && _limitadorServidores.RegistrarRechazo(conexion, DateTime.UtcNow) is int tope)
                {
                    throw new ServidorSaturadoException(System.Net.HttpStatusCode.Forbidden, null, tope);
                }
                finally
                {
                    conexion.Cerrar();
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested
                                       && EsErrorTransitorioDeRed(ex)
                                       && !EsRechazoDeEnlace(ex))
            {
                // Si el tramo avanzó antes de cortarse, el fallo no cuenta como intento agotado.
                if (BytesRecibidos(tramo) > bytesAntes) intentosFallidos = 0;
                if (++intentosFallidos > MaxReintentosPorTrozo) throw;

                var espera = EsperaConVariacion(TimeSpan.FromSeconds(Math.Min(1 << (intentosFallidos - 1), 8)));
                if (ex is ServidorSaturadoException { TopeConexiones: int topeServidor })
                {
                    // Ya no se reduce a la mitad: el tope aprendido para este servidor (compartido entre
                    // descargas) hace esperar a las conexiones de más. Reintento rápido: esperará su turno.
                    espera = EsperaConVariacion(TimeSpan.FromSeconds(1));
                    if (_ultimoTopeAvisado.TryGetValue(servidor, out int avisado) is false || avisado != topeServidor)
                    {
                        _ultimoTopeAvisado[servidor] = topeServidor;
                        AppLogger.Info("DownloadService", $"{servidor} limita las conexiones simultáneas: se usan como mucho {topeServidor} a la vez con ese servidor.");
                    }
                }
                else if (ex is ServidorSaturadoException saturado)
                {
                    // El servidor pide calma (429/503): menos conexiones a la vez y respetar su espera.
                    int antes = ConexionesPermitidas(control);
                    control.Reducir(ConexionesPorDescarga(), TechoConexionesPorDescarga(), DateTime.UtcNow);
                    int despues = ConexionesPermitidas(control);
                    if (despues < antes) AppLogger.Info("DownloadService", $"El servidor pidió calma: se baja de {antes} a {despues} conexiones para esta descarga.");
                    if (saturado.EsperaSugerida is TimeSpan sugerida && sugerida > espera)
                        espera = sugerida < EsperaMaximaRetryAfter ? sugerida : EsperaMaximaRetryAfter;
                }
                AppLogger.Debug("DownloadService", $"Corte en un trozo (intento {intentosFallidos}/{MaxReintentosPorTrozo}): {ex.Message}. Reintentando en {espera.TotalSeconds:F1}s.");
                await Task.Delay(espera, ct);
            }
            finally
            {
                for (int i = TrozosCompletos(tramo) - completosAntes; i > 0; i--) control.RegistrarTrozoCompletado();
            }
        }
    }

    /// <summary>Espera turno con el servidor (su tope aprendido de conexiones simultáneas) y abre una conexión.</summary>
    private async Task<LimitadorPorServidor.Conexion> AbrirConexionAsync(string servidor, CancellationToken ct)
    {
        LimitadorPorServidor.Conexion? conexion;
        while ((conexion = _limitadorServidores.IntentarAbrir(servidor, DateTime.UtcNow)) == null)
        {
            await Task.Delay(IntervaloReparto, ct);
        }
        return conexion;
    }

    private static long BytesRecibidos(IReadOnlyList<SegmentState> tramo)
    {
        long total = 0;
        foreach (var t in tramo) total += t.CurrentOffset - t.Start;
        return total;
    }

    private static int TrozosCompletos(IReadOnlyList<SegmentState> tramo)
    {
        int n = 0;
        foreach (var t in tramo) if (t.CurrentOffset > t.End) n++;
        return n;
    }

    /// <summary>
    /// ±25 % aleatorio sobre la espera: si varias conexiones se cortan a la vez, no vuelven todas
    /// en el mismo instante (lo que el servidor castigaría con otro corte).
    /// </summary>
    private static TimeSpan EsperaConVariacion(TimeSpan espera)
        => TimeSpan.FromMilliseconds(espera.TotalMilliseconds * (0.75 + Random.Shared.NextDouble() * 0.5));

    private async Task DescargarTramoAsync(string videoUrl, SafeFileHandle fileHandle, IReadOnlyList<SegmentState> tramo, long totalBytes, ProgresoAgregado acumulador, AgrupadorTrozos agrupador, LimitadorPorServidor.Conexion conexion, CancellationToken ct)
    {
        // El tramo pendiente es continuo: desde el byte actual del primer trozo sin terminar hasta el
        // final del último (los trozos siguientes al primero están sin empezar, ver ArmarTramos).
        int indice = 0;
        while (tramo[indice].CurrentOffset > tramo[indice].End) indice++;
        long desdeByte = tramo[indice].CurrentOffset;
        long hastaByte = tramo[^1].End;

        using var req = new HttpRequestMessage(HttpMethod.Get, videoUrl);
        req.Headers.Add("User-Agent", UserAgent);
        req.Headers.Add("Referer", "https://www.mp4upload.com/");
        req.Headers.Range = new RangeHeaderValue(desdeByte, hastaByte);

        var reloj = Stopwatch.StartNew();
        using var response = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        agrupador.RegistrarLatencia(reloj.Elapsed);

        if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            throw new RangoInvalidoException("El servidor rechazó el rango pedido (416): el archivo cambió o no admite rangos.");
        }
        if (response.StatusCode is System.Net.HttpStatusCode.TooManyRequests or System.Net.HttpStatusCode.ServiceUnavailable)
        {
            throw new ServidorSaturadoException(response.StatusCode, LeerRetryAfter(response));
        }
        response.EnsureSuccessStatusCode();

        // Un 200 a una petición con Range significa que el cuerpo empieza en el byte 0: escribirlo
        // en la posición del trozo dejaría el video corrupto sin ningún error visible.
        if (response.StatusCode != System.Net.HttpStatusCode.PartialContent)
        {
            throw new RangoInvalidoException($"El servidor ignoró el rango pedido (respondió {(int)response.StatusCode} en vez de 206).");
        }

        var contentRange = response.Content.Headers.ContentRange;
        if (contentRange?.Length is long longitudReal && longitudReal != totalBytes)
        {
            throw new RangoInvalidoException($"El tamaño del archivo cambió en el servidor ({totalBytes} → {longitudReal} bytes).");
        }
        if (contentRange?.From is long desde && desde != desdeByte)
        {
            throw new IOException($"El servidor devolvió el rango desde el byte {desde} en vez de {desdeByte}.");
        }
        conexion.MarcarTransfiriendo(DateTime.UtcNow);

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        // Búfer prestado del pool: uno nuevo de 128 KB por trozo (cientos por episodio) iba a la
        // zona de objetos grandes de la memoria y forzaba limpiezas pesadas durante la descarga.
        byte[] buffer = ArrayPool<byte>.Shared.Rent(TamanoBufferLectura);
        // FUN-015: watchdog de inactividad — 60 s sin recibir datos abortan el tramo. Un solo
        // temporizador por tramo, reprogramado en cada lectura, en vez de crear uno por lectura.
        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            while (indice < tramo.Count)
            {
                var trozo = tramo[indice];
                // Nunca se lee más allá del final del trozo actual: así cada byte se anota en su trozo.
                int bytesToRead = (int)Math.Min(TamanoBufferLectura, trozo.End - trozo.CurrentOffset + 1);

                int read;
                try
                {
                    idleCts.CancelAfter(TiempoMaximoInactividad);
                    read = await stream.ReadAsync(buffer.AsMemory(0, bytesToRead), idleCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new IOException("La descarga se detuvo por inactividad (60 s sin recibir datos del servidor).");
                }
                if (read == 0) break;

                await RandomAccess.WriteAsync(fileHandle, buffer.AsMemory(0, read), trozo.CurrentOffset, ct);
                trozo.CurrentOffset += read;
                acumulador.Sumar(read);
                if (trozo.CurrentOffset > trozo.End) indice++;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        // FUN-018: un cierre prematuro de la conexión (host gratuito saturado) deja el stream en
        // EOF antes de entregar todo el rango. Se trata como corte transitorio: se reintenta
        // desde el último byte recibido en vez de dar el trozo por terminado.
        var ultimoTrozo = tramo[^1];
        if (ultimoTrozo.CurrentOffset <= ultimoTrozo.End)
        {
            long recibidoHasta = indice < tramo.Count ? tramo[indice].CurrentOffset : ultimoTrozo.CurrentOffset;
            throw new IOException($"El servidor cerró la conexión antes de completar el trozo (recibidos hasta el byte {recibidoHasta} de {ultimoTrozo.End}).");
        }
    }

    /// <summary>
    /// Conexiones simultáneas por descarga: se reparte un presupuesto total (~12) entre las
    /// descargas que están transfiriendo AHORA para no saturar al servidor (que respondería con
    /// cortes o más lento). Antes se dividía por el límite configurado: con 5 descargas simultáneas
    /// permitidas, un episodio descargándose solo usaba 3 conexiones en vez de 8.
    /// </summary>
    private int ConexionesPorDescarga()
    {
        int activas = Math.Max(1, Volatile.Read(ref _descargasHttpActivas));
        return Math.Clamp(ConexionesTotalesObjetivo / activas, MinConexionesPorDescarga, MaxConexionesPorDescarga);
    }

    /// <summary>
    /// Tope de conexiones que el ajuste automático puede alcanzar por descarga: un presupuesto
    /// total repartido entre las activas, para no abrir decenas de conexiones al mismo servidor.
    /// </summary>
    private int TechoConexionesPorDescarga()
    {
        int activas = Math.Max(1, Volatile.Read(ref _descargasHttpActivas));
        return Math.Clamp(PresupuestoMaximoConexiones / activas, MinConexionesPorDescarga, TechoAbsolutoConexionesPorDescarga);
    }

    private int ConexionesPermitidas(ControlConexionesDescarga control)
        => control.Permitidas(ConexionesPorDescarga(), TechoConexionesPorDescarga());

    /// <summary>
    /// Mide la velocidad de la descarga cada <see cref="VentanaMedicionConexiones"/> y deja que
    /// <see cref="ControlConexionesDescarga"/> decida si probar más conexiones o quitarlas.
    /// </summary>
    private async Task AjustarConexionesAsync(ControlConexionesDescarga control, ProgresoAgregado acumulador, string servidor, AgrupadorTrozos agrupador, Func<bool> enFinal, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(VentanaMedicionConexiones);
        long bytesAnteriores = acumulador.Descargado;
        var reloj = Stopwatch.StartNew();
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                long bytes = acumulador.Descargado;
                double segundos = reloj.Elapsed.TotalSeconds;
                reloj.Restart();
                double velocidad = segundos > 0 ? (bytes - bytesAnteriores) / segundos : 0;
                bytesAnteriores = bytes;

                var ahora = DateTime.UtcNow;
                bool servidorAlTope = _limitadorServidores.Tope(servidor, ahora) is int tope && _limitadorServidores.Abiertas(servidor) >= tope;
                int ventanas = ControlConexionesDescarga.VentanasParaLatencia(agrupador.LatenciaMedia, VentanaMedicionConexiones);
                string? cambio = control.EvaluarVentana(velocidad, ConexionesPorDescarga(), TechoConexionesPorDescarga(), ahora,
                    servidorAlTope, ventanas, enFinal());
                if (cambio != null) AppLogger.Info("DownloadService", cambio);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>429/503: el servidor está saturado o limita peticiones; reintentable, con la espera que sugiera.</summary>
    private sealed class ServidorSaturadoException : HttpRequestException
    {
        public TimeSpan? EsperaSugerida { get; }
        /// <summary>Solo para el 403 por exceso de conexiones: tope aprendido para ese servidor.</summary>
        public int? TopeConexiones { get; }

        public ServidorSaturadoException(System.Net.HttpStatusCode estado, TimeSpan? esperaSugerida, int? topeConexiones = null)
            : base($"El servidor está saturado ({(int)estado}).", null, estado)
        {
            EsperaSugerida = esperaSugerida;
            TopeConexiones = topeConexiones;
        }
    }

    private static TimeSpan? LeerRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is TimeSpan delta) return delta;
        if (retryAfter?.Date is DateTimeOffset fecha)
        {
            var espera = fecha - DateTimeOffset.UtcNow;
            return espera > TimeSpan.Zero ? espera : null;
        }
        return null;
    }

    /// <summary>
    /// Agrega los bytes de todas las conexiones y publica progreso con una velocidad suavizada
    /// (media móvil), para que el indicador no salte entre valores extremos.
    /// </summary>
    private sealed class ProgresoAgregado
    {
        private readonly IProgress<(double Progress, double Speed)>? _progress;
        private readonly long _total;
        private readonly object _lock = new();
        private long _descargado;
        private long _ultimoTotal;

        public long Descargado => Interlocked.Read(ref _descargado);
        private DateTime _ultimoInstante = DateTime.UtcNow;
        private double _ultimoPorcentaje = -1.0;
        private double _velocidadSuavizada;

        public ProgresoAgregado(IProgress<(double Progress, double Speed)>? progress, long total, long yaDescargado)
        {
            _progress = progress;
            _total = total;
            _descargado = yaDescargado;
            _ultimoTotal = yaDescargado;
        }

        public void Sumar(int bytes)
        {
            long actual = Interlocked.Add(ref _descargado, bytes);
            if (_progress == null) return;

            double porcentaje = Math.Clamp((double)actual / _total * 100.0, 0.0, 100.0);
            double velocidad = 0;
            bool reportar = false;

            lock (_lock)
            {
                var ahora = DateTime.UtcNow;
                double transcurrido = (ahora - _ultimoInstante).TotalSeconds;
                if (porcentaje - _ultimoPorcentaje >= 0.5 || transcurrido >= 0.25 || actual == _total)
                {
                    if (transcurrido >= 0.1)
                    {
                        double instantanea = (actual - _ultimoTotal) / transcurrido;
                        _velocidadSuavizada = _velocidadSuavizada <= 0 ? instantanea : 0.7 * _velocidadSuavizada + 0.3 * instantanea;
                        _ultimoTotal = actual;
                        _ultimoInstante = ahora;
                    }
                    _ultimoPorcentaje = porcentaje;
                    velocidad = _velocidadSuavizada;
                    reportar = true;
                }
            }

            if (reportar) _progress.Report((porcentaje, velocidad));
        }
    }

    /// <summary>
    /// El modo de una sola conexión también ocupa turno con el servidor: antes se saltaba el tope y,
    /// con el servidor lleno por otras descargas, recibía un 403 que parecía un enlace rechazado.
    /// </summary>
    private async Task DescargarSecuencialConTurnoAsync(string videoUrl, string destinationPath, long totalBytes, IProgress<(double Progress, double Speed)>? progress, CancellationToken cancellationToken)
    {
        var conexion = await AbrirConexionAsync(new Uri(videoUrl).Authority, cancellationToken);
        try
        {
            await DownloadSequentialAsync(videoUrl, destinationPath, totalBytes, progress, cancellationToken);
        }
        finally
        {
            conexion.Cerrar();
        }
    }

    private async Task DownloadSequentialAsync(string videoUrl, string destinationPath, long totalBytes, IProgress<(double Progress, double Speed)>? progress, CancellationToken cancellationToken)
    {
        long existingLength = 0;
        if (File.Exists(destinationPath))
        {
            existingLength = new FileInfo(destinationPath).Length;
        }

        if (totalBytes > 0 && existingLength >= totalBytes)
        {
            progress?.Report((100.0, 0));
            return;
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, videoUrl);
        req.Headers.Add("User-Agent", UserAgent);
        req.Headers.Add("Referer", "https://www.mp4upload.com/");

        if (existingLength > 0 && totalBytes > 0)
        {
            req.Headers.Range = new RangeHeaderValue(existingLength, null);
        }

        using var response = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        // FUN-014: si reanudamos con un archivo parcial (Range enviado) pero el servidor
        // responde 200 en vez de 206, el cuerpo empieza en CERO: hacer append sobre el
        // parcial corrompería el archivo. Se reinicia la descarga desde cero.
        if (existingLength > 0 && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
        {
            AppLogger.Warn("DownloadService", "El servidor ignoró la cabecera Range (200 en vez de 206): la descarga se reinicia desde cero para evitar corrupción (FUN-014).");
            EliminarParcialSeguro(destinationPath);
            existingLength = 0;
            totalBytes = response.Content.Headers.ContentLength ?? -1L;
        }
        else if (totalBytes <= 0)
        {
            totalBytes = response.Content.Headers.ContentLength ?? -1L;
            if (existingLength > 0) totalBytes += existingLength; // Adjust total if resumed
        }

        // SEC-03: el modo secuencial queda acotado igual que el segmentado: un servidor
        // que declare un tamaño absurdo no puede llenar el disco.
        if (totalBytes > MaxArchivoDescargaBytes)
        {
            EliminarParcialSeguro(destinationPath);
            throw new DownloadAbortDefinitivoException($"El tamaño declarado del video supera el límite de seguridad de {MaxArchivoDescargaBytes / (1024.0 * 1024 * 1024):F0} GB.");
        }

        var canReportProgress = totalBytes > 0 && progress != null;

        using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var fileStream = new FileStream(destinationPath, FileMode.Append, FileAccess.Write, FileShare.None, 131072, true);

        var totalRead = existingLength;
        var buffer = new byte[131072];
        var isMoreToRead = true;
        bool superoLimite = false;
        var lastReportedPercentage = -1.0;
        var lastReportTime = DateTime.UtcNow;
        var lastReportedTotalBytes = totalRead;
        // FUN-015: watchdog de inactividad — 60 s sin datos abortan la descarga secuencial
        // (un temporizador reprogramado en cada lectura).
        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        do
        {
            int read;
            try
            {
                idleCts.CancelAfter(TiempoMaximoInactividad);
                read = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), idleCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new IOException("La descarga se detuvo por inactividad (60 s sin recibir datos del servidor).");
            }
            if (read == 0)
            {
                isMoreToRead = false;
            }
            else
            {
                totalRead += read;

                // SEC-03: corte duro si el servidor no declaró el tamaño y el stream excede el tope.
                if (totalRead > MaxArchivoDescargaBytes)
                {
                    superoLimite = true;
                    isMoreToRead = false;
                }
                else
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);

                    if (canReportProgress && progress != null)
                    {
                        double currentPercentage = Math.Clamp((double)totalRead / totalBytes * 100, 0.0, 100.0);
                        var now = DateTime.UtcNow;
                        var timeElapsed = (now - lastReportTime).TotalSeconds;
                        if (currentPercentage - lastReportedPercentage >= 0.5 || timeElapsed >= 0.15 || totalRead == totalBytes)
                        {
                            double speed = timeElapsed > 0 ? (totalRead - lastReportedTotalBytes) / timeElapsed : 0;
                            lastReportedPercentage = currentPercentage;
                            lastReportTime = now;
                            lastReportedTotalBytes = totalRead;
                            progress.Report((currentPercentage, speed));
                        }
                    }
                }
            }
        }
        while (isMoreToRead);

        if (superoLimite)
        {
            EliminarParcialSeguro(destinationPath);
            throw new DownloadAbortDefinitivoException($"El archivo descargado superó el límite de seguridad de {MaxArchivoDescargaBytes / (1024.0 * 1024 * 1024):F0} GB.");
        }

        // FUN-018: mismo problema que en el modo segmentado — un cierre prematuro de conexión
        // (host gratuito saturado) deja el stream en EOF (read == 0) antes de entregar todo el
        // Content-Length declarado, y el bucle de arriba lo tomaba por una descarga terminada
        // con normalidad. Si conocemos el tamaño esperado, validarlo evita mover un archivo
        // truncado a la biblioteca como si estuviera completo.
        if (totalBytes > 0 && totalRead < totalBytes)
        {
            throw new IOException($"El servidor cerró la conexión antes de completar la descarga (recibidos {totalRead} de {totalBytes} bytes).");
        }

        if (canReportProgress && progress != null)
        {
            progress.Report((100.0, 0));
        }
    }

    private static void EliminarParcialSeguro(string destinationPath)
    {
        try
        {
            if (File.Exists(destinationPath)) File.Delete(destinationPath);
        }
        catch (Exception ex)
        {
            AppLogger.Debug("DownloadService", $"No se pudo limpiar el archivo parcial tras el corte de seguridad: {ex.Message}");
        }
    }

    /// <summary>
    /// HTTP 403/404/410 del servidor de video = el enlace firmado ya no sirve (caducado,
    /// geo-bloqueado o eliminado): no es un fallo definitivo, merece re-resolver.
    /// </summary>
    private static bool EsRechazoDeEnlace(Exception ex)
    {
        return ex is HttpRequestException hre
               && ex is not ServidorSaturadoException
               && hre.StatusCode is System.Net.HttpStatusCode.Forbidden
                   or System.Net.HttpStatusCode.NotFound
                   or System.Net.HttpStatusCode.Gone;
    }

    /// <summary>
    /// FUN-017: corte de red o de servidor que suele ser pasajero (timeout, inactividad,
    /// conexión reiniciada, 5xx) y vale la pena reintentar sin perder el progreso ya
    /// descargado. Excluye los cortes de seguridad definitivos (tamaño/espacio en disco),
    /// marcados con <see cref="DownloadAbortDefinitivoException"/>, que no deben reintentarse.
    /// </summary>
    private static bool EsErrorTransitorioDeRed(Exception ex)
    {
        if (ex is DownloadAbortDefinitivoException) return false;
        return ex is IOException or HttpRequestException or TaskCanceledException;
    }

    /// <summary>El servidor no respeta el rango pedido (200 en vez de 206, 416, tamaño distinto): no reintentable por rangos.</summary>
    private sealed class RangoInvalidoException : Exception
    {
        public RangoInvalidoException(string message) : base(message) { }
    }

    /// <summary>
    /// Comprobación barata de que el archivo descargado es un video y no una página de error
    /// (HTML/JSON) que el servidor entregó con código 200 al caducar el enlace.
    /// </summary>
    private static bool ArchivoPareceVideo(string ruta)
    {
        try
        {
            using var fs = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 12) return false;
            var cabecera = new byte[512];
            int leidos = fs.Read(cabecera, 0, cabecera.Length);
            // Una cabecera entera a ceros es espacio preasignado que nunca se escribió, no un video
            // (MP4 empieza por ceros, pero el tamaño de su primera caja y "ftyp" llegan en los primeros bytes).
            if (cabecera.AsSpan(0, leidos).IndexOfAnyExcept((byte)0) < 0) return false;
            for (int i = 0; i < leidos; i++)
            {
                byte b = cabecera[i];
                if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') continue;
                return b is not ((byte)'<' or (byte)'{');
            }
            return false; // todo espacios/ceros de pre-asignación no es un video
        }
        catch (Exception ex)
        {
            AppLogger.Debug("DownloadService", $"No se pudo comprobar la cabecera del video: {ex.Message}");
            return true; // ante la duda no se bloquea una descarga posiblemente buena
        }
    }

    /// <summary>Corte de seguridad definitivo (límite de tamaño, espacio en disco insuficiente): no se reintenta.</summary>
    private sealed class DownloadAbortDefinitivoException : IOException
    {
        public DownloadAbortDefinitivoException(string message) : base(message) { }
    }

    private static string SanitizarUrlParaLog(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "(vacía)";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "(url no parseable)";
        return $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}";
    }

    private static string FormatearVelocidad(double bytesPerSecond)
    {
        if (bytesPerSecond < 1024)
            return $"{bytesPerSecond:F0} B/s";
        if (bytesPerSecond < 1024 * 1024)
            return $"{bytesPerSecond / 1024:F1} KB/s";
        return $"{bytesPerSecond / (1024 * 1024):F2} MB/s";
    }
}
