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
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Win32.SafeHandles;

namespace AnimeLocalTracker.Services;

public class DownloadService : IDownloadService
{
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
    private const int SegmentosParalelos = 6;
    private const int LimiteDescargasPorDefecto = 3;

    private readonly HttpClient _httpClient;
    private readonly IDownloadStateStore _stateStore;
    private readonly IVideoSourceResolver _sourceResolver;
    private readonly ConcurrentDictionary<string, DownloadState> _activeDownloads = new();

    // Gestor de slots de concurrencia (redimensionable en caliente según DescargasSimultaneas)
    private readonly object _slotLock = new();
    private Queue<TaskCompletionSource<bool>> _slotWaiters = new();
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
    }

    public DownloadService(
        IHttpClientFactory httpClientFactory,
        IDownloadStateStore? stateStore = null,
        IVideoSourceResolver? sourceResolver = null,
        ISettingsService? settingsService = null)
    {
        _httpClient = httpClientFactory.CreateClient("Downloader");
        _stateStore = stateStore ?? new DownloadStateStore();
        _sourceResolver = sourceResolver ?? new AnimeAv1VideoSourceResolver(_httpClient);

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
            var waiter = _slotWaiters.Dequeue();
            _slotsActivos++;
            concedidos.Add(waiter);
        }
        return concedidos.ToArray();
    }

    /// <summary>
    /// Espera un slot de descarga (bloquea si ya hay el máximo simultáneo).
    /// Respetuoso con la cancelación: si el token se cancela mientras espera,
    /// el slot no se consume y el waiter se descarta de la cola.
    /// </summary>
    private async Task<bool> AdquirirSlotAsync(CancellationToken ct)
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
            _slotWaiters.Enqueue(tcs);
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
                _slotWaiters = new Queue<TaskCompletionSource<bool>>(_slotWaiters.Where(w => !ReferenceEquals(w, tcs)));
            }
            throw;
        }
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
                RutaArchivo = s.RutaDestino
            })
            .ToList();
    }

    public Task IniciarDescargaEpisodioAsync(int aniListId, string animeTitulo, string carpetaDestino, int numeroEpisodio, IEnumerable<string>? titulosAlternativos = null)
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
        };
        state.RutaTemporal = state.RutaDestino + ".downloading";

        if (!_activeDownloads.TryAdd(key, state)) return Task.CompletedTask;

        if (!Directory.Exists(carpetaDestino))
        {
            Directory.CreateDirectory(carpetaDestino);
        }

        WeakReferenceMessenger.Default.Send(new DescargaProgresoMensaje(aniListId, numeroEpisodio, 0, isDownloading: true, isCompleted: false, isPaused: false, "", null, animeTitulo));

        EjecutarBucleDescargaAsync(state);
        return Task.CompletedTask;
    }

    private void EjecutarBucleDescargaAsync(DownloadState state)
    {
        string key = $"{state.AniListId}_{state.NumeroEpisodio}";

        _ = Task.Run(async () =>
        {
            bool slotAdquirido = false;
            try
            {
                await AdquirirSlotAsync(state.Cts.Token);
                slotAdquirido = true;

                if (state.Cts.IsCancellationRequested || state.IsPaused) return;

                if (string.IsNullOrEmpty(state.VideoUrl))
                {
                    state.VideoUrl = await _sourceResolver.BuscarUrlEpisodioAsync(state.Titulos, state.NumeroEpisodio, state.Cts.Token);
                    if (string.IsNullOrEmpty(state.VideoUrl))
                    {
                        if (state.IsPaused || state.Cts.IsCancellationRequested) return;

                        Debug.WriteLine($"[DownloadService] No se encontró enlace para '{state.AnimeTitulo}' Ep {state.NumeroEpisodio}.");
                        _activeDownloads.TryRemove(key, out _);
                        WeakReferenceMessenger.Default.Send(new DescargaProgresoMensaje(state.AniListId, state.NumeroEpisodio, 0, isDownloading: false, isCompleted: false, isPaused: false, "", $"No se encontró el episodio {state.NumeroEpisodio} en el servidor.", state.AnimeTitulo));
                        return;
                    }
                }

                if (state.Cts.IsCancellationRequested || state.IsPaused) return;

                var progress = new Progress<double>(p =>
                {
                    state.Progreso = p;
                    WeakReferenceMessenger.Default.Send(new DescargaProgresoMensaje(state.AniListId, state.NumeroEpisodio, p, isDownloading: true, isCompleted: false, isPaused: false, state.RutaDestino, null, state.AnimeTitulo));
                });

                await DownloadVideoAsync(state.VideoUrl, state.RutaTemporal, progress, state.Cts.Token);

                if (File.Exists(state.RutaDestino)) File.Delete(state.RutaDestino);
                File.Move(state.RutaTemporal, state.RutaDestino);
                _stateStore.EliminarArchivosTemporales(state.RutaTemporal);

                _activeDownloads.TryRemove(key, out _);
                WeakReferenceMessenger.Default.Send(new DescargaProgresoMensaje(state.AniListId, state.NumeroEpisodio, 100, isDownloading: false, isCompleted: true, isPaused: false, state.RutaDestino, null, state.AnimeTitulo));
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine($"[DownloadService] Descarga interrumpida: {state.AnimeTitulo} Ep {state.NumeroEpisodio}. Pausado: {state.IsPaused}");
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
                    Debug.WriteLine($"[DownloadService] Descarga pausada generó excepción esperada: {ex.Message}");
                    return;
                }

                Debug.WriteLine($"[DownloadService] Error descargando {state.AnimeTitulo} Ep {state.NumeroEpisodio}: {ex.Message}");
                _stateStore.EliminarArchivosTemporales(state.RutaTemporal);
                _activeDownloads.TryRemove(key, out _);
                WeakReferenceMessenger.Default.Send(new DescargaProgresoMensaje(state.AniListId, state.NumeroEpisodio, 0, isDownloading: false, isCompleted: false, isPaused: false, "", ex.Message, state.AnimeTitulo));
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

    public async Task<string?> GetVideoUrlAsync(string pageUrl, CancellationToken cancellationToken = default)
    {
        return await _sourceResolver.GetVideoUrlAsync(pageUrl, cancellationToken);
    }

    public async Task DownloadVideoAsync(string videoUrl, string destinationPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // Obtener tamaño y verificar soporte de rangos
        long totalBytes = -1;
        bool supportsRanges = false;

        using (var headReq = new HttpRequestMessage(HttpMethod.Head, videoUrl))
        {
            headReq.Headers.Add("User-Agent", UserAgent);
            headReq.Headers.Add("Referer", "https://www.mp4upload.com/");

            try
            {
                using var headRes = await _httpClient.SendAsync(headReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (headRes.IsSuccessStatusCode)
                {
                    totalBytes = headRes.Content.Headers.ContentLength ?? -1;
                    supportsRanges = headRes.Headers.AcceptRanges.Contains("bytes") || headRes.Content.Headers.ContentRange != null;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("DownloadService", $"Error en sondeo HEAD para '{videoUrl}': {ex.Message}");
            }
        }

        // Si HEAD no devolvió tamaño o soporte de rangos, probar con GET range 0-0
        if (totalBytes <= 0 || !supportsRanges)
        {
            using var testReq = new HttpRequestMessage(HttpMethod.Get, videoUrl);
            testReq.Headers.Add("User-Agent", UserAgent);
            testReq.Headers.Add("Referer", "https://www.mp4upload.com/");
            testReq.Headers.Range = new RangeHeaderValue(0, 0);

            try
            {
                using var testRes = await _httpClient.SendAsync(testReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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
                AppLogger.Warn("DownloadService", $"Error en sondeo GET Range(0,0) para '{videoUrl}': {ex.Message}");
            }
        }

        // Descarga segmentada en paralelo si el servidor soporta Range y conocemos el tamaño (> 3MB)
        if (supportsRanges && totalBytes > 3 * 1024 * 1024)
        {
            await DownloadSegmentedParallelAsync(videoUrl, destinationPath, totalBytes, progress, cancellationToken);
        }
        else
        {
            await DownloadSequentialAsync(videoUrl, destinationPath, totalBytes, progress, cancellationToken);
        }
    }

    private async Task DownloadSegmentedParallelAsync(string videoUrl, string destinationPath, long totalBytes, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        string statePath = destinationPath + ".state";
        DownloadStateInfo stateInfo = await _stateStore.CargarOInicializarAsync(statePath, totalBytes, SegmentosParalelos);

        bool shouldPreAlloc = !File.Exists(destinationPath);
        using (var preAlloc = new FileStream(destinationPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite, 4096, useAsync: true))
        {
            if (shouldPreAlloc || preAlloc.Length != totalBytes)
            {
                preAlloc.SetLength(totalBytes);
            }
        }

        using SafeFileHandle fileHandle = File.OpenHandle(destinationPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, FileOptions.Asynchronous);

        long totalDownloaded = stateInfo.Segments.Sum(s => s.CurrentOffset - s.Start);
        double lastReportedPercentage = -1.0;
        var lastReportTime = DateTime.UtcNow;

        var tasks = new Task[SegmentosParalelos];

        try
        {
            for (int i = 0; i < SegmentosParalelos; i++)
            {
                var segment = stateInfo.Segments[i];
                tasks[i] = Task.Run(async () =>
                {
                    if (segment.CurrentOffset > segment.End) return; // Ya terminó este segmento

                    using var req = new HttpRequestMessage(HttpMethod.Get, videoUrl);
                    req.Headers.Add("User-Agent", UserAgent);
                    req.Headers.Add("Referer", "https://www.mp4upload.com/");
                    req.Headers.Range = new RangeHeaderValue(segment.CurrentOffset, segment.End);

                    using var response = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    response.EnsureSuccessStatusCode();

                    using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    byte[] buffer = new byte[131072];

                    while (segment.CurrentOffset <= segment.End)
                    {
                        int bytesToRead = (int)Math.Min(buffer.Length, segment.End - segment.CurrentOffset + 1);
                        int read = await stream.ReadAsync(buffer.AsMemory(0, bytesToRead), cancellationToken);
                        if (read == 0) break;

                        await RandomAccess.WriteAsync(fileHandle, buffer.AsMemory(0, read), segment.CurrentOffset, cancellationToken);
                        segment.CurrentOffset += read;

                        long currentTotal = Interlocked.Add(ref totalDownloaded, read);

                        if (progress != null)
                        {
                            double percent = Math.Clamp((double)currentTotal / totalBytes * 100.0, 0.0, 100.0);
                            var now = DateTime.UtcNow;
                            if (percent - lastReportedPercentage >= 0.5 || (now - lastReportTime).TotalMilliseconds >= 150 || currentTotal == totalBytes)
                            {
                                lastReportedPercentage = percent;
                                lastReportTime = now;
                                progress.Report(percent);
                            }
                        }
                    }
                }, cancellationToken);
            }

            await Task.WhenAll(tasks);
            progress?.Report(100.0);
        }
        catch (OperationCanceledException)
        {
            await _stateStore.GuardarAsync(statePath, stateInfo);
            throw;
        }
    }

    private async Task DownloadSequentialAsync(string videoUrl, string destinationPath, long totalBytes, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        long existingLength = 0;
        if (File.Exists(destinationPath))
        {
            existingLength = new FileInfo(destinationPath).Length;
        }

        if (totalBytes > 0 && existingLength >= totalBytes)
        {
            progress?.Report(100.0);
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

        if (totalBytes <= 0)
        {
            totalBytes = response.Content.Headers.ContentLength ?? -1L;
            if (existingLength > 0) totalBytes += existingLength; // Adjust total if resumed
        }

        var canReportProgress = totalBytes > 0 && progress != null;

        using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var fileStream = new FileStream(destinationPath, FileMode.Append, FileAccess.Write, FileShare.None, 131072, true);

        var totalRead = existingLength;
        var buffer = new byte[131072];
        var isMoreToRead = true;
        var lastReportedPercentage = -1.0;
        var lastReportTime = DateTime.UtcNow;

        do
        {
            var read = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
            if (read == 0)
            {
                isMoreToRead = false;
            }
            else
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                totalRead += read;

                if (canReportProgress && progress != null)
                {
                    double currentPercentage = Math.Clamp((double)totalRead / totalBytes * 100, 0.0, 100.0);
                    var now = DateTime.UtcNow;
                    if (currentPercentage - lastReportedPercentage >= 0.5 || (now - lastReportTime).TotalMilliseconds >= 150 || totalRead == totalBytes)
                    {
                        lastReportedPercentage = currentPercentage;
                        lastReportTime = now;
                        progress.Report(currentPercentage);
                    }
                }
            }
        }
        while (isMoreToRead);

        if (canReportProgress && progress != null)
        {
            progress.Report(100.0);
        }
    }
}
