using System;
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
    private const int LimiteDescargasPorDefecto = 3;
    // FUN-017: máximo de reintentos automáticos ante cortes de red transitorios antes de abandonar.
    private const int MaxReintentosDescargaTransitoria = 5;
    // SEC-03: tope de seguridad por archivo en el modo secuencial (el segmentado ya lo acota).
    private const long MaxArchivoDescargaBytes = 35L * 1024 * 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly IDownloadStateStore _stateStore;
    private readonly IVideoSourceResolver _sourceResolver;
    private readonly ISettingsService? _settingsService;
    private readonly IPythonBridgeService? _pythonBridge;
    private readonly IDatabaseService? _database;
    private readonly ConcurrentDictionary<string, DownloadState> _activeDownloads = new();

    // Gestor de slots de concurrencia (redimensionable en caliente según DescargasSimultaneas)
    private readonly object _slotLock = new();
    private readonly List<(string Key, TaskCompletionSource<bool> Tcs)> _slotWaiters = new();
    private int _slotsActivos;
    private int _limiteDescargas = LimiteDescargasPorDefecto;

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
    }

    public DownloadService(
        IHttpClientFactory httpClientFactory,
        IDownloadStateStore? stateStore = null,
        IVideoSourceResolver? sourceResolver = null,
        ISettingsService? settingsService = null,
        IPythonBridgeService? pythonBridge = null,
        IDatabaseService? database = null)
    {
        _database = database;
        _httpClient = httpClientFactory.CreateClient("Downloader");
        _stateStore = stateStore ?? new DownloadStateStore();
        _sourceResolver = sourceResolver ?? new AnimeAv1VideoSourceResolver(_httpClient);
        _pythonBridge = pythonBridge;
        _settingsService = settingsService;

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
            _stateStore.EliminarArchivosTemporales(state.RutaTemporal);
            WeakReferenceMessenger.Default.Send(new DescargaProgresoMensaje(aniListId, numeroEpisodio, 0, isDownloading: false, isCompleted: false, isPaused: false, "", "Descarga cancelada", state.AnimeTitulo));
        }
    }

    public void CancelarTodas()
    {
        foreach (var kvp in _activeDownloads.ToList())
        {
            if (_activeDownloads.TryRemove(kvp.Key, out var state))
            {
                try { state.Cts.Cancel(); } catch { }
                _stateStore.EliminarArchivosTemporales(state.RutaTemporal);
                WeakReferenceMessenger.Default.Send(new DescargaProgresoMensaje(state.AniListId, state.NumeroEpisodio, 0, isDownloading: false, isCompleted: false, isPaused: false, "", "Descarga cancelada", state.AnimeTitulo));
            }
        }
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
            EjecutarBucleDescargaAsync(state);
        }
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
                EjecutarBucleDescargaAsync(state);
            }
        }
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

    private Task IniciarInterno(int aniListId, string animeTitulo, string carpetaDestino, int numeroEpisodio, IEnumerable<string>? titulosAlternativos, bool automatica)
    {
        string key = $"{aniListId}_{numeroEpisodio}";
        if (_activeDownloads.ContainsKey(key)) return Task.CompletedTask;

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

        if (!Directory.Exists(carpetaDestino))
        {
            Directory.CreateDirectory(carpetaDestino);
        }

        WeakReferenceMessenger.Default.Send(new DescargaProgresoMensaje(aniListId, numeroEpisodio, 0, isDownloading: true, isCompleted: false, isPaused: false, "", null, animeTitulo, enCola: true));

        EjecutarBucleDescargaAsync(state);
        return Task.CompletedTask;
    }

    private void EjecutarBucleDescargaAsync(DownloadState state)
    {
        string key = $"{state.AniListId}_{state.NumeroEpisodio}";

        _ = Task.Run(async () =>
        {
            bool slotAdquirido = false;
            int reintentosResolucion = 0;
            int reintentosDescarga = 0;
            try
            {
                await AdquirirSlotAsync(key, state, state.Cts.Token);
                slotAdquirido = true;
                state.EnCola = false;

                if (state.Cts.IsCancellationRequested || state.IsPaused) return;

                WeakReferenceMessenger.Default.Send(new DescargaProgresoMensaje(state.AniListId, state.NumeroEpisodio, state.Progreso, isDownloading: true, isCompleted: false, isPaused: false, state.RutaDestino, null, state.AnimeTitulo, enCola: false, reintentos: state.Reintentos));

                IProgress<(double Progress, double Speed)>? progress = null;
                bool descargaCompletada = false;

                // El enlace ya resuelto puede ser rechazado por el servidor (firma caducada,
                // 403/404/410): se re-resuelve UNA vez con un enlace nuevo antes de fallar.
                while (!descargaCompletada)
                {
                    if (state.Cts.IsCancellationRequested || state.IsPaused) return;

                    if (string.IsNullOrEmpty(state.VideoUrl))
                    {
                        var configuracion = _settingsService?.ObtenerConfiguracion();
                        string? audioPreferido = configuracion?.PreferenciaAudioAnimeAv1;
                        string? servidorPreferido = configuracion?.ServidorPreferidoAnimeAv1;
                        state.VideoUrl = await _sourceResolver.BuscarUrlEpisodioAsync(state.Titulos, state.NumeroEpisodio, state.AniListId, audioPreferido, servidorPreferido, state.Cts.Token);
                        if (string.IsNullOrEmpty(state.VideoUrl))
                        {
                            if (state.IsPaused || state.Cts.IsCancellationRequested) return;

                            // FUN-016: trazabilidad del ciclo de descarga en app.log (antes solo Debug)
                            AppLogger.Warn("DownloadService", $"No se encontró enlace para '{state.AnimeTitulo}' Ep {state.NumeroEpisodio}.");
                            _activeDownloads.TryRemove(key, out _);
                            string errorNoEncontrado = $"No se encontró el episodio {state.NumeroEpisodio} en el servidor.";
                            WeakReferenceMessenger.Default.Send(new DescargaProgresoMensaje(state.AniListId, state.NumeroEpisodio, 0, isDownloading: false, isCompleted: false, isPaused: false, "", errorNoEncontrado, state.AnimeTitulo));
                            // Descarga automática: el episodio puede tardar horas en aparecer en el servidor; cada intento
                            // fallido no debe llenar el historial (el monitor reintenta con espera creciente).
                            if (!state.Automatica) RegistrarEnHistorial(state, completada: false, errorNoEncontrado);
                            return;
                        }
                    }

                    if (state.Cts.IsCancellationRequested || state.IsPaused) return;

                    progress ??= new Progress<(double Progress, double Speed)>(p =>
                    {
                        state.Progreso = p.Progress;
                        string speedText = FormatearVelocidad(p.Speed);
                        WeakReferenceMessenger.Default.Send(new DescargaProgresoMensaje(state.AniListId, state.NumeroEpisodio, p.Progress, isDownloading: true, isCompleted: false, isPaused: false, state.RutaDestino, null, state.AnimeTitulo, speedText, velocidadBps: p.Speed, reintentos: state.Reintentos));
                    });

                    try
                    {
                        await DownloadVideoAsync(state.VideoUrl, state.RutaTemporal, progress, state.Cts.Token);

                        // Un enlace caducado a veces devuelve una página de error con código 200:
                        // si lo descargado no es un video se descarta y se repite limpio.
                        if (!Core.UrlSeguridad.EsUrlManifiestoStreaming(state.VideoUrl) && !ArchivoPareceVideo(state.RutaTemporal))
                        {
                            _stateStore.EliminarArchivosTemporales(state.RutaTemporal);
                            throw new IOException("El archivo descargado no es un video válido (el servidor devolvió una página de error).");
                        }
                        descargaCompletada = true;
                    }
                    catch (Exception ex) when (EsRechazoDeEnlace(ex) && reintentosResolucion == 0 && !state.IsPaused && !state.Cts.IsCancellationRequested)
                    {
                        reintentosResolucion++;
                        AppLogger.Warn("DownloadService", $"El servidor rechazó el enlace de '{state.AnimeTitulo}' Ep {state.NumeroEpisodio} ({(int?)((HttpRequestException)ex).StatusCode}): re-resolviendo el episodio (intento 2).");
                        EliminarParcialSeguro(state.RutaTemporal);
                        state.VideoUrl = null;
                    }
                    // FUN-017: los cortes de red (timeouts, conexión reiniciada, inactividad) son
                    // habituales en servidores de streaming gratuitos y no implican que el enlace
                    // sea inválido. Se reintenta varias veces conservando el progreso (el .state y
                    // el archivo parcial no se borran) en vez de abandonar la descarga a la primera.
                    catch (Exception ex) when (EsErrorTransitorioDeRed(ex) && reintentosDescarga < MaxReintentosDescargaTransitoria && !state.IsPaused && !state.Cts.IsCancellationRequested)
                    {
                        reintentosDescarga++;
                        state.Reintentos = reintentosDescarga;
                        var espera = TimeSpan.FromSeconds(Math.Min(2 * reintentosDescarga, 15));
                        AppLogger.Warn("DownloadService", $"Fallo transitorio de red descargando '{state.AnimeTitulo}' Ep {state.NumeroEpisodio} (intento {reintentosDescarga}/{MaxReintentosDescargaTransitoria}): {ex.Message}. Reintentando en {espera.TotalSeconds:F0}s sin perder el progreso.");
                        WeakReferenceMessenger.Default.Send(new DescargaProgresoMensaje(state.AniListId, state.NumeroEpisodio, state.Progreso, isDownloading: true, isCompleted: false, isPaused: false, state.RutaDestino, null, state.AnimeTitulo, reintentos: reintentosDescarga));
                        await Task.Delay(espera, state.Cts.Token);
                    }
                }

                if (File.Exists(state.RutaDestino)) File.Delete(state.RutaDestino);
                File.Move(state.RutaTemporal, state.RutaDestino);
                _stateStore.EliminarArchivosTemporales(state.RutaTemporal);

                _activeDownloads.TryRemove(key, out _);
                WeakReferenceMessenger.Default.Send(new DescargaProgresoMensaje(state.AniListId, state.NumeroEpisodio, 100, isDownloading: false, isCompleted: true, isPaused: false, state.RutaDestino, null, state.AnimeTitulo));
                RegistrarEnHistorial(state, completada: true, null);
            }
            catch (OperationCanceledException)
            {
                AppLogger.Info("DownloadService", $"Descarga interrumpida: {state.AnimeTitulo} Ep {state.NumeroEpisodio}. Pausado: {state.IsPaused}");
                if (!state.IsPaused)
                {
                    _stateStore.EliminarArchivosTemporales(state.RutaTemporal);
                    _activeDownloads.TryRemove(key, out _);
                }
            }
            catch (Exception ex)
            {
                if (state.IsPaused || state.Cts.IsCancellationRequested)
                {
                    AppLogger.Debug("DownloadService", $"Descarga pausada generó excepción esperada: {ex.Message}");
                    return;
                }

                AppLogger.Error("DownloadService", $"Error descargando {state.AnimeTitulo} Ep {state.NumeroEpisodio}", ex);
                // FUN-017: no se borra el archivo parcial ni el .state aquí — ya se agotaron los
                // reintentos automáticos, pero conservar el progreso permite que un reintento manual
                // del usuario retome la descarga en vez de empezar desde cero.
                _activeDownloads.TryRemove(key, out _);
                WeakReferenceMessenger.Default.Send(new DescargaProgresoMensaje(state.AniListId, state.NumeroEpisodio, 0, isDownloading: false, isCompleted: false, isPaused: false, "", ex.Message, state.AnimeTitulo));
                RegistrarEnHistorial(state, completada: false, ex.Message);
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
        using (var headReq = new HttpRequestMessage(HttpMethod.Head, videoUrl))
        {
            headReq.Headers.Add("User-Agent", UserAgent);
            headReq.Headers.Add("Referer", "https://www.mp4upload.com/");

            try
            {
                using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                probeCts.CancelAfter(TimeSpan.FromSeconds(6));

                using var headRes = await _httpClient.SendAsync(headReq, HttpCompletionOption.ResponseHeadersRead, probeCts.Token);
                if (headRes.IsSuccessStatusCode)
                {
                    totalBytes = headRes.Content.Headers.ContentLength ?? -1;
                    supportsRanges = headRes.Headers.AcceptRanges.Contains("bytes") || headRes.Content.Headers.ContentRange != null;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug("DownloadService", $"Sondeo HEAD para '{SanitizarUrlParaLog(videoUrl)}' omitido/timeout: {ex.Message}");
            }
        }

        // Si HEAD no devolvió tamaño o soporte de rangos, probar con GET range 0-0
        if (!reanudandoSegmentada && (totalBytes <= 0 || !supportsRanges))
        {
            using var testReq = new HttpRequestMessage(HttpMethod.Get, videoUrl);
            testReq.Headers.Add("User-Agent", UserAgent);
            testReq.Headers.Add("Referer", "https://www.mp4upload.com/");
            testReq.Headers.Range = new RangeHeaderValue(0, 0);

            try
            {
                using var probeGetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                probeGetCts.CancelAfter(TimeSpan.FromSeconds(6));

                using var testRes = await _httpClient.SendAsync(testReq, HttpCompletionOption.ResponseHeadersRead, probeGetCts.Token);
                if (testRes.StatusCode == System.Net.HttpStatusCode.PartialContent)
                {
                    supportsRanges = true;
                    if (testRes.Content.Headers.ContentRange?.Length.HasValue == true)
                    {
                        totalBytes = testRes.Content.Headers.ContentRange.Length.Value;
                    }
                }
                else if (testRes.IsSuccessStatusCode && totalBytes <= 0)
                {
                    totalBytes = testRes.Content.Headers.ContentLength ?? -1;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug("DownloadService", $"Sondeo GET Range(0,0) para '{SanitizarUrlParaLog(videoUrl)}' omitido/timeout: {ex.Message}");
            }
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
                await DownloadSequentialAsync(videoUrl, destinationPath, -1, progress, cancellationToken);
            }
        }
        else
        {
            await DownloadSequentialAsync(videoUrl, destinationPath, totalBytes, progress, cancellationToken);
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
        int conexiones = Math.Min(ConexionesPorDescarga(), trozos.Count);

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

        var trabajadores = new Task[conexiones];
        for (int i = 0; i < conexiones; i++)
        {
            trabajadores[i] = Task.Run(async () =>
            {
                try
                {
                    while (!trabajoCts.IsCancellationRequested)
                    {
                        int idx = Interlocked.Increment(ref siguienteTrozo);
                        if (idx >= trozos.Count) return;
                        await DescargarTrozoConReintentosAsync(videoUrl, fileHandle, trozos[idx], totalBytes, acumulador, trabajoCts.Token);
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

        guardadorCts.Cancel();
        await guardador;

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

    private async Task DescargarTrozoConReintentosAsync(string videoUrl, SafeFileHandle fileHandle, SegmentState trozo, long totalBytes, ProgresoAgregado acumulador, CancellationToken ct)
    {
        int intentosFallidos = 0;
        while (trozo.CurrentOffset <= trozo.End)
        {
            long offsetAntes = trozo.CurrentOffset;
            try
            {
                await DescargarTramoAsync(videoUrl, fileHandle, trozo, totalBytes, acumulador, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested
                                       && EsErrorTransitorioDeRed(ex)
                                       && !EsRechazoDeEnlace(ex))
            {
                // Si el trozo avanzó antes de cortarse, el fallo no cuenta como intento agotado.
                if (trozo.CurrentOffset > offsetAntes) intentosFallidos = 0;
                if (++intentosFallidos > MaxReintentosPorTrozo) throw;

                var espera = TimeSpan.FromSeconds(Math.Min(1 << (intentosFallidos - 1), 8));
                AppLogger.Debug("DownloadService", $"Corte en un trozo (intento {intentosFallidos}/{MaxReintentosPorTrozo}): {ex.Message}. Reintentando en {espera.TotalSeconds:F0}s.");
                await Task.Delay(espera, ct);
            }
        }
    }

    private async Task DescargarTramoAsync(string videoUrl, SafeFileHandle fileHandle, SegmentState trozo, long totalBytes, ProgresoAgregado acumulador, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, videoUrl);
        req.Headers.Add("User-Agent", UserAgent);
        req.Headers.Add("Referer", "https://www.mp4upload.com/");
        req.Headers.Range = new RangeHeaderValue(trozo.CurrentOffset, trozo.End);

        using var response = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            throw new RangoInvalidoException("El servidor rechazó el rango pedido (416): el archivo cambió o no admite rangos.");
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
        if (contentRange?.From is long desde && desde != trozo.CurrentOffset)
        {
            throw new IOException($"El servidor devolvió el rango desde el byte {desde} en vez de {trozo.CurrentOffset}.");
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        byte[] buffer = new byte[131072];

        while (trozo.CurrentOffset <= trozo.End)
        {
            int bytesToRead = (int)Math.Min(buffer.Length, trozo.End - trozo.CurrentOffset + 1);

            int read;
            try
            {
                // FUN-015: watchdog de inactividad — 60 s sin recibir datos abortan el tramo.
                using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idleCts.CancelAfter(TimeSpan.FromSeconds(60));
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
        }

        // FUN-018: un cierre prematuro de la conexión (host gratuito saturado) deja el stream en
        // EOF antes de entregar todo el rango. Se trata como corte transitorio: se reintenta
        // desde el último byte recibido en vez de dar el trozo por terminado.
        if (trozo.CurrentOffset <= trozo.End)
        {
            throw new IOException($"El servidor cerró la conexión antes de completar el trozo (recibidos hasta el byte {trozo.CurrentOffset} de {trozo.End}).");
        }
    }

    /// <summary>
    /// Conexiones simultáneas por descarga: se reparte un presupuesto total (~12) entre las
    /// descargas en paralelo para no saturar al servidor (que respondería con cortes o más lento).
    /// </summary>
    private int ConexionesPorDescarga()
    {
        int limite;
        lock (_slotLock) limite = _limiteDescargas;
        return Math.Clamp(ConexionesTotalesObjetivo / Math.Max(1, limite), 3, 8);
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

        do
        {
            int read;
            try
            {
                // FUN-015: watchdog de inactividad — 60 s sin datos abortan la descarga secuencial.
                using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                idleCts.CancelAfter(TimeSpan.FromSeconds(60));
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
