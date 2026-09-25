using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
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
    IRecipient<DescargaProgresoMensaje>,
    IDisposable
{
    private readonly IAnimeTrackingService _animeTrackingService;
    private readonly IDatabaseService _databaseService;
    private readonly IAuthService _authService;
    private readonly IFileScannerService _fileScannerService;
    private readonly IDialogService _dialogService;
    private readonly IDownloadService _downloadService;
    private readonly PythonEpisodeEnricher? _enricher;
    private readonly IPluginService? _pluginService;
    private readonly IVideoIntegrityService? _videoIntegrityService;
    private readonly INyaaSourceService? _nyaaSourceService;
    private readonly ISelectorTorrentService? _selectorTorrentService;
    private readonly ISettingsService? _settingsService;

    // Enriquecimiento de metadata/miniaturas de episodios (extraído a EpisodeEnrichmentCoordinator).
    private readonly EpisodeEnrichmentCoordinator _enrichmentCoordinator = new();

    /// <summary>
    /// Cancela las cargas de fondo de ESTA ficha (espacio en disco, próximos episodios, próxima
    /// emisión, datos extra, preferencias de emisión, enriquecimiento de episodios): si el usuario
    /// navega a otro anime antes de que terminen, no tiene sentido seguir golpeando disco/red por
    /// uno que ya no está en pantalla. Se crea uno nuevo en cada InicializarAsync (también en el
    /// refresco desde AniList) y el anterior se cancela.
    /// </summary>
    private CancellationTokenSource? _ctsCargaFicha;

    // CA1001: el coordinador de enriquecimiento (posee un SemaphoreSlim) y el CTS de las cargas de
    // fondo se liberan al descartar el ViewModel (los transients no los dispone el contenedor).
    public void Dispose()
    {
        DetenerContador();
        _ctsCargaFicha?.Cancel();
        _ctsCargaFicha?.Dispose();
        _enrichmentCoordinator.Dispose();
        _reproductorPreview?.Close();
        GC.SuppressFinalize(this);
    }
    
    [ObservableProperty]
    private AnimeItem? _animeSeleccionado;

    private List<EpisodioItem> _todosLosEpisodios = new();
    
    public ObservableCollection<EpisodioItem> EpisodiosDelAnime { get; } = [];

    [ObservableProperty]
    private bool _ordenAscendente = false;

    [ObservableProperty]
    private string _filtroEpisodios = LocalizationService.T("Filtro_Todos");

    public string[] OpcionesFiltro { get; } = [
        LocalizationService.T("Filtro_Todos"), 
        LocalizationService.T("Filtro_Descargados"), 
        LocalizationService.T("Filtro_Vistos"), 
        LocalizationService.T("Filtro_NoVistos"), 
        LocalizationService.T("Filtro_Favoritos")
    ];

    [ObservableProperty] private string _mensajeSinEpisodios = LocalizationService.T("Det_SinEpisodios");
    [ObservableProperty] private string _subtituloSinEpisodios = LocalizationService.T("Det_SinEpisodiosSub");

    // === ACCIONES HERO Y DETALLES ===
    [ObservableProperty] private bool _sinopsisExpandida = false;
    [ObservableProperty] private bool _esFavoritoAnime = false;
    [ObservableProperty] private bool _tieneCapituloEnProgreso = false;

    // === BANNER DE EPISODIOS FALTANTES (huecos en la carpeta local) ===
    [ObservableProperty] private bool _hayEpisodiosFaltantes;
    [ObservableProperty] private string _episodiosFaltantesTexto = string.Empty;
    private List<int> _numerosEpisodiosFaltantes = new();

    // === DOCTOR DE INTEGRIDAD DE VIDEO ===
    [ObservableProperty] private bool _verificandoIntegridad;

    public bool TieneEpisodios => EpisodiosDelAnime.Count > 0;

    // === EDITOR DE SEGUIMIENTO ===
    [ObservableProperty] private bool _mostrandoEditorSeguimiento;
    [ObservableProperty] private string _editEstado = "CURRENT";
    [ObservableProperty] private int _editProgreso;
    [ObservableProperty] private string _editProgresoTexto = "0";

    partial void OnEditProgresoTextoChanged(string value)
    {
        ProcesarProgresoTexto(value);
    }

    private void ProcesarProgresoTexto(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            EditProgreso = 0;
            return;
        }

        string soloDigitos = new string(value.Where(char.IsDigit).ToArray());
        if (string.IsNullOrEmpty(soloDigitos))
        {
            EditProgreso = 0;
            EditProgresoTexto = "0";
            return;
        }

        if (int.TryParse(soloDigitos, out int num))
        {
            int max = ObtenerMaximoEpisodiosEmitidos();
            if (num < 0) num = 0;
            if (max > 0 && num > max) num = max;

            EditProgreso = num;
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
    [ObservableProperty] private string _editEstadoVisual = LocalizationService.T("Estado_Viendo");
    public List<string> OpcionesEstadoVisual { get; } = [
        LocalizationService.T("Estado_Viendo"), 
        LocalizationService.T("Estado_Finalizado"), 
        LocalizationService.T("Estado_EnPausa"), 
        LocalizationService.T("Estado_Abandonado"), 
        LocalizationService.T("Estado_Planeando")
    ];

    [ObservableProperty] private bool _estaConectado;
    /// <summary>Fase 2d: espeja AppSettings.BusquedaTorrentHabilitada — controla si se
    /// muestra el botón "elegir torrent manualmente" junto al de descargar.</summary>
    [ObservableProperty] private bool _busquedaTorrentHabilitada;

    public DetalleViewModel(
        IAnimeTrackingService animeTrackingService, 
        IDatabaseService databaseService, 
        IAuthService authService, 
        IFileScannerService fileScannerService,
        IDialogService dialogService,
        IDownloadService downloadService,
        PythonEpisodeEnricher? enricher = null,
        IPluginService? pluginService = null,
        IVideoIntegrityService? videoIntegrityService = null,
        IProximaEmisionService? proximaEmision = null,
        IDatosExtraService? datosExtra = null,
        IEmisionMonitorService? monitorEmision = null,
        IAnimeThemesService? animeThemesService = null,
        IAnimeThemesDownloadService? animeThemesDownload = null,
        INyaaSourceService? nyaaSourceService = null,
        ISelectorTorrentService? selectorTorrentService = null,
        ISettingsService? settingsService = null)
    {
        _proximaEmision = proximaEmision;
        _datosExtra = datosExtra;
        _monitorEmision = monitorEmision;
        _animeThemesService = animeThemesService;
        _animeThemesDownload = animeThemesDownload;
        _animeTrackingService = animeTrackingService;
        _databaseService = databaseService;
        _authService = authService;
        _fileScannerService = fileScannerService;
        _dialogService = dialogService;
        _downloadService = downloadService;
        _enricher = enricher;
        _pluginService = pluginService;
        _videoIntegrityService = videoIntegrityService;
        _nyaaSourceService = nyaaSourceService;
        _selectorTorrentService = selectorTorrentService;
        _settingsService = settingsService;

        WeakReferenceMessenger.Default.Register<UsuarioLogeadoMensaje>(this);
        WeakReferenceMessenger.Default.Register<UsuarioDesconectadoMensaje>(this);
        WeakReferenceMessenger.Default.Register<EpisodioActualizadoMensaje>(this);
        WeakReferenceMessenger.Default.Register<DescargaProgresoMensaje>(this);
        EstaConectado = _authService.EstaAutenticado();

        // Fase 2d: el botón "elegir torrent manualmente" solo tiene sentido si la
        // búsqueda por torrent está activa — se mantiene sincronizado con Configuración.
        BusquedaTorrentHabilitada = _settingsService?.ObtenerConfiguracion()?.BusquedaTorrentHabilitada ?? false;
        if (_settingsService != null)
        {
            _settingsService.ConfiguracionModificada += config =>
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher != null && !dispatcher.HasShutdownStarted)
                {
                    dispatcher.Invoke(() => BusquedaTorrentHabilitada = config?.BusquedaTorrentHabilitada ?? false);
                }
            };
        }
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
                    AnimeSeleccionado.EpisodiosVistos = _todosLosEpisodios.Count(e => e.Visto);
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
                AnimeSeleccionado.EpisodiosVistos = _todosLosEpisodios.Count(e => e.Visto);
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
                    _ = CalcularEspacioEnDiscoAsync();

                    // Generar miniatura nativa y metadata técnica automáticamente sin salir de la pestaña
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(episodio.RutaCompleta))
                            {
                                string thumbPath = PythonEpisodeEnricher.ObtenerRutaMiniaturaEsperada(episodio.RutaCompleta);
                                bool extraido;
                                if (_enricher != null)
                                {
                                    // Rust primero, Python solo si hace falta, con reintento de
                                    // timestamp si el frame cae en una cortinilla casi negra.
                                    extraido = await _enricher.ExtraerMiniaturaAsync(episodio.RutaCompleta, thumbPath);
                                }
                                else
                                {
                                    extraido = NativeMethods.IsAvailable
                                               && NativeMethods.ExtractFrame(episodio.RutaCompleta, thumbPath, 2.0, 320)
                                               && PythonEpisodeEnricher.EsMiniaturaValida(thumbPath);
                                }
                                if (extraido)
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
                                        AniListId = AnimeSeleccionado?.AniListId ?? 0,
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

                                    // FUN-019: si el episodio ya estaba visto o a medias (registro
                                    // previo sin archivo local), la descarga NO debe resetear ese
                                    // estado: el guardado solo añade los metadatos del archivo nuevo.
                                    var registroPrevio = (await _databaseService.ObtenerRegistrosPorAnimeAsync(reg.AniListId).ConfigureAwait(false))
                                        ?.FirstOrDefault(r => r.NumeroEpisodio == reg.NumeroEpisodio);
                                    if (registroPrevio != null)
                                    {
                                        reg.VistoLocal = registroPrevio.VistoLocal;
                                        reg.FavoritoLocal = registroPrevio.FavoritoLocal;
                                        reg.ProgresoSegundos = registroPrevio.ProgresoSegundos;
                                        reg.TotalSegundos = registroPrevio.TotalSegundos;
                                        reg.UltimaReproduccion = registroPrevio.UltimaReproduccion;
                                    }

                                    await _databaseService.GuardarRegistroEpisodioAsync(reg).ConfigureAwait(false);
                                }
                                catch { }

                                // PERF-01: refresco coalescido (varios pueden completar a la vez)
                                SolicitarRefrescoEpisodios();
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Debug("DetalleViewModel", $"Error generando miniatura post-descarga: {ex.Message}");
                        }
                    });

                    _dialogService.MostrarDialogoAsync(LocalizationService.T("Det_DescargaCompletadaTitulo"),
                        string.Format(LocalizationService.T("Det_DescargaCompletadaMsj"), episodio.NumeroEpisodio),
                        false, "CheckCircleOutline", "#4CAF50");
                    AplicarFiltrosYOrdenamiento();
                }
                else if (!string.IsNullOrEmpty(message.Error))
                {
                    _dialogService.MostrarDialogoAsync(LocalizationService.T("Det_ErrorDescargaTitulo"),
                        string.Format(LocalizationService.T("Det_ErrorDescargaMsj"), episodio.NumeroEpisodio, message.Error),
                        false, "AlertCircleOutline", "#E53935");
                }
            }
        });
    }

    public async Task InicializarAsync(AnimeItem anime)
    {
        ReiniciarContadorProximo();
        ReiniciarExtras();
        ReiniciarMusica();
        EstaConectado = _authService.EstaAutenticado();
        AnimeSeleccionado = anime;
        EsFavoritoAnime = anime.EsFavorito;
        _todosLosEpisodios.Clear();
        EpisodiosDelAnime.Clear();

        // 1. CARGA RÁPIDA DE BASE DE DATOS Y DISCO (SIN INTERNET)
        var registrosGuardados = await _databaseService.ObtenerRegistrosPorAnimeAsync(anime.AniListId);
        anime.EpisodiosVistos = registrosGuardados.Count(r => r.VistoLocal);
        
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
                                  && PythonEpisodeEnricher.EsMiniaturaValida(memoria.RutaMiniatura)
                                  && !PythonEpisodeEnricher.EsFrameDemasiadoVacio(memoria.RutaMiniatura))
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
                    UltimaReproduccion = memoria?.UltimaReproduccion ?? DateTime.MinValue,
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

        // Navegación rápida entre fichas (flechas, clics seguidos): se cancelan las cargas de
        // fondo de la ficha anterior en vez de dejarlas terminar para un anime que ya no se ve.
        _ctsCargaFicha?.Cancel();
        _ctsCargaFicha?.Dispose();
        _ctsCargaFicha = new CancellationTokenSource();
        var ctFicha = _ctsCargaFicha.Token;

        // Enriquecimiento Python (metadata ffprobe + miniaturas) en segundo plano solo para los que falten
        _ = EnriquecerEpisodiosEnSegundoPlanoAsync(anime.AniListId, ctFicha);
        _ = CargarProximosEpisodiosDeAniListAsync(ctFicha);
        _ = CargarProximaEmisionAsync(ctFicha);
        _ = CalcularEspacioEnDiscoAsync(ctFicha);
        _ = CargarDatosExtraAsync(ctFicha);
        _ = CargarPreferenciasEmisionAsync(ctFicha);
        _ = CargarTemasMusicalesAsync(ctFicha);
    }

    /// <summary>
    /// Enriquecer los episodios locales que aún no tienen metadata técnica o miniatura vía
    /// el bridge Python y guardar el resultado en SQLite para visitas instantáneas futuras.
    /// La implementación vive en EpisodeEnrichmentCoordinator (extraída para reducir el
    /// tamaño de este ViewModel); aquí solo se le pasan las dependencias necesarias.
    /// </summary>
    private Task EnriquecerEpisodiosEnSegundoPlanoAsync(int aniListId, CancellationToken cancellationToken = default) =>
        _enrichmentCoordinator.EnriquecerEnSegundoPlanoAsync(
            aniListId, _todosLosEpisodios, _enricher, _databaseService, SolicitarRefrescoEpisodios, cancellationToken);

    // PERF-01: refrescos coalescidos de la lista de episodios — varios episodios pueden
    // completar su miniatura casi a la vez; repintar la lista completa por cada uno era
    // O(N²) en la UI. Un solo refresco por ráfaga vía el Dispatcher.
    private bool _refrescoListaEpisodiosPendiente;

    private void SolicitarRefrescoEpisodios()
    {
        if (_refrescoListaEpisodiosPendiente) return;
        _refrescoListaEpisodiosPendiente = true;

        var disp = System.Windows.Application.Current?.Dispatcher;
        if (disp == null || disp.HasShutdownStarted)
        {
            _refrescoListaEpisodiosPendiente = false;
            return;
        }

        _ = disp.InvokeAsync(() =>
        {
            _refrescoListaEpisodiosPendiente = false;
            AplicarFiltrosYOrdenamiento();
        });
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
                EstadoDuplicados = string.Format(LocalizationService.T("Det_DuplicadosContador"), duplicados.Count);
                await _dialogService.MostrarDialogoAsync(
                    LocalizationService.T("Det_DuplicadosEncontradosTitulo"),
                    string.Format(LocalizationService.T("Det_DuplicadosEncontradosMsj"), duplicados.Count, string.Join("\n", duplicados.Select(d => System.IO.Path.GetFileName(d)).Take(10))),
                    false, "ContentDuplicate", "#F59E0B");
            }
            else
            {
                EstadoDuplicados = string.Empty;
                await _dialogService.MostrarDialogoAsync(
                    LocalizationService.T("Det_AnalisisDuplicadosTitulo"),
                    LocalizationService.T("Det_SinDuplicadosMsj"),
                    false, "CheckCircleOutline", "#10B981");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("DetalleViewModel", $"Error analizando duplicados: {ex.Message}");
            EstadoDuplicados = string.Empty;
            await _dialogService.MostrarDialogoAsync(
                LocalizationService.T("Det_ErrorAnalisisTitulo"),
                string.Format(LocalizationService.T("Det_ErrorAnalisisMsj"), ex.Message),
                false, "AlertCircleOutline", "#EF4444");
        }
        finally
        {
            EstaAnalizandoDuplicados = false;
        }
    }

    private async Task CargarProximosEpisodiosDeAniListAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (AnimeSeleccionado == null || cancellationToken.IsCancellationRequested) return;
            var anime = AnimeSeleccionado;
            var datos = await _animeTrackingService.ObtenerAnimePorIdAsync(anime.AniListId);
            if (cancellationToken.IsCancellationRequested || !ReferenceEquals(anime, AnimeSeleccionado)) return; // se cambió de ficha mientras se consultaba
            if (datos?.NextAiringEpisode == null) return;

            int proximo = datos.NextAiringEpisode.Episode;
            int maxLocal = _todosLosEpisodios.Count;
            int faltantes = Math.Max(0, proximo - 1 - maxLocal);

            if (faltantes > 0)
            {
                ProximosEpisodiosTexto = string.Format(LocalizationService.T("Det_ProximosEpisodiosFormato"), proximo, faltantes);
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
                MensajeSinEpisodios = LocalizationService.T("Det_SinEpisodiosNoEstrenado");
                SubtituloSinEpisodios = LocalizationService.T("Det_SinEpisodiosNoEstrenadoSub");
            }
            else
            {
                MensajeSinEpisodios = LocalizationService.T("Det_SinEpisodios");
                SubtituloSinEpisodios = LocalizationService.T("Det_SinEpisodiosSub");
            }
            return;
        }

        // ARQ-01: filtrado/orden delegados a la lógica pura extraída (testeable sin UI)
        var query = Core.EpisodiosOrganizador.FiltrarYOrdenar(_todosLosEpisodios, FiltroEpisodios, OrdenAscendente);

        EpisodiosDelAnime.Clear();
        foreach (var ep in query) EpisodiosDelAnime.Add(ep);

        if (EpisodiosDelAnime.Count == 0)
        {
            MensajeSinEpisodios = LocalizationService.T("Det_SinEpisodiosFiltro");
            SubtituloSinEpisodios = string.Format(LocalizationService.T("Det_SinEpisodiosFiltroSub"), FiltroEpisodios);
        }

        TieneCapituloEnProgreso = _todosLosEpisodios != null && _todosLosEpisodios.Any(e => e.TieneProgresoGuardado);

        ActualizarEpisodiosFaltantes();
    }

    /// <summary>
    /// Detecta huecos reales (episodios sin archivo local por debajo del episodio descargado
    /// más alto) — no cuenta episodios "todavía no descargados" al final de la lista, que es
    /// el estado normal de un anime en emisión, no un hueco silencioso que haga saltarte una
    /// trama por accidente (el caso que describe este banner).
    /// </summary>
    private void ActualizarEpisodiosFaltantes()
    {
        int maxDescargado = _todosLosEpisodios.Where(e => e.Descargado).Select(e => e.NumeroEpisodio).DefaultIfEmpty(0).Max();

        _numerosEpisodiosFaltantes = _todosLosEpisodios
            .Where(e => !e.Descargado && e.NumeroEpisodio < maxDescargado)
            .Select(e => e.NumeroEpisodio)
            .OrderBy(n => n)
            .ToList();

        HayEpisodiosFaltantes = _numerosEpisodiosFaltantes.Count > 0;
        EpisodiosFaltantesTexto = HayEpisodiosFaltantes
            ? string.Format(LocalizationService.T("Det_FaltantesBannerFormato"), _numerosEpisodiosFaltantes.Count, FormatearListaEpisodios(_numerosEpisodiosFaltantes))
            : string.Empty;
    }

    private static string FormatearListaEpisodios(List<int> numeros)
    {
        const int limiteVisible = 5;
        var etiquetas = numeros.Take(limiteVisible)
            .Select(n => string.Format(LocalizationService.T("Det_FaltanteEpisodioEtiqueta"), n))
            .ToList();

        string texto = etiquetas.Count == 1
            ? etiquetas[0]
            : string.Join(", ", etiquetas.Take(etiquetas.Count - 1)) + $" {LocalizationService.T("Det_Y")} " + etiquetas[^1];

        int resto = numeros.Count - limiteVisible;
        if (resto > 0) texto += $" {string.Format(LocalizationService.T("Det_FaltantesResto"), resto)}";
        return texto;
    }

    [RelayCommand]
    private async Task DescargarFaltantesAsync()
    {
        var faltantes = _todosLosEpisodios.Where(e => !e.Descargado && _numerosEpisodiosFaltantes.Contains(e.NumeroEpisodio)).ToList();
        foreach (var episodio in faltantes)
        {
            await DescargarEpisodioAsync(episodio);
        }
    }

    /// <summary>
    /// "Doctor de integridad": verifica con ffprobe (sin decodificar el archivo entero) que
    /// cada episodio descargado abra bien — detecta descargas que quedaron truncadas/corruptas
    /// (el bug real que motivó esto: DownloadService trataba un corte de conexión a mitad de
    /// descarga como éxito) antes de que el usuario se siente a verlas y se encuentre el error.
    /// </summary>
    [RelayCommand]
    private async Task VerificarIntegridadAsync()
    {
        if (_videoIntegrityService == null || VerificandoIntegridad) return;

        var descargados = _todosLosEpisodios.Where(e => e.Descargado && !string.IsNullOrWhiteSpace(e.RutaCompleta)).ToList();
        if (descargados.Count == 0) return;

        VerificandoIntegridad = true;
        int corruptos = 0;
        try
        {
            foreach (var episodio in descargados)
            {
                episodio.EstaCorrupto = false;
                var resultado = await _videoIntegrityService.VerificarArchivoAsync(episodio.RutaCompleta);
                if (resultado == ResultadoIntegridad.Corrupto)
                {
                    episodio.EstaCorrupto = true;
                    corruptos++;
                }
            }

            await _dialogService.MostrarDialogoAsync(
                LocalizationService.T("Det_IntegridadTitulo"),
                corruptos > 0
                    ? string.Format(LocalizationService.T("Det_IntegridadConCorruptosFormato"), corruptos, descargados.Count)
                    : string.Format(LocalizationService.T("Det_IntegridadSinCorruptosFormato"), descargados.Count),
                false,
                corruptos > 0 ? "AlertOctagonOutline" : "CheckCircleOutline",
                corruptos > 0 ? "#F87171" : "#4CAF50");
        }
        finally
        {
            VerificandoIntegridad = false;
        }
    }

    /// <summary>
    /// Abre la CARPETA DEL ANIME en el Explorador (acción única de nivel anime,
    /// no por episodio — evita saturar la lista de episodios).
    /// </summary>
    [RelayCommand]
    private void AbrirCarpetaAnime()
    {
        if (AnimeSeleccionado == null || string.IsNullOrWhiteSpace(AnimeSeleccionado.RutaCarpeta))
        {
            _ = _dialogService.MostrarDialogoAsync(LocalizationService.T("Det_CarpetaNoEncontradaTitulo"), LocalizationService.T("Det_SinCarpetaLocalMsj"), false, "AlertCircleOutline", "#F59E0B");
            return;
        }
        if (!Directory.Exists(AnimeSeleccionado.RutaCarpeta))
        {
            _ = _dialogService.MostrarDialogoAsync(LocalizationService.T("Det_CarpetaNoEncontradaTitulo"), LocalizationService.T("Det_CarpetaYaNoExisteMsj"), false, "AlertCircleOutline", "#F59E0B");
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{AnimeSeleccionado.RutaCarpeta}\"",
                UseShellExecute = false
            });
        }
        catch (Exception ex)
        {
            AppLogger.Debug("DetalleViewModel", $"Error abriendo carpeta del anime: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task EliminarEpisodio(EpisodioItem episodio)
    {
        if (episodio == null || !episodio.Descargado || string.IsNullOrWhiteSpace(episodio.RutaCompleta)) return;

        bool confirmar = await _dialogService.MostrarDialogoAsync(
            LocalizationService.T("Det_EliminarEpisodioTitulo"),
            string.Format(LocalizationService.T("Det_EliminarEpisodioConfirmacionMsj"), episodio.NumeroEpisodio),
            true, "DeleteOutline", "#EF4444");
        if (!confirmar) return;

        string rutaArchivo = episodio.RutaCompleta;
        string rutaMiniatura = PythonEpisodeEnricher.ObtenerRutaMiniaturaEsperada(rutaArchivo);

        // 1. Borrar el archivo de video
        try { if (File.Exists(rutaArchivo)) File.Delete(rutaArchivo); }
        catch (Exception ex) { AppLogger.Debug("DetalleViewModel", $"No se pudo borrar archivo del episodio: {ex.Message}"); }

        // 2. Borrar su miniatura
        try { if (File.Exists(rutaMiniatura)) File.Delete(rutaMiniatura); } catch { }

        // 3. Conservar el registro en la base de datos: el historial es un registro
        //    permanente — borrar el archivo NO debe borrar que se vio el episodio.
        try
        {
            await _databaseService.ConservarRegistroTrasEliminarArchivoAsync(AnimeSeleccionado?.AniListId ?? 0, episodio.NumeroEpisodio);
        }
        catch (Exception ex)
        {
            AppLogger.Debug("DetalleViewModel", $"No se pudo conservar el registro del episodio: {ex.Message}");
        }

        // 4. Reiniciar en la UI solo lo relativo al archivo (visto/progreso/fecha se conservan)
        episodio.Descargado = false;
        episodio.RutaCompleta = string.Empty;
        episodio.RutaMiniatura = null;
        episodio.TamanoArchivoFormateado = string.Empty;
        episodio.Resolucion = string.Empty;
        episodio.CodecVideo = string.Empty;
        episodio.Fps = string.Empty;
        episodio.Es10Bit = false;

        AplicarFiltrosYOrdenamiento();
        _ = CalcularEspacioEnDiscoAsync();

        await _dialogService.MostrarDialogoAsync(LocalizationService.T("Det_EpisodioEliminadoTitulo"),
            string.Format(LocalizationService.T("Det_EpisodioEliminadoMsj"), episodio.NumeroEpisodio),
            false, "CheckCircleOutline", "#4CAF50");
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
            LocalizationService.T("Bib_EliminarTitulo"),
            string.Format(LocalizationService.T("Bib_EliminarMsj"), AnimeSeleccionado.Titulo),
            true, "HeartBrokenOutline", "#EF4444");

        if (confirmacion)
        {
            // Opción extra: borrar también los archivos del disco
            bool borrarArchivos = await _dialogService.MostrarDialogoAsync(
                LocalizationService.T("Bib_BorrarArchivosTitulo"),
                string.Format(LocalizationService.T("Bib_BorrarArchivosMsj"), string.IsNullOrWhiteSpace(AnimeSeleccionado.RutaCarpeta) ? LocalizationService.T("Bib_SinCarpetaLocal") : AnimeSeleccionado.RutaCarpeta),
                true, "FolderOutline", "#EF4444");

            string? carpeta = AnimeSeleccionado.RutaCarpeta;

            await _databaseService.EliminarAnimeAsync(AnimeSeleccionado);

            if (borrarArchivos && !string.IsNullOrWhiteSpace(carpeta) && Directory.Exists(carpeta))
            {
                try
                {
                    Directory.Delete(carpeta, recursive: true);
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("DetalleViewModel", $"No se pudo borrar la carpeta del anime: {ex.Message}");
                }
            }

            VolverAGaleria();
        }
    }
    
    [RelayCommand]
    private async Task AnalizarOpenings()
    {
        if (AnimeSeleccionado == null || _pluginService == null) return;
        var descargados = _todosLosEpisodios.Where(e => e.Descargado && File.Exists(e.RutaCompleta)).ToList();
        if (descargados.Count < 2)
        {
            await _dialogService.MostrarDialogoAsync("Info", LocalizationService.T("Det_MinEpisodiosOPMsj"), false, "InformationOutline", "#60A5FA");
            return;
        }

        await _dialogService.MostrarDialogoAsync(LocalizationService.T("Det_Analizando"), LocalizationService.T("Det_AnalizandoOpMsj"), false, "InformationOutline", "#60A5FA");
        
        var pluginRes = await _pluginService.EjecutarPluginAsync<object, AnimeLocalTracker.Services.SkipTimesCoordinator.AudioSkipResult>(
            "audio_skip_plugin.py", 
            "detect_opening", 
            new { video_paths = descargados.Take(2).Select(e => e.RutaCompleta).ToArray() }
        );
        
        if (pluginRes != null && pluginRes.Found)
        {
            await _dialogService.MostrarDialogoAsync(LocalizationService.T("Det_OpEncontradoTitulo"), string.Format(LocalizationService.T("Det_OpEncontradoMsj"), pluginRes.IntroEstimatedStart, pluginRes.IntroEstimatedEnd), false, "CheckCircle", "#10B981");
        }
        else
        {
            await _dialogService.MostrarDialogoAsync(LocalizationService.T("Det_OpNoEncontradoTitulo"), LocalizationService.T("Det_OpNoEncontradoMsj"), false, "CloseCircle", "#EF4444");
        }
    }
    
    [RelayCommand]
    private async Task ReproducirEpisodio(EpisodioItem episodio)
    {
        if (episodio == null || AnimeSeleccionado == null) return;
        
        if (!episodio.Descargado || !File.Exists(episodio.RutaCompleta))
        {
            await _dialogService.MostrarDialogoAsync(LocalizationService.T("Det_EpisodioNoEncontradoTitulo"), string.Format(LocalizationService.T("Det_EpisodioNoEncontradoMsj"), episodio.NumeroEpisodio), false, "InformationOutline", "#FFC107");

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
                EpisodiosDisponibles: episodiosDisponibles,
                RutaPortada: AnimeSeleccionado.PortadaVisible
            ));
        }
        catch (System.Exception ex)
        {
            await _dialogService.MostrarDialogoAsync(LocalizationService.T("Dlg_ErrorTitulo"), string.Format(LocalizationService.T("Det_ErrorIniciarReproductorMsj"), ex.Message), false, "AlertCircleOutline", "#E53935");
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

    /// <summary>Favorito individual por anime (no un estado/categoría): persiste en AnimeItem.EsFavorito.</summary>
    [RelayCommand]
    private async Task AlternarFavoritoAnimeAsync()
    {
        if (AnimeSeleccionado == null) return;

        AnimeSeleccionado.EsFavorito = !AnimeSeleccionado.EsFavorito;
        EsFavoritoAnime = AnimeSeleccionado.EsFavorito;
        await _databaseService.ActualizarAnimeAsync(AnimeSeleccionado);
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

        // FUN-010: reanudar el episodio dejado a medias MÁS RECIENTE (antes se elegía el de
        // menor número con progreso, ignorando UltimaReproduccion).
        var epEnCurso = _todosLosEpisodios
            .Where(e => e.TieneProgresoGuardado)
            .OrderByDescending(e => e.UltimaReproduccion)
            .FirstOrDefault();
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
            ep.ProgresoSegundos = 0;
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
            ep.ProgresoSegundos = 0;
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

    /// <summary>
    /// Alterna el estado visto/no visto de un único episodio desde el menú contextual.
    /// </summary>
    [RelayCommand]
    private async Task AlternarVistoEpisodioAsync(EpisodioItem? episodio)
    {
        if (episodio == null || AnimeSeleccionado == null) return;

        episodio.Visto = !episodio.Visto;
        episodio.ProgresoSegundos = 0; // consistente con el reset que hace la BD al marcar manualmente

        var registro = new RegistroEpisodio
        {
            AniListId = AnimeSeleccionado.AniListId,
            NumeroEpisodio = episodio.NumeroEpisodio,
            VistoLocal = episodio.Visto,
            FavoritoLocal = episodio.Favorito,
            RutaArchivo = episodio.RutaCompleta ?? string.Empty
        };

        await _databaseService.GuardarRegistroEpisodioAsync(registro);
        AnimeSeleccionado.EpisodiosVistos = _todosLosEpisodios.Count(e => e.Visto);
        await _databaseService.ActualizarAnimeAsync(AnimeSeleccionado);
        WeakReferenceMessenger.Default.Send(new EpisodioActualizadoMensaje(AnimeSeleccionado.AniListId, episodio.NumeroEpisodio, episodio.Visto, 0, 0));
        AplicarFiltrosYOrdenamiento();
    }

    /// <summary>
    /// Marca como vistos el episodio seleccionado y todos los anteriores (&lt;= N).
    /// </summary>
    [RelayCommand]
    private async Task MarcarAnterioresVistosAsync(EpisodioItem? episodio)
    {
        if (episodio == null || AnimeSeleccionado == null) return;

        var aMarcar = _todosLosEpisodios
            .Where(e => e.NumeroEpisodio <= episodio.NumeroEpisodio && !e.Visto)
            .ToList();

        if (aMarcar.Count == 0) return;

        var listaRegistros = new List<RegistroEpisodio>(aMarcar.Count);
        foreach (var ep in aMarcar)
        {
            ep.Visto = true;
            ep.ProgresoSegundos = 0;
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

    /// <summary>
    /// Marca toda la temporada / serie completa del anime como vista.
    /// </summary>
    [RelayCommand]
    private async Task MarcarTemporadaCompletaAsync()
    {
        if (AnimeSeleccionado == null) return;

        var aMarcar = _todosLosEpisodios
            .Where(e => !e.Visto)
            .ToList();

        if (aMarcar.Count == 0) return;

        var listaRegistros = new List<RegistroEpisodio>(aMarcar.Count);
        foreach (var ep in aMarcar)
        {
            ep.Visto = true;
            ep.ProgresoSegundos = 0;
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
            // El título nativo (japonés) es clave para el catálogo del sitio (aka ja-jp)
            if (!string.IsNullOrWhiteSpace(datosFrescos.Title.Native)) titulosAlt.Add(datosFrescos.Title.Native!);
            if (datosFrescos.Synonyms != null) titulosAlt.AddRange(datosFrescos.Synonyms.Where(s => !string.IsNullOrWhiteSpace(s)));
            AnimeSeleccionado.NombresAlternativos = string.Join(" | ", titulosAlt.Distinct());

            AnimeSeleccionado.TotalEpisodios = episodiosEmitidos;
            AnimeSeleccionado.Estado = datosFrescos.Status ?? "UNKNOWN";
            AnimeSeleccionado.Generos = datosFrescos.Genres != null ? string.Join(", ", datosFrescos.Genres) : "";
            AnimeSeleccionado.UrlPortada = datosFrescos.CoverImage?.ExtraLarge ?? datosFrescos.CoverImage?.Large ?? AnimeSeleccionado.UrlPortada;
            
            await _databaseService.ActualizarAnimeAsync(AnimeSeleccionado);
            
            _forzarProximaEmision = true; // el botón "Actualizar" también revalida la hora del próximo episodio
            await InicializarAsync(AnimeSeleccionado);
            
            await _dialogService.MostrarDialogoAsync(LocalizationService.T("Det_ActualizadoTitulo"), string.Format(LocalizationService.T("Det_ActualizadoMsj"), episodiosEmitidos), false, "CheckCircleOutline", "#4CAF50");
        }
        else
        {
            await _dialogService.MostrarDialogoAsync(LocalizationService.T("Dlg_ErrorTitulo"), LocalizationService.T("Det_ErrorConectarAniListMsj"), false, "AlertCircleOutline", "#E53935");
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
        EditEstadoVisual = LocalizationService.T("Estado_Viendo");
        NotificarDerivadosEditor();
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

            NotificarDerivadosEditor();
        }
    }

    [RelayCommand]
    private async Task GuardarEditorSeguimientoAsync()
    {
        if (AnimeSeleccionado == null) return;
        
        var token = _authService.ObtenerTokenGuardado();
        if (string.IsNullOrEmpty(token))
        {
            await _dialogService.MostrarDialogoAsync(LocalizationService.T("Det_ErrorAutenticacionTitulo"), LocalizationService.T("Det_DebesConectarAniListMsj"), false, "AlertCircleOutline", "#E53935");
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
            await _dialogService.MostrarDialogoAsync(LocalizationService.T("Det_NubeSincronizadaTitulo"), LocalizationService.T("Det_NubeSincronizadaMsj"), false, "CloudCheck", "#4CAF50");
        }
        else
        {
            await _dialogService.MostrarDialogoAsync(LocalizationService.T("Det_ErrorSincronizacionTitulo"), LocalizationService.T("Det_ErrorSincronizacionMsj"), false, "AlertCircleOutline", "#E53935");
        }
    }
    
    [RelayCommand]
    private void CerrarEditorSeguimiento()
    {
        MostrandoEditorSeguimiento = false;
    }
    
    private static string ConvertirEstadoAIngles(string estadoVisual)
    {
        if (estadoVisual == LocalizationService.T("Estado_Viendo")) return "CURRENT";
        if (estadoVisual == LocalizationService.T("Estado_Finalizado")) return "COMPLETED";
        if (estadoVisual == LocalizationService.T("Estado_EnPausa")) return "PAUSED";
        if (estadoVisual == LocalizationService.T("Estado_Abandonado")) return "DROPPED";
        if (estadoVisual == LocalizationService.T("Estado_Planeando")) return "PLANNING";
        return "CURRENT";
    }

    private static string ConvertirEstadoAEspanol(string estadoIngles) => estadoIngles switch
    {
        "CURRENT" => LocalizationService.T("Estado_Viendo"),
        "COMPLETED" => LocalizationService.T("Estado_Finalizado"),
        "PAUSED" => LocalizationService.T("Estado_EnPausa"),
        "DROPPED" => LocalizationService.T("Estado_Abandonado"),
        "PLANNING" => LocalizationService.T("Estado_Planeando"),
        _ => LocalizationService.T("Estado_Viendo")
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

    /// <summary>
    /// Fase 2d: busca todos los candidatos de torrent válidos para el episodio (en vez
    /// de dejar que la app elija sola el de más semillas), muestra el selector, y si el
    /// usuario elige uno, lo descarga directo por torrent — sin pasar por el resolver
    /// HTTP ni por la búsqueda automática de Nyaa.
    /// </summary>
    [RelayCommand]
    private async Task ElegirTorrentManualAsync(EpisodioItem episodio)
    {
        if (episodio == null || AnimeSeleccionado == null) return;
        if (episodio.IsDownloading) return;
        if (_nyaaSourceService == null || _selectorTorrentService == null) return;

        var titulosCandidatos = new List<string> { AnimeSeleccionado.Titulo };
        if (!string.IsNullOrWhiteSpace(AnimeSeleccionado.NombresAlternativos))
        {
            titulosCandidatos.AddRange(AnimeSeleccionado.NombresAlternativos.Split([" | ", ";"], StringSplitOptions.RemoveEmptyEntries));
        }

        var configuracion = _settingsService?.ObtenerConfiguracion();
        var candidatos = await _nyaaSourceService.BuscarCandidatosAsync(
            titulosCandidatos, episodio.NumeroEpisodio,
            configuracion?.GrupoFansubPreferidoTorrent, configuracion?.ResolucionPreferidaTorrent);

        if (candidatos.Count == 0)
        {
            _dialogService.MostrarToast(
                LocalizationService.T("Sel_Titulo"),
                LocalizationService.T("Sel_SinCandidatos"),
                "AlertCircleOutline", "#F59E0B");
            return;
        }

        string tituloEpisodio = $"{AnimeSeleccionado.Titulo} — {episodio.TituloVisual}";
        var elegido = await _selectorTorrentService.MostrarSelectorAsync(tituloEpisodio, candidatos);
        if (elegido == null) return; // el usuario canceló

        episodio.IsDownloading = true;
        episodio.DownloadProgress = 0;

        await _downloadService.IniciarDescargaTorrentManualAsync(
            AnimeSeleccionado.AniListId,
            AnimeSeleccionado.Titulo,
            AnimeSeleccionado.RutaCarpeta,
            episodio.NumeroEpisodio,
            elegido.Value,
            titulosCandidatos);
    }
}
