using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
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
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, DownloadState> _activeDownloads = new();
    private readonly SemaphoreSlim _downloadLock = new(1, 1);
    private long _ordenCounter = 0;

    private class DownloadState
    {
        public long Orden { get; set; }
        public int AniListId { get; set; }
        public string AnimeTitulo { get; set; } = string.Empty;
        public int NumeroEpisodio { get; set; }
        public double Progreso { get; set; }
        public string RutaDestino { get; set; } = string.Empty;
        public string RutaTemporal { get; set; } = string.Empty;
        public CancellationTokenSource Cts { get; set; } = new();
        public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;
    }

    public DownloadService(IHttpClientFactory httpClientFactory)
    {
        _httpClient = httpClientFactory.CreateClient("Downloader");
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
            try
            {
                state.Cts.Cancel();
            }
            catch { }

            try
            {
                if (!string.IsNullOrEmpty(state.RutaTemporal) && File.Exists(state.RutaTemporal))
                {
                    File.Delete(state.RutaTemporal);
                }
            }
            catch { }

            WeakReferenceMessenger.Default.Send(new DescargaProgresoMensaje(aniListId, numeroEpisodio, 0, isDownloading: false, isCompleted: false, "", "Descarga cancelada", state.AnimeTitulo));
        }
    }

    public void CancelarTodas()
    {
        foreach (var kvp in _activeDownloads)
        {
            if (_activeDownloads.TryRemove(kvp.Key, out var state))
            {
                try
                {
                    state.Cts.Cancel();
                }
                catch { }

                try
                {
                    if (!string.IsNullOrEmpty(state.RutaTemporal) && File.Exists(state.RutaTemporal))
                    {
                        File.Delete(state.RutaTemporal);
                    }
                }
                catch { }

                WeakReferenceMessenger.Default.Send(new DescargaProgresoMensaje(state.AniListId, state.NumeroEpisodio, 0, isDownloading: false, isCompleted: false, "", "Descarga cancelada", state.AnimeTitulo));
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
                RutaArchivo = s.RutaDestino
            })
            .ToList();
    }

    public async Task IniciarDescargaEpisodioAsync(int aniListId, string animeTitulo, string carpetaDestino, int numeroEpisodio, IEnumerable<string>? titulosAlternativos = null)
    {
        string key = $"{aniListId}_{numeroEpisodio}";
        if (_activeDownloads.ContainsKey(key)) return;

        var state = new DownloadState
        {
            Orden = Interlocked.Increment(ref _ordenCounter),
            AniListId = aniListId,
            AnimeTitulo = animeTitulo,
            NumeroEpisodio = numeroEpisodio,
            Progreso = 0
        };

        if (!_activeDownloads.TryAdd(key, state)) return;

        // Notificar que entró en cola de descarga
        WeakReferenceMessenger.Default.Send(new DescargaProgresoMensaje(aniListId, numeroEpisodio, 0, isDownloading: true, isCompleted: false, "", animeTitulo: animeTitulo));

        // Preparar lista de todos los títulos candidatos
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

        _ = Task.Run(async () =>
        {
            string tempPath = string.Empty;
            string targetPath = string.Empty;

            // Esperar turno en la cola (1 descarga a la vez)
            await _downloadLock.WaitAsync(state.Cts.Token);
            try
            {
                if (state.Cts.IsCancellationRequested) return;

                // Paso 1: Buscar y extraer URL directa probando nombres principales y alternativos
                string? directUrl = await BuscarUrlEpisodioEnAnimeAv1Async(todosLosTitulos, numeroEpisodio, state.Cts.Token);
                
                if (string.IsNullOrEmpty(directUrl))
                {
                    Debug.WriteLine($"[DownloadService] No se encontró enlace para '{animeTitulo}' Ep {numeroEpisodio}.");
                    _activeDownloads.TryRemove(key, out _);
                    WeakReferenceMessenger.Default.Send(new DescargaProgresoMensaje(
                        aniListId, numeroEpisodio, 0, isDownloading: false, isCompleted: false, "", 
                        $"No se encontró el episodio {numeroEpisodio} en el servidor con ningún nombre conocido.", animeTitulo));
                    return;
                }

                // Paso 2: Preparar rutas (temporal para descarga y final para reproducción)
                if (!Directory.Exists(carpetaDestino))
                {
                    Directory.CreateDirectory(carpetaDestino);
                }
                targetPath = Path.Combine(carpetaDestino, $"Episodio {numeroEpisodio:D2}.mp4");
                tempPath = targetPath + ".downloading";
                state.RutaDestino = targetPath;
                state.RutaTemporal = tempPath;

                // Paso 3: Descarga paralela multi-hilo en el archivo temporal
                var progress = new Progress<double>(p =>
                {
                    state.Progreso = p;
                    WeakReferenceMessenger.Default.Send(new DescargaProgresoMensaje(aniListId, numeroEpisodio, p, isDownloading: true, isCompleted: false, targetPath, animeTitulo: animeTitulo));
                });

                await DownloadVideoAsync(directUrl, tempPath, progress, state.Cts.Token);

                // Paso 4: Finalización exitosa: solo al completar el 100% de los bytes se promueve al archivo final
                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                }
                File.Move(tempPath, targetPath);

                _activeDownloads.TryRemove(key, out _);
                WeakReferenceMessenger.Default.Send(new DescargaProgresoMensaje(aniListId, numeroEpisodio, 100, isDownloading: false, isCompleted: true, targetPath, animeTitulo: animeTitulo));
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine($"[DownloadService] Descarga cancelada: {animeTitulo} Ep {numeroEpisodio}");
                try
                {
                    if (!string.IsNullOrEmpty(tempPath) && File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch { }
                _activeDownloads.TryRemove(key, out _);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DownloadService] Error descargando {animeTitulo} Ep {numeroEpisodio}: {ex.Message}");
                try
                {
                    if (!string.IsNullOrEmpty(tempPath) && File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch { }
                _activeDownloads.TryRemove(key, out _);
                WeakReferenceMessenger.Default.Send(new DescargaProgresoMensaje(aniListId, numeroEpisodio, 0, isDownloading: false, isCompleted: false, "", ex.Message, animeTitulo));
            }
            finally
            {
                _downloadLock.Release();
            }
        });

        await Task.CompletedTask;
    }

    private async Task<string?> BuscarUrlEpisodioEnAnimeAv1Async(IEnumerable<string> titulos, int numeroEpisodio, CancellationToken cancellationToken)
    {
        var titulosLista = titulos.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        var slugsProbados = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // FASE 1: Probar slugs generados directamente a partir de todos los títulos válidos
        foreach (var titulo in titulosLista)
        {
            var variaciones = GenerarVariacionesSlug(titulo);
            foreach (var slug in variaciones)
            {
                if (slugsProbados.Add(slug) && EsSlugCompatible(slug, titulosLista))
                {
                    string pageUrl = $"https://animeav1.com/media/{slug}/{numeroEpisodio}";
                    string? videoUrl = await GetVideoUrlAsync(pageUrl, cancellationToken);
                    if (!string.IsNullOrEmpty(videoUrl))
                    {
                        return videoUrl;
                    }
                }
            }
        }

        // FASE 2: Si no coincidió directamente, buscar en el catálogo de AnimeAV1 pero con validación estricta
        foreach (var titulo in titulosLista)
        {
            var terminosBusqueda = GenerarTerminosBusqueda(titulo);
            foreach (var termino in terminosBusqueda)
            {
                try
                {
                    string searchUrl = $"https://animeav1.com/catalogo?search={Uri.EscapeDataString(termino)}";
                    using var req = new HttpRequestMessage(HttpMethod.Get, searchUrl);
                    req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

                    using var res = await _httpClient.SendAsync(req, cancellationToken);
                    if (res.IsSuccessStatusCode)
                    {
                        var html = await res.Content.ReadAsStringAsync(cancellationToken);

                        // Extraer slugs de los enlaces /media/{slug} o de JSON slug:"{slug}"
                        var matches = System.Text.RegularExpressions.Regex.Matches(html, @"(?:/media/|slug:\s*""?)([a-zA-Z0-9_-]+)");
                        foreach (System.Text.RegularExpressions.Match match in matches)
                        {
                            string discoveredSlug = match.Groups[1].Value.Trim('"', '\'');
                            if (!string.IsNullOrWhiteSpace(discoveredSlug) && 
                                !discoveredSlug.Equals("catalogo", StringComparison.OrdinalIgnoreCase) &&
                                slugsProbados.Add(discoveredSlug))
                            {
                                // VALIDACIÓN CRÍTICA: Asegurar que el slug encontrado corresponde a la temporada/secuela exacta solicitada
                                if (EsSlugCompatible(discoveredSlug, titulosLista))
                                {
                                    string pageUrl = $"https://animeav1.com/media/{discoveredSlug}/{numeroEpisodio}";
                                    string? videoUrl = await GetVideoUrlAsync(pageUrl, cancellationToken);
                                    if (!string.IsNullOrEmpty(videoUrl))
                                    {
                                        return videoUrl;
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[DownloadService] Error en búsqueda de catálogo para '{termino}': {ex.Message}");
                }
            }
        }

        return null;
    }

    private static bool EsSlugCompatible(string slug, IEnumerable<string> titulos)
    {
        string s = slug.ToLower();

        foreach (var tit in titulos)
        {
            string t = tit.ToLower();

            // 1. Validar temporadas
            bool tEsTemp2 = t.Contains(" 2nd season") || t.Contains(" season 2") || t.Contains(" 2da temporada") || t.Contains(" 2ª temporada") || t.Contains(" ii") || t.EndsWith(" 2") || t.Contains(" 2:") || t.Contains("part 2") || t.Contains("part ii");
            bool tEsTemp3 = t.Contains(" 3rd season") || t.Contains(" season 3") || t.Contains(" 3ra temporada") || t.Contains(" 3ª temporada") || t.Contains(" iii") || t.EndsWith(" 3") || t.Contains(" 3:") || t.Contains("part 3") || t.Contains("part iii");
            bool tEsTemp4 = t.Contains(" 4th season") || t.Contains(" season 4") || t.Contains(" iv") || t.EndsWith(" 4") || t.Contains(" 4:");
            bool tEsPelicula = t.Contains("movie") || t.Contains("pelicula") || t.Contains("película") || t.Contains("film") || t.Contains("gekijouban") || t.Contains("zankyou-hen") || t.Contains("zankyou");

            bool sEsTemp2 = s.Contains("-2nd-season") || s.Contains("-season-2") || s.Contains("-ii") || s.EndsWith("-2") || s.Contains("-2-") || s.Contains("-part-2");
            bool sEsTemp3 = s.Contains("-3rd-season") || s.Contains("-season-3") || s.Contains("-iii") || s.EndsWith("-3") || s.Contains("-3-") || s.Contains("-part-3");
            bool sEsTemp4 = s.Contains("-4th-season") || s.Contains("-season-4") || s.Contains("-iv") || s.EndsWith("-4") || s.Contains("-4-");
            bool sEsPelicula = s.Contains("-movie") || s.Contains("-pelicula") || s.Contains("-film") || s.Contains("-gekijouban") || s.Contains("zankyou");

            // Si el título es temporada 2, el slug NO puede ser temporada 1 ni temporada 3
            if (tEsTemp2 && !sEsTemp2) continue;
            if (tEsTemp3 && !sEsTemp3) continue;
            if (tEsTemp4 && !sEsTemp4) continue;
            if (tEsPelicula && !sEsPelicula) continue;

            // Si el título es temporada 1 pura (sin temporada ni película), el slug NO debe ser temporada 2/3/película
            bool tEsTemp1Pura = !tEsTemp2 && !tEsTemp3 && !tEsTemp4 && !tEsPelicula;
            if (tEsTemp1Pura && (sEsTemp2 || sEsTemp3 || sEsTemp4 || sEsPelicula)) continue;

            // 2. Validar subtítulos distintivos (ej: ": Zankyou-hen", ": Yuukaku-hen", ": Mugen Ressha-hen")
            var partesSub = tit.Split([':', '–'], StringSplitOptions.RemoveEmptyEntries);
            if (partesSub.Length > 1)
            {
                string subtitulo = partesSub[1].Trim().ToLower();
                var palabrasSub = subtitulo.Split([' ', '-', '!', '?', '.', ',', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
                                           .Where(w => w.Length > 3 && !w.Equals("season", StringComparison.OrdinalIgnoreCase) && !w.Equals("hen", StringComparison.OrdinalIgnoreCase))
                                           .ToList();

                if (palabrasSub.Count > 0)
                {
                    // Si el título original tiene palabras clave en su subtítulo (ej: "zankyou"), al menos una debe estar en el slug
                    bool contienePalabraSub = palabrasSub.Any(w => s.Contains(w));
                    if (!contienePalabraSub) continue;
                }
            }

            return true;
        }

        return false;
    }

    private static List<string> GenerarVariacionesSlug(string titulo)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(titulo)) return list;

        string BaseSlug(string input)
        {
            string clean = System.Text.RegularExpressions.Regex.Replace(input.ToLower(), @"[^\w\s-]", " ");
            clean = System.Text.RegularExpressions.Regex.Replace(clean, @"\s+", "-").Trim('-');
            return clean;
        }

        string rawSlug = BaseSlug(titulo);
        if (!string.IsNullOrEmpty(rawSlug)) list.Add(rawSlug);

        // Variaciones de temporadas y números romanos
        (string, string)[] conversiones = [
            (" 2nd season", "-2nd-season"), (" 2nd season", "-2"), (" 2nd season", "-ii"),
            (" 3rd season", "-3rd-season"), (" 3rd season", "-3"), (" 3rd season", "-iii"),
            (" 4th season", "-4th-season"), (" 4th season", "-4"), (" 4th season", "-iv"),
            (" ii", "-ii"), (" ii", "-2"), (" ii", "-2nd-season"),
            (" iii", "-iii"), (" iii", "-3"), (" iii", "-3rd-season"),
            (" 2", "-2"), (" 2", "-ii"), (" 2", "-2nd-season"),
            (" 3", "-3"), (" 3", "-iii"), (" 3", "-3rd-season"),
            (" season 2", "-2"), (" season 2", "-2nd-season"),
            (" season 3", "-3"), (" season 3", "-3rd-season")
        ];

        string tLower = titulo.ToLower();
        foreach (var (patron, sufijo) in conversiones)
        {
            if (tLower.Contains(patron))
            {
                string baseName = BaseSlug(tLower.Replace(patron, ""));
                string candidate = $"{baseName}{sufijo}";
                if (!list.Contains(candidate)) list.Add(candidate);
            }
        }

        return list;
    }

    private static List<string> GenerarTerminosBusqueda(string titulo)
    {
        var terminos = new List<string>();
        if (string.IsNullOrWhiteSpace(titulo)) return terminos;

        // 1. Título completo limpio
        terminos.Add(titulo.Trim());

        // 2. Parte antes de dos puntos / subtítulo
        var partes = titulo.Split([':', '-', '–', '~', '('], StringSplitOptions.RemoveEmptyEntries);
        if (partes.Length > 1)
        {
            string mainPart = partes[0].Trim();
            if (!string.IsNullOrWhiteSpace(mainPart) && !terminos.Contains(mainPart, StringComparer.OrdinalIgnoreCase))
            {
                terminos.Add(mainPart);
            }
        }

        // 3. Primeras 2 o 3 palabras significativas
        var palabras = titulo.Split([' '], StringSplitOptions.RemoveEmptyEntries)
                             .Where(p => p.Length > 2)
                             .Take(3)
                             .ToList();
        if (palabras.Count > 0)
        {
            string shortTerm = string.Join(" ", palabras);
            if (!terminos.Contains(shortTerm, StringComparer.OrdinalIgnoreCase))
            {
                terminos.Add(shortTerm);
            }
        }

        return terminos;
    }

    public async Task<string?> GetVideoUrlAsync(string pageUrl, CancellationToken cancellationToken = default)
    {
        if (pageUrl.Contains("animeav1.com"))
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, pageUrl);
                req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

                using var res = await _httpClient.SendAsync(req, cancellationToken);
                if (!res.IsSuccessStatusCode) return null;

                var html = await res.Content.ReadAsStringAsync(cancellationToken);

                // 1. Buscar enlace de MP4Upload en los embeds o descargas
                var mp4UploadMatch = System.Text.RegularExpressions.Regex.Match(html, @"https?://(?:www\.)?mp4upload\.com/(?:embed-)?([a-zA-Z0-9]+)(?:\.html)?");
                if (mp4UploadMatch.Success)
                {
                    string mp4Id = mp4UploadMatch.Groups[1].Value;
                    var directMp4 = await ExtractFromMp4UploadAsync($"https://www.mp4upload.com/embed-{mp4Id}.html", cancellationToken);
                    if (!string.IsNullOrEmpty(directMp4))
                    {
                        return directMp4;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error fetching animeav1 page: {ex.Message}");
                return null;
            }
        }
        else if (pageUrl.Contains("mp4upload.com"))
        {
            return await ExtractFromMp4UploadAsync(pageUrl, cancellationToken);
        }

        return null;
    }

    private async Task<string?> ExtractFromMp4UploadAsync(string embedUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, embedUrl);
            req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            req.Headers.Add("Referer", "https://animeav1.com/");

            using var res = await _httpClient.SendAsync(req, cancellationToken);
            if (!res.IsSuccessStatusCode) return null;

            var html = await res.Content.ReadAsStringAsync(cancellationToken);

            // Extraer enlace directo .mp4 de player.src
            var srcMatch = System.Text.RegularExpressions.Regex.Match(html, @"src:\s*""(https?://[^""]+?\.(?:mp4|mkv)[^""]*)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (srcMatch.Success)
            {
                return srcMatch.Groups[1].Value;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MP4Upload extraction error: {ex.Message}");
        }

        return null;
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
            headReq.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
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
            catch { }
        }

        // Si HEAD no devolvió tamaño o soporte de rangos, probar con GET range 0-0
        if (totalBytes <= 0 || !supportsRanges)
        {
            using var testReq = new HttpRequestMessage(HttpMethod.Get, videoUrl);
            testReq.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
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
            catch { }
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
        // 6 conexiones paralelas para maximizar el ancho de banda sin saturar el host
        int segmentCount = 6;
        long segmentSize = totalBytes / segmentCount;

        // Pre-reservar el archivo en disco para evitar fragmentación
        using (var preAlloc = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 4096, useAsync: true))
        {
            preAlloc.SetLength(totalBytes);
        }

        using SafeFileHandle fileHandle = File.OpenHandle(destinationPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, FileOptions.Asynchronous);

        long totalDownloaded = 0;
        double lastReportedPercentage = -1.0;
        var lastReportTime = DateTime.UtcNow;

        var tasks = new Task[segmentCount];

        for (int i = 0; i < segmentCount; i++)
        {
            long start = i * segmentSize;
            long end = (i == segmentCount - 1) ? totalBytes - 1 : (start + segmentSize - 1);

            tasks[i] = Task.Run(async () =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, videoUrl);
                req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                req.Headers.Add("Referer", "https://www.mp4upload.com/");
                req.Headers.Range = new RangeHeaderValue(start, end);

                using var response = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                byte[] buffer = new byte[131072]; // 128 KB por lectura
                long currentOffset = start;

                while (currentOffset <= end)
                {
                    int bytesToRead = (int)Math.Min(buffer.Length, end - currentOffset + 1);
                    int read = await stream.ReadAsync(buffer.AsMemory(0, bytesToRead), cancellationToken);
                    if (read == 0) break;

                    await RandomAccess.WriteAsync(fileHandle, buffer.AsMemory(0, read), currentOffset, cancellationToken);
                    currentOffset += read;

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

    private async Task DownloadSequentialAsync(string videoUrl, string destinationPath, long totalBytes, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, videoUrl);
        req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        req.Headers.Add("Referer", "https://www.mp4upload.com/");

        using var response = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        if (totalBytes <= 0)
        {
            totalBytes = response.Content.Headers.ContentLength ?? -1L;
        }

        var canReportProgress = totalBytes > 0 && progress != null;

        using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true);

        var totalRead = 0L;
        var buffer = new byte[131072]; // 128 KB
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
