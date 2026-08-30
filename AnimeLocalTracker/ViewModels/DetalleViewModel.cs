using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Native;
using AnimeLocalTracker.Services.Python;
using AnimeLocalTracker.Messages;

namespace AnimeLocalTracker.ViewModels;

public partial class DetalleViewModel : ObservableObject, 
    IRecipient<UsuarioLogeadoMensaje>, 
    IRecipient<UsuarioDesconectadoMensaje>, 
    IRecipient<EpisodioActualizadoMensaje>,
    IRecipient<DescargaProgresoMensaje>
{
    private readonly IAnimeTrackingService _animeTrackingService;
    private readonly IDatabaseService _databaseService;
    private readonly IAuthService _authService;
    private readonly IFileScannerService _fileScannerService;
    private readonly IDialogService _dialogService;
    private readonly IDownloadService _downloadService;
    private readonly PythonEpisodeEnricher? _enricher;

    // Evita pasadas concurrentes de enriquecimiento (entrar/salir de la vista rápido
    // lanzaba varios ExtractFrame simultáneos sobre los mismos archivos).
    private readonly SemaphoreSlim _enriquecimientoGate = new(1, 1);
    
    [ObservableProperty]
    private AnimeItem? _animeSeleccionado;

    private List<EpisodioItem> _todosLosEpisodios = new();
    
    public ObservableCollection<EpisodioItem> EpisodiosDelAnime { get; } = [];

    [ObservableProperty]
    private bool _ordenAscendente = false;

    [ObservableProperty]
    private string _filtroEpisodios = "Todos";

    public string[] OpcionesFiltro { get; } = ["Todos", "Descargados", "Vistos", "No Vistos", "Favoritos"];

    [ObservableProperty] private string _mensajeSinEpisodios = "No hay episodios para mostrar";
    [ObservableProperty] private string _subtituloSinEpisodios = "El filtro actual no encontró coincidencias.";

    // === ACCIONES HERO Y DETALLES ===
    [ObservableProperty] private bool _sinopsisExpandida = false;
    [ObservableProperty] private bool _esFavoritoAnime = false;
    [ObservableProperty] private bool _tieneCapituloEnProgreso = false;

    public bool TieneEpisodios => EpisodiosDelAnime.Count > 0;

    // === EDITOR DE SEGUIMIENTO ===
    [ObservableProperty] private bool _mostrandoEditorSeguimiento;
    [ObservableProperty] private string _editEstado = "CURRENT";
    [ObservableProperty] private int _editProgreso;
    [ObservableProperty] private string _editProgresoTexto = "0";

    partial void OnEditProgresoTextoChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            _editProgreso = 0;
            return;
        }

        string soloDigitos = new string(value.Where(char.IsDigit).ToArray());
        if (string.IsNullOrEmpty(soloDigitos))
        {
            _editProgreso = 0;
            EditProgresoTexto = "0";
            return;
        }

        if (int.TryParse(soloDigitos, out int num))
        {
            int max = ObtenerMaximoEpisodiosEmitidos();
            if (num < 0) num = 0;
            if (max > 0 && num > max) num = max;

            _editProgreso = num;
            if (num.ToString() != value)
            {
                EditProgresoTexto = num.ToString();
            }
        }
    }

    public int ObtenerMaximoEpisodiosEmitidos()
    {
        if (AnimeSeleccionado == null) return 9999;
        if (AnimeSeleccionado.TotalEpisodios > 0) return AnimeSeleccionado.TotalEpisodios;
        if (_todosLosEpisodios.Count > 0) return _todosLosEpisodios.Count;
        if (EpisodiosDelAnime.Count > 0) return EpisodiosDelAnime.Count;
        return 9999;
    }

    [ObservableProperty] private float _editPuntaje;
    [ObservableProperty] private DateTime? _editFechaInicio;
    [ObservableProperty] private DateTime? _editFechaFin;
    [ObservableProperty] private string _editEstadoVisual = "Viendo";
    public List<string> OpcionesEstadoVisual { get; } = ["Viendo", "Finalizado", "En Pausa", "Abandonado", "Planeando"];

    [ObservableProperty] private bool _estaConectado;

    public DetalleViewModel(
        IAnimeTrackingService animeTrackingService, 
        IDatabaseService databaseService, 
        IAuthService authService, 
        IFileScannerService fileScannerService,
        IDialogService dialogService,
        IDownloadService downloadService,
        PythonEpisodeEnricher? enricher = null)
    {
        _animeTrackingService = animeTrackingService;
        _databaseService = databaseService;
        _authService = authService;
        _fileScannerService = fileScannerService;
        _dialogService = dialogService;
        _downloadService = downloadService;
        _enricher = enricher;
        
        WeakReferenceMessenger.Default.Register<UsuarioLogeadoMensaje>(this);
        WeakReferenceMessenger.Default.Register<UsuarioDesconectadoMensaje>(this);
        WeakReferenceMessenger.Default.Register<EpisodioActualizadoMensaje>(this);
        WeakReferenceMessenger.Default.Register<DescargaProgresoMensaje>(this);
        EstaConectado = _authService.EstaAutenticado();
    }

    public void Receive(UsuarioLogeadoMensaje message) => EstaConectado = true;
    public void Receive(UsuarioDesconectadoMensaje message) => EstaConectado = false;
    public void Receive(EpisodioActualizadoMensaje message)
    {
        if (AnimeSeleccionado == null || AnimeSeleccionado.AniListId != message.AnimeId) return;

        var episodio = _todosLosEpisodios.FirstOrDefault(e => e.NumeroEpisodio == message.NumeroEpisodio);
        if (episodio != null)
        {
            // Ejecutar en el hilo principal de la UI sin bloquear al emisor
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.HasShutdownStarted)
            {
                _ = dispatcher.InvokeAsync(() =>
                {
                    episodio.Visto = message.VistoLocal;
                    episodio.ProgresoSegundos = message.ProgresoSegundos;
                    if (message.TotalSegundos > 0)
                    {
                        episodio.TotalSegundos = message.TotalSegundos;
                    }
                    AplicarFiltrosYOrdenamiento();
                });
            }
            else
            {
                episodio.Visto = message.VistoLocal;
                episodio.ProgresoSegundos = message.ProgresoSegundos;
                if (message.TotalSegundos > 0)
                {
                    episodio.TotalSegundos = message.TotalSegundos;
                }
            }
        }
    }

    public void Receive(DescargaProgresoMensaje message)
    {
        if (AnimeSeleccionado == null || AnimeSeleccionado.AniListId != message.AniListId) return;

        // InvokeAsync (no Invoke): los ticks de progreso llegan desde tareas de descarga en
        // segundo plano; un Invoke síncrono por tick compite con el hilo de UI.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted) return;

        _ = dispatcher.InvokeAsync(() =>
        {
            var episodio = _todosLosEpisodios.FirstOrDefault(e => e.NumeroEpisodio == message.NumeroEpisodio);
            if (episodio != null)
            {
                episodio.IsDownloading = message.IsDownloading;
                episodio.DownloadProgress = message.Progreso;

                if (message.IsCompleted)
                {
                    episodio.Descargado = true;
                    episodio.RutaCompleta = message.RutaArchivo;
                    episodio.CalcularTamanoArchivo();

                    // Generar miniatura nativa y metadata técnica automáticamente sin salir de la pestaña
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(episodio.RutaCompleta))
                            {
                                string thumbPath = PythonEpisodeEnricher.ObtenerRutaMiniaturaEsperada(episodio.RutaCompleta);
                                bool extraido = false;
                                if (NativeMethods.IsAvailable)
                                {
                                    extraido = NativeMethods.ExtractFrame(episodio.RutaCompleta, thumbPath, 8.0, 320);
                                }
                                if (!extraido && _enricher != null)
                                {
                                    await _enricher.GenerarMiniaturaAsync(episodio);
                                }
                                if (File.Exists(thumbPath) && new FileInfo(thumbPath).Length > 0)
                                {
                                    episodio.RutaMiniatura = thumbPath;
                                }

                                if (_enricher != null)
                                {
                                    await _enricher.EnriquecerEpisodioAsync(episodio);
                                }

                                // Persistir inmediatamente en SQLite
                                try
                                {
                                    var reg = new RegistroEpisodio
                                    {
                                        AniListId = _animeSeleccionado?.AniListId ?? 0,
                                        NumeroEpisodio = episodio.NumeroEpisodio,
                                        RutaArchivo = episodio.RutaCompleta,
                                        Resolucion = episodio.Resolucion,
                                        CodecVideo = episodio.CodecVideo,
                                        Fps = episodio.Fps,
                                        Es10Bit = episodio.Es10Bit,
                                        RutaMiniatura = episodio.RutaMiniatura,
                                        VistoLocal = episodio.Visto,
                                        FavoritoLocal = episodio.Favorito,
                                        ProgresoSegundos = episodio.ProgresoSegundos,
                                        TotalSegundos = episodio.TotalSegundos
                                    };
                                    await _databaseService.GuardarRegistroEpisodioAsync(reg).ConfigureAwait(false);
                                }
                                catch { }

                                var disp = System.Windows.Application.Current?.Dispatcher;
                                if (disp != null && !disp.HasShutdownStarted)
                                {
                                    _ = disp.InvokeAsync(() =>
                                    {
                                        AplicarFiltrosYOrdenamiento();
                                    });
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Debug("DetalleViewModel", $"Error generando miniatura post-descarga: {ex.Message}");
                        }
                    });

                    _dialogService.MostrarDialogoAsync("Descarga Completada",
                        $"El episodio {episodio.NumeroEpisodio} se ha descargado exitosamente.",
                        false, "CheckCircleOutline", "#4CAF50");
                    AplicarFiltrosYOrdenamiento();
                }
                else if (!string.IsNullOrEmpty(message.Error))
                {
                    _dialogService.MostrarDialogoAsync("Error de descarga",
                        $"Error al descargar el episodio {episodio.NumeroEpisodio}:\n{message.Error}",
                        false, "AlertCircleOutline", "#E53935");
                }
            }
        });
    }

    public async Task InicializarAsync(AnimeItem anime)
    {
        EstaConectado = _authService.EstaAutenticado();
        AnimeSeleccionado = anime;
        _todosLosEpisodios.Clear();
        EpisodiosDelAnime.Clear();

        // 1. CARGA RÁPIDA DE BASE DE DATOS Y DISCO (SIN INTERNET)
        var registrosGuardados = await _databaseService.ObtenerRegistrosPorAnimeAsync(anime.AniListId);
        
        // Escaneamos la carpeta local si existe
        List<EpisodioItem> encontrados = new();
        if (!string.IsNullOrEmpty(anime.RutaCarpeta))
        {
            encontrados = await _fileScannerService.EscanearEpisodiosAsync(anime.RutaCarpeta);
        }

        int maxEpisodio = 0;
        if (encontrados.Count > 0)
            maxEpisodio = encontrados.Max(e => e.NumeroEpisodio);
        if (anime.TotalEpisodios > maxEpisodio)
            maxEpisodio = anime.TotalEpisodios;
        if (anime.EpisodiosVistos > maxEpisodio)
            maxEpisodio = anime.EpisodiosVistos;

        // Límite de seguridad para prevenir asignaciones anómalas de memoria (máx 3000)
        const int LimiteSeguridadEpisodios = 3000;
        int episodiosACargar = Math.Min(maxEpisodio, LimiteSeguridadEpisodios);

        // CARGA RÁPIDA: construimos la lista de episodios INMEDIATAMENTE sin esperar a
        // generar miniaturas (antes se extraían todas en lote antes de mostrar la lista:
        // con muchos episodios la pestaña quedaba bloqueada minutos/horas). Las miniaturas
        // faltantes se generan en segundo plano y aparecen progresivamente.
        var episodiosGenerados = await Task.Run(() =>
        {
            var archivosPorEp = encontrados.GroupBy(e => e.NumeroEpisodio)
                                           .ToDictionary(g => g.Key, g => g.First());
            var registrosPorEp = registrosGuardados.GroupBy(r => r.NumeroEpisodio)
                                                   .ToDictionary(g => g.Key, g => g.First());

            var temp = new List<EpisodioItem>(episodiosACargar);
            for (int i = 1; i <= episodiosACargar; i++)
            {
                archivosPorEp.TryGetValue(i, out var archivoLocal);
                registrosPorEp.TryGetValue(i, out var memoria);
                
                bool estaDescargando = _downloadService.EstaDescargando(anime.AniListId, i, out double prog);

                // Recuperar miniatura y metadata técnica si ya existen en caché local / SQLite
                string? thumbCache = null;
                string resolucionCache = string.Empty;
                string codecCache = string.Empty;
                string fpsCache = string.Empty;
                bool es10BitCache = false;

                if (archivoLocal != null && !string.IsNullOrWhiteSpace(archivoLocal.RutaCompleta))
                {
                    thumbCache = (!string.IsNullOrEmpty(memoria?.RutaMiniatura)
                                  && System.IO.File.Exists(memoria.RutaMiniatura)
                                  && new System.IO.FileInfo(memoria.RutaMiniatura).Length > 0)
                        ? memoria.RutaMiniatura
                        : PythonEpisodeEnricher.ObtenerRutaMiniaturaSiExiste(archivoLocal.RutaCompleta);

                    resolucionCache = memoria?.Resolucion ?? string.Empty;
                    codecCache = memoria?.CodecVideo ?? string.Empty;
                    fpsCache = memoria?.Fps ?? string.Empty;
                    es10BitCache = memoria?.Es10Bit ?? false;
                }

                var ep = new EpisodioItem
                {
                    NumeroEpisodio = i,
                    Descargado = archivoLocal != null,
                    RutaCompleta = archivoLocal?.RutaCompleta ?? string.Empty,
                    TamanoArchivoFormateado = archivoLocal?.TamanoArchivoFormateado ?? string.Empty,
                    Visto = memoria != null && memoria.VistoLocal,
                    Favorito = memoria != null && memoria.FavoritoLocal,
                    ProgresoSegundos = memoria?.ProgresoSegundos ?? 0,
                    TotalSegundos = memoria?.TotalSegundos ?? 0,
                    IsDownloading = estaDescargando,
                    DownloadProgress = prog,
                    Resolucion = resolucionCache,
                    CodecVideo = codecCache,
                    Fps = fpsCache,
                    Es10Bit = es10BitCache,
                    RutaMiniatura = thumbCache
                };
                
                if (ep.ProgresoSegundos > 0)
                {
                    AnimeLocalTracker.Services.AppLogger.Debug("DetalleViewModel", $"Cargado Episodio {ep.NumeroEpisodio}: Progreso={ep.ProgresoSegundos}/{ep.TotalSegundos}, Visto={ep.Visto}, TieneProgreso={ep.TieneProgresoGuardado}");
                }
                
                temp.Add(ep);
            }
            return temp;
        });

        _todosLosEpisodios.AddRange(episodiosGenerados);
        AplicarFiltrosYOrdenamiento();

        // Enriquecimiento Python (metadata ffprobe + miniaturas) en segundo plano solo para los que falten
        _ = EnriquecerEpisodiosEnSegundoPlanoAsync(anime.AniListId, AplicarFiltrosYOrdenamiento);
        _ = CargarProximosEpisodiosDeAniListAsync();
    }

    /// <summary>
    /// Enriquecer los episodios locales que aún no tienen metadata técnica o miniatura vía
    /// el bridge Python y guardar el resultado en SQLite para visitas instantáneas futuras.
    /// TODO el trabajo pesado (ffprobe, ffmpeg, SQLite) corre en el thread pool; al hilo de
    /// UI solo se le manda el repintado final vía Dispatcher. Antes este método se ejecutaba
    /// en el hilo de UI (los await del hilo llamador reanudaban en el SynchronizationContext)
    /// y cada episodio lanzaba un proceso ffmpeg síncrono → congelamiento total de la vista.
    /// </summary>
    private async Task EnriquecerEpisodiosEnSegundoPlanoAsync(int aniListId, Action alTerminar)
    {
        // Coalescing: si ya hay una pasada en curso, no abrir otra en paralelo.
        if (!_enriquecimientoGate.Wait(0)) return;

        try
        {
            await Task.Run(async () =>
            {
                var pendientes = _todosLosEpisodios
                    .Where(e => e.Descargado && !string.IsNullOrWhiteSpace(e.RutaCompleta) && System.IO.File.Exists(e.RutaCompleta) &&
                                (string.IsNullOrEmpty(e.RutaMiniatura) || string.IsNullOrEmpty(e.Resolucion)))
                    .ToList();

                if (pendientes.Count == 0) return;

                // Importante: NO esperar aquí el ping al daemon Python (si está frío puede
                // tardar 2-8 s). La extracción Rust de miniaturas no depende de Python:
                // se resuelve pythonDisponible de forma diferida, solo si se necesita.
                bool pythonDisponible = false;

                // ── FASE 1: Extracción PARALELA de miniaturas con Rust FFI, por chunks
                //    progresivos: cada chunk completado se persiste y se refleja en la UI
                //    antes de seguir, para que las miniaturas aparezcan de inmediato
                //    (no todas al final del lote completo). ──
                var sinMiniatura = pendientes.Where(e => string.IsNullOrEmpty(e.RutaMiniatura)).ToList();
                if (sinMiniatura.Count > 0)
                {
                    bool huboCambiosMiniatura = false;

                    if (NativeMethods.IsAvailable)
                    {
                        const int TamanoChunk = 16;
                        foreach (var chunk in sinMiniatura.Chunk(TamanoChunk))
                        {
                            var requests = chunk.Select(ep => new NativeFrameRequest
                            {
                                VideoPath = ep.RutaCompleta,
                                OutPath = PythonEpisodeEnricher.ObtenerRutaMiniaturaEsperada(ep.RutaCompleta),
                                // Timestamp corto (8s): decodificar desde el keyframe anterior
                                // hasta 8s es 4× más rápido que hasta 30s en HEVC/4K.
                                Timestamp = 8.0,
                                Width = 320
                            }).ToList();

                            var batchResults = NativeMethods.ExtractFramesBatch(requests);
                            if (batchResults != null)
                            {
                                foreach (var res in batchResults)
                                {
                                    if (res.Success && File.Exists(res.OutPath) && new FileInfo(res.OutPath).Length > 0)
                                    {
                                        string resNorm = Path.GetFullPath(res.OutPath);
                                        var ep = sinMiniatura.FirstOrDefault(e =>
                                            string.Equals(
                                                Path.GetFullPath(PythonEpisodeEnricher.ObtenerRutaMiniaturaEsperada(e.RutaCompleta)),
                                                resNorm,
                                                StringComparison.OrdinalIgnoreCase));

                                        if (ep != null)
                                        {
                                            ep.RutaMiniatura = res.OutPath;
                                            huboCambiosMiniatura = true;
                                        }
                                    }
                                }
                            }

                            // Persistir y refrescar la UI con el chunk ya terminado
                            var conMiniatura = chunk.Where(e => !string.IsNullOrEmpty(e.RutaMiniatura)).ToList();
                            if (conMiniatura.Count > 0)
                            {
                                await PersistirRegistrosAsync(aniListId, conMiniatura).ConfigureAwait(false);
                                var disp = System.Windows.Application.Current?.Dispatcher;
                                if (disp != null && !disp.HasShutdownStarted)
                                {
                                    _ = disp.InvokeAsync(() => alTerminar());
                                }
                            }

                            // Dejar respirar a la UI/otros hilos entre chunks
                            await Task.Delay(30).ConfigureAwait(false);
                        }
                    }

                    // Fallback individual SOLO si Rust no estuvo disponible (resolver el ping
                    // de Python aquí, de forma diferida, sin bloquear la extracción Rust)
                    if (!huboCambiosMiniatura)
                    {
                        pythonDisponible = _enricher != null && await _enricher.EstáDisponibleAsync().ConfigureAwait(false);
                        if (pythonDisponible)
                        {
                            foreach (var ep in sinMiniatura)
                            {
                                await _enricher.GenerarMiniaturaAsync(ep).ConfigureAwait(false);
                                if (!string.IsNullOrEmpty(ep.RutaMiniatura)) huboCambiosMiniatura = true;
                            }

                            if (huboCambiosMiniatura)
                            {
                                await PersistirRegistrosAsync(aniListId, sinMiniatura).ConfigureAwait(false);
                                var disp = System.Windows.Application.Current?.Dispatcher;
                                if (disp != null && !disp.HasShutdownStarted)
                                {
                                    _ = disp.InvokeAsync(() => alTerminar());
                                }
                            }
                        }
                    }
                }

                // ── FASE 2: Metadata técnica (ffprobe vía daemon Python) en segundo plano diferido ──
                var sinMetadata = pendientes.Where(e => string.IsNullOrEmpty(e.Resolucion)).ToList();
                if (sinMetadata.Count > 0)
                {
                    if (!pythonDisponible)
                        pythonDisponible = _enricher != null && await _enricher.EstáDisponibleAsync().ConfigureAwait(false);
                    if (pythonDisponible)
                    {
                        foreach (var ep in sinMetadata)
                        {
                            await _enricher!.EnriquecerEpisodioAsync(ep).ConfigureAwait(false);

                        if (!string.IsNullOrEmpty(ep.Resolucion))
                        {
                            await PersistirRegistrosAsync(aniListId, new[] { ep }).ConfigureAwait(false);

                            var disp = System.Windows.Application.Current?.Dispatcher;
                            if (disp != null && !disp.HasShutdownStarted)
                            {
                                _ = disp.InvokeAsync(() => alTerminar());
                            }
                        }

                        // Pausa de cortesía para no saturar CPU en segundo plano
                        await Task.Delay(20).ConfigureAwait(false);
                        }
                    }
                }
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLogger.Debug("DetalleViewModel", $"Error en enriquecimiento de fondo: {ex.Message}");
        }
        finally
        {
            _enriquecimientoGate.Release();
        }
    }

    /// <summary>
    /// Persiste los episodios con miniatura/metadata recién generada en SQLite para
    /// que las siguientes visitas al anime sean instantáneas (sin regenerar).
    /// </summary>
    private async Task PersistirRegistrosAsync(int aniListId, IEnumerable<EpisodioItem> episodios)
    {
        foreach (var ep in episodios)
        {
            try
            {
                var registro = new RegistroEpisodio
                {
                    AniListId = aniListId,
                    NumeroEpisodio = ep.NumeroEpisodio,
                    RutaArchivo = ep.RutaCompleta,
                    Resolucion = ep.Resolucion,
                    CodecVideo = ep.CodecVideo,
                    Fps = ep.Fps,
                    Es10Bit = ep.Es10Bit,
                    RutaMiniatura = ep.RutaMiniatura,
                    VistoLocal = ep.Visto,
                    FavoritoLocal = ep.Favorito,
                    ProgresoSegundos = ep.ProgresoSegundos,
                    TotalSegundos = ep.TotalSegundos
                };
                await _databaseService.GuardarRegistroEpisodioAsync(registro).ConfigureAwait(false);
            }
            catch { }
        }
    }

    /// <summary>
    /// Consulta AniList para informar de próximos episodios aún no emitidos/localizados.
    /// (Integración #6: temporalidad AniList vs episodios locales.)
    /// </summary>
    [ObservableProperty]
    private string _proximosEpisodiosTexto = string.Empty;

    [ObservableProperty]
    private bool _tieneProximosEpisodios;

    // ── Análisis de duplicados (perceptual hash vía Python) ──
    [ObservableProperty]
    private string _estadoDuplicados = string.Empty;

    [ObservableProperty]
    private bool _estaAnalizandoDuplicados;

    [RelayCommand]
    private async Task AnalizarDuplicadosAsync()
    {
        if (EstaAnalizandoDuplicados || _enricher == null) return;

        try
        {
            EstaAnalizandoDuplicados = true;
            EstadoDuplicados = string.Empty;
            var duplicados = await _enricher.EncontrarDuplicadosAsync(_todosLosEpisodios);
            if (duplicados.Count > 0)
            {
                EstadoDuplicados = $"{duplicados.Count} duplicado(s)";
                await _dialogService.MostrarDialogoAsync(
                    "Duplicados encontrados",
                    $"Se detectaron {duplicados.Count} archivos duplicados por hash perceptual:\n\n{string.Join("\n", duplicados.Select(d => System.IO.Path.GetFileName(d)).Take(10))}",
                    false, "ContentDuplicate", "#F59E0B");
            }
            else
            {
                EstadoDuplicados = string.Empty;
                await _dialogService.MostrarDialogoAsync(
                    "Análisis de duplicados",
                    "¡Excelente! No se encontraron episodios duplicados en esta serie.",
                    false, "CheckCircleOutline", "#10B981");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("DetalleViewModel", $"Error analizando duplicados: {ex.Message}");
            EstadoDuplicados = string.Empty;
            await _dialogService.MostrarDialogoAsync(
                "Error en análisis",
                $"No se pudo completar el análisis de duplicados: {ex.Message}",
                false, "AlertCircleOutline", "#EF4444");
        }
        finally
        {
            EstaAnalizandoDuplicados = false;
        }
    }

    private async Task CargarProximosEpisodiosDeAniListAsync()
    {
        try
        {
            if (AnimeSeleccionado == null) return;
            var datos = await _animeTrackingService.ObtenerAnimePorIdAsync(AnimeSeleccionado.AniListId);
            if (datos?.NextAiringEpisode == null) return;

            int proximo = datos.NextAiringEpisode.Episode;
            int maxLocal = _todosLosEpisodios.Count;
            int faltantes = Math.Max(0, proximo - 1 - maxLocal);

            if (faltantes > 0)
            {
                ProximosEpisodiosTexto = $"Episodio {proximo} confirmado en AniList — faltan {faltantes} por localizar";
                TieneProximosEpisodios = true;
            }
        }
        catch
        {
            // Fallo de red: no bloquear la vista
        }
    }

    partial void OnOrdenAscendenteChanged(bool value) => AplicarFiltrosYOrdenamiento();
    partial void OnFiltroEpisodiosChanged(string value) => AplicarFiltrosYOrdenamiento();

    private void AplicarFiltrosYOrdenamiento()
    {
        if (_todosLosEpisodios == null || _todosLosEpisodios.Count == 0)
        {
            EpisodiosDelAnime.Clear();
            if (AnimeSeleccionado != null && (AnimeSeleccionado.Estado == "NOT_YET_RELEASED" || AnimeSeleccionado.TotalEpisodios == 0))
            {
                MensajeSinEpisodios = "Anime aún no estrenado";
                SubtituloSinEpisodios = "Este anime aún no cuenta con episodios emitidos.";
            }
            else
            {
                MensajeSinEpisodios = "No hay episodios para mostrar";
                SubtituloSinEpisodios = "No se encontraron episodios para este anime.";
            }
            return;
        }

        var query = _todosLosEpisodios.AsEnumerable();

        switch (FiltroEpisodios)
        {
            case "Descargados":
                query = query.Where(e => e.Descargado);
                break;
            case "Vistos":
                query = query.Where(e => e.Visto);
                break;
            case "No Vistos":
                query = query.Where(e => !e.Visto);
                break;
            case "Favoritos":
                query = query.Where(e => e.Favorito);
                break;
        }

        query = OrdenAscendente ? query.OrderBy(e => e.NumeroEpisodio) : query.OrderByDescending(e => e.NumeroEpisodio);

        EpisodiosDelAnime.Clear();
        foreach (var ep in query) EpisodiosDelAnime.Add(ep);

        if (EpisodiosDelAnime.Count == 0)
        {
            MensajeSinEpisodios = "No hay episodios con este filtro";
            SubtituloSinEpisodios = $"No se encontraron episodios en la categoría '{FiltroEpisodios}'.";
        }

        TieneCapituloEnProgreso = _todosLosEpisodios != null && _todosLosEpisodios.Any(e => e.TieneProgresoGuardado);
    }

    [RelayCommand]
    private void VolverAGaleria()
    {
        WeakReferenceMessenger.Default.Send(new NavegarMensaje_Galeria());
    }
    
    [RelayCommand]
    private async Task EliminarAnimeActualAsync()
    {
        if (AnimeSeleccionado == null) return;

        bool confirmacion = await _dialogService.MostrarDialogoAsync(
            "Eliminar de la biblioteca", 
            $"¿Deseas eliminar '{AnimeSeleccionado.Titulo}' de tu biblioteca local?", 
            true, "HeartBrokenOutline", "#EF4444");

        if (confirmacion)
        {
            await _databaseService.EliminarAnimeAsync(AnimeSeleccionado);
            VolverAGaleria();
        }
    }
    
    [RelayCommand]
    private async Task ReproducirEpisodio(EpisodioItem episodio)
    {
        if (episodio == null || AnimeSeleccionado == null) return;
        
        if (!episodio.Descargado || !File.Exists(episodio.RutaCompleta))
        {
            await _dialogService.MostrarDialogoAsync("Episodio no encontrado", $"Archivo no encontrado para el episodio {episodio.NumeroEpisodio}.\nBuscando opciones de descarga en el navegador web...", false, "InformationOutline", "#FFC107");

            string numeroEp = episodio.NumeroEpisodio.ToString("D2"); 
            string busqueda = $"{AnimeSeleccionado.Titulo} {numeroEp}";
            string url = $"https://nyaa.si/?f=0&c=0_0&q={Uri.EscapeDataString(busqueda)}";
            
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
            return; 
        }

        try
        {
            // Enviamos un mensaje a la aplicación principal para que abra nuestra nueva ventana de reproductor
            var episodiosDisponibles = _todosLosEpisodios.Where(e => e.Descargado && File.Exists(e.RutaCompleta)).ToList();
            WeakReferenceMessenger.Default.Send(new NavegarMensaje_Reproductor(
                episodio.RutaCompleta,
                AnimeSeleccionado.AniListId,
                AnimeSeleccionado.Titulo,
                episodio.NumeroEpisodio,
                EpisodiosDisponibles: episodiosDisponibles
            ));
        }
        catch (System.Exception ex)
        {
            await _dialogService.MostrarDialogoAsync("Error", $"Error al intentar iniciar el reproductor: {ex.Message}", false, "AlertCircleOutline", "#E53935");
        }
    }
    
    [RelayCommand]
    private async Task AlternarFavoritoEpisodioAsync(EpisodioItem episodio)
    {
        if (episodio == null || AnimeSeleccionado == null) return;
        
        episodio.Favorito = !episodio.Favorito;
        
        var registro = new RegistroEpisodio
        {
            AniListId = AnimeSeleccionado.AniListId,
            NumeroEpisodio = episodio.NumeroEpisodio,
            RutaArchivo = episodio.RutaCompleta,
            VistoLocal = episodio.Visto,
            FavoritoLocal = episodio.Favorito,
            SincronizadoEnNube = false 
        };
        
        await _databaseService.GuardarRegistroEpisodioAsync(registro);
        
        if (FiltroEpisodios == "Favoritos")
        {
            AplicarFiltrosYOrdenamiento();
        }
    }
    
    [RelayCommand]
    private void AlternarSinopsis()
    {
        SinopsisExpandida = !SinopsisExpandida;
    }

    [RelayCommand]
    private void AlternarFavoritoAnime()
    {
        EsFavoritoAnime = !EsFavoritoAnime;
    }

    [RelayCommand]
    private void AbrirWebView()
    {
        if (AnimeSeleccionado == null) return;
        string url = $"https://anilist.co/anime/{AnimeSeleccionado.AniListId}";
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLogger.Error("DetalleViewModel", "Error abriendo WebView de AniList", ex);
        }
    }

    [RelayCommand]
    private async Task ReanudarAsync()
    {
        if (_todosLosEpisodios.Count == 0) return;

        // Reproducir el episodio que se dejó a medias con progreso guardado
        var epEnCurso = _todosLosEpisodios.FirstOrDefault(e => e.TieneProgresoGuardado);
        if (epEnCurso != null)
        {
            await ReproducirEpisodio(epEnCurso);
        }
    }

    [RelayCommand]
    private async Task MarcarVistosAsync(System.Collections.IList? episodiosSeleccionados)
    {
        if (AnimeSeleccionado == null) return;

        List<EpisodioItem> episodios;
        if (episodiosSeleccionados != null && episodiosSeleccionados.Count > 0)
        {
            episodios = episodiosSeleccionados.Cast<EpisodioItem>().ToList();
        }
        else
        {
            episodios = EpisodiosDelAnime.Where(e => !e.Visto).ToList();
        }

        if (episodios.Count == 0) return;

        var listaRegistros = new List<RegistroEpisodio>(episodios.Count);
        foreach (var ep in episodios)
        {
            ep.Visto = true;
            listaRegistros.Add(new RegistroEpisodio 
            {
                AniListId = AnimeSeleccionado.AniListId,
                NumeroEpisodio = ep.NumeroEpisodio,
                VistoLocal = true,
                FavoritoLocal = ep.Favorito,
                RutaArchivo = ep.RutaCompleta ?? string.Empty
            });
        }
        await _databaseService.GuardarRegistrosEpisodioBulkAsync(listaRegistros);
        AnimeSeleccionado.EpisodiosVistos = _todosLosEpisodios.Count(e => e.Visto);
        await _databaseService.ActualizarAnimeAsync(AnimeSeleccionado);
        WeakReferenceMessenger.Default.Send(new EpisodioActualizadoMensaje(AnimeSeleccionado.AniListId, 0, false, 0, 0));
        AplicarFiltrosYOrdenamiento();
    }

    [RelayCommand]
    private async Task MarcarNoVistosAsync(System.Collections.IList? episodiosSeleccionados)
    {
        if (AnimeSeleccionado == null) return;

        List<EpisodioItem> episodios;
        if (episodiosSeleccionados != null && episodiosSeleccionados.Count > 0)
        {
            episodios = episodiosSeleccionados.Cast<EpisodioItem>().ToList();
        }
        else
        {
            episodios = EpisodiosDelAnime.Where(e => e.Visto).ToList();
        }

        if (episodios.Count == 0) return;

        var listaRegistros = new List<RegistroEpisodio>(episodios.Count);
        foreach (var ep in episodios)
        {
            ep.Visto = false;
            listaRegistros.Add(new RegistroEpisodio 
            {
                AniListId = AnimeSeleccionado.AniListId,
                NumeroEpisodio = ep.NumeroEpisodio,
                VistoLocal = false,
                FavoritoLocal = ep.Favorito,
                RutaArchivo = ep.RutaCompleta ?? string.Empty
            });
        }
        await _databaseService.GuardarRegistrosEpisodioBulkAsync(listaRegistros);
        AnimeSeleccionado.EpisodiosVistos = _todosLosEpisodios.Count(e => e.Visto);
        await _databaseService.ActualizarAnimeAsync(AnimeSeleccionado);
        WeakReferenceMessenger.Default.Send(new EpisodioActualizadoMensaje(AnimeSeleccionado.AniListId, 0, false, 0, 0));
        AplicarFiltrosYOrdenamiento();
    }

    [RelayCommand]
    private async Task ActualizarAnimeActualAsync()
    {
        if (AnimeSeleccionado == null) return;
        
        var datosFrescos = await _animeTrackingService.ObtenerAnimePorIdAsync(AnimeSeleccionado.AniListId);
        if (datosFrescos != null)
        {
            string estadoFresco = datosFrescos.Status?.ToUpperInvariant() ?? "UNKNOWN";
            int episodiosEmitidos = 0;

            if (estadoFresco == "NOT_YET_RELEASED")
            {
                episodiosEmitidos = 0;
            }
            else if (estadoFresco == "RELEASING")
            {
                if (datosFrescos.NextAiringEpisode != null)
                {
                    episodiosEmitidos = Math.Max(0, datosFrescos.NextAiringEpisode.Episode - 1);
                }
                else
                {
                    episodiosEmitidos = datosFrescos.Episodes ?? 0;
                }
            }
            else
            {
                episodiosEmitidos = datosFrescos.Episodes ?? AnimeSeleccionado.TotalEpisodios;
            }

            var titulosAlt = new List<string>();
            if (!string.IsNullOrWhiteSpace(datosFrescos.Title.English)) titulosAlt.Add(datosFrescos.Title.English);
            if (!string.IsNullOrWhiteSpace(datosFrescos.Title.UserPreferred) && datosFrescos.Title.UserPreferred != datosFrescos.Title.Romaji) titulosAlt.Add(datosFrescos.Title.UserPreferred);
            if (datosFrescos.Synonyms != null) titulosAlt.AddRange(datosFrescos.Synonyms.Where(s => !string.IsNullOrWhiteSpace(s)));
            AnimeSeleccionado.NombresAlternativos = string.Join(" | ", titulosAlt.Distinct());

            AnimeSeleccionado.TotalEpisodios = episodiosEmitidos;
            AnimeSeleccionado.Estado = datosFrescos.Status ?? "UNKNOWN";
            AnimeSeleccionado.Generos = datosFrescos.Genres != null ? string.Join(", ", datosFrescos.Genres) : "";
            AnimeSeleccionado.UrlPortada = datosFrescos.CoverImage?.ExtraLarge ?? datosFrescos.CoverImage?.Large ?? AnimeSeleccionado.UrlPortada;
            
            await _databaseService.ActualizarAnimeAsync(AnimeSeleccionado);
            
            await InicializarAsync(AnimeSeleccionado);
            
            await _dialogService.MostrarDialogoAsync("Actualizado", $"Anime actualizado. Total de episodios emitidos: {episodiosEmitidos}", false, "CheckCircleOutline", "#4CAF50");
        }
        else
        {
            await _dialogService.MostrarDialogoAsync("Error", "Error al conectar con AniList para actualizar.", false, "AlertCircleOutline", "#E53935");
        }
    }
    
    [RelayCommand]
    private async Task AbrirEditorSeguimientoAsync()
    {
        if (AnimeSeleccionado == null) return;
        
        EditEstado = "CURRENT";
        EditProgreso = AnimeSeleccionado.EpisodiosVistos;
        EditProgresoTexto = EditProgreso.ToString();
        EditPuntaje = 0;
        EditFechaInicio = null;
        EditFechaFin = null;
        MostrandoEditorSeguimiento = true;

        var token = _authService.ObtenerTokenGuardado();
        if (string.IsNullOrEmpty(token)) return;

        var datos = await _animeTrackingService.ObtenerSeguimientoUsuarioAsync(AnimeSeleccionado.AniListId, token);
        if (datos != null)
        {
            EditEstadoVisual = ConvertirEstadoAEspanol(datos.Status ?? "CURRENT");
            int max = ObtenerMaximoEpisodiosEmitidos();
            EditProgreso = Math.Clamp(datos.Progress, 0, max > 0 ? max : 9999);
            EditProgresoTexto = EditProgreso.ToString();
            EditPuntaje = datos.Score;
            
            if (datos.StartedAt != null && datos.StartedAt.Year.HasValue)
                EditFechaInicio = new DateTime(datos.StartedAt.Year.Value, datos.StartedAt.Month ?? 1, datos.StartedAt.Day ?? 1);
            
            if (datos.CompletedAt != null && datos.CompletedAt.Year.HasValue)
                EditFechaFin = new DateTime(datos.CompletedAt.Year.Value, datos.CompletedAt.Month ?? 1, datos.CompletedAt.Day ?? 1);
        }
    }

    [RelayCommand]
    private async Task GuardarEditorSeguimientoAsync()
    {
        if (AnimeSeleccionado == null) return;
        
        var token = _authService.ObtenerTokenGuardado();
        if (string.IsNullOrEmpty(token)) 
        {
            await _dialogService.MostrarDialogoAsync("Error de Autenticación", "Debes conectar tu cuenta de AniList primero.", false, "AlertCircleOutline", "#E53935");
            return;
        }

        int max = ObtenerMaximoEpisodiosEmitidos();
        int progresoFinal = Math.Clamp(EditProgreso, 0, max > 0 ? max : 9999);
        string estadoEnIngles = ConvertirEstadoAIngles(EditEstadoVisual);
        bool exito = await _animeTrackingService.GuardarSeguimientoUsuarioAsync(
            AnimeSeleccionado.AniListId, estadoEnIngles, progresoFinal, EditPuntaje, EditFechaInicio, EditFechaFin, token);
            
        if (exito)
        {
            MostrandoEditorSeguimiento = false;
            AnimeSeleccionado.EstadoUsuario = estadoEnIngles;
            AnimeSeleccionado.EpisodiosVistos = progresoFinal;
            await _databaseService.ActualizarAnimeAsync(AnimeSeleccionado);
            await _dialogService.MostrarDialogoAsync("Nube Sincronizada", "¡Seguimiento actualizado en AniList con éxito!", false, "CloudCheck", "#4CAF50");
        }
        else
        {
            await _dialogService.MostrarDialogoAsync("Error de Sincronización", "Hubo un error de comunicación al intentar guardar tus datos en AniList.", false, "AlertCircleOutline", "#E53935");
        }
    }
    
    [RelayCommand]
    private void CerrarEditorSeguimiento()
    {
        MostrandoEditorSeguimiento = false;
    }
    
    private static string ConvertirEstadoAIngles(string estadoVisual) => estadoVisual switch
    {
        "Viendo" => "CURRENT",
        "Finalizado" => "COMPLETED",
        "En Pausa" => "PAUSED",
        "Abandonado" => "DROPPED",
        "Planeando" => "PLANNING",
        _ => "CURRENT"
    };

    private static string ConvertirEstadoAEspanol(string estadoIngles) => estadoIngles switch
    {
        "CURRENT" => "Viendo",
        "COMPLETED" => "Finalizado",
        "PAUSED" => "En Pausa",
        "DROPPED" => "Abandonado",
        "PLANNING" => "Planeando",
        _ => "Viendo"
    };

    [RelayCommand]
    private async Task DescargarEpisodioAsync(EpisodioItem episodio)
    {
        if (episodio == null || AnimeSeleccionado == null) return;
        if (episodio.IsDownloading) return;

        episodio.IsDownloading = true;
        episodio.DownloadProgress = 0;

        var titulosCandidatos = new List<string>();
        if (!string.IsNullOrWhiteSpace(AnimeSeleccionado.NombresAlternativos))
        {
            titulosCandidatos.AddRange(AnimeSeleccionado.NombresAlternativos.Split([" | ", ";"], StringSplitOptions.RemoveEmptyEntries));
        }

        await _downloadService.IniciarDescargaEpisodioAsync(
            AnimeSeleccionado.AniListId, 
            AnimeSeleccionado.Titulo, 
            AnimeSeleccionado.RutaCarpeta, 
            episodio.NumeroEpisodio,
            titulosCandidatos);
    }
}
