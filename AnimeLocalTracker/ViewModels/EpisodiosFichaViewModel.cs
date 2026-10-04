using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Native;
using AnimeLocalTracker.Services.Python;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AnimeLocalTracker.ViewModels;

/// <summary>
/// La lista de episodios de la ficha y todo lo que se hace con ellos: cargarla (base de datos + carpeta), filtrarla y
/// ordenarla, marcar vistos, descargar, reproducir, borrar, el aviso de huecos y las herramientas sobre los archivos
/// (integridad, duplicados). También mantiene las filas al día con lo que avisan el reproductor y las descargas.
/// Es una pieza de la ficha (<see cref="DetalleViewModel.Episodios"/>): la ficha le dice de qué anime se trata y cuándo cargar.
/// </summary>
public sealed partial class EpisodiosFichaViewModel : ObservableObject,
    IRecipient<EpisodioActualizadoMensaje>,
    IRecipient<DescargaProgresoMensaje>,
    IRecipient<IdiomaCambiadoMensaje>,
    IDisposable
{
    private readonly IDatabaseService _databaseService;
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

    private Action<AppSettings>? _alCambiarConfiguracion;
    private bool _liberado;

    /// <summary>Se borró o se terminó de descargar un archivo: la ficha recalcula el espacio en disco.</summary>
    public event Action? ArchivosCambiados;

    /// <summary>Se va a abrir el reproductor con un episodio (la ficha corta su música: no debe sonar bajo el video).</summary>
    public event Action? ReproduccionSolicitada;

    public EpisodiosFichaViewModel(
        IDatabaseService databaseService,
        IFileScannerService fileScannerService,
        IDialogService dialogService,
        IDownloadService downloadService,
        PythonEpisodeEnricher? enricher = null,
        IPluginService? pluginService = null,
        IVideoIntegrityService? videoIntegrityService = null,
        INyaaSourceService? nyaaSourceService = null,
        ISelectorTorrentService? selectorTorrentService = null,
        ISettingsService? settingsService = null)
    {
        _databaseService = databaseService;
        _fileScannerService = fileScannerService;
        _dialogService = dialogService;
        _downloadService = downloadService;
        _enricher = enricher;
        _pluginService = pluginService;
        _videoIntegrityService = videoIntegrityService;
        _nyaaSourceService = nyaaSourceService;
        _selectorTorrentService = selectorTorrentService;
        _settingsService = settingsService;

        WeakReferenceMessenger.Default.Register<EpisodioActualizadoMensaje>(this);
        WeakReferenceMessenger.Default.Register<DescargaProgresoMensaje>(this);
        WeakReferenceMessenger.Default.Register<IdiomaCambiadoMensaje>(this);

        // Fase 2d: el botón "elegir torrent manualmente" solo tiene sentido si la
        // búsqueda por torrent está activa — se mantiene sincronizado con Configuración.
        BusquedaTorrentHabilitada = _settingsService?.ObtenerConfiguracion()?.BusquedaTorrentHabilitada ?? false;
        if (_settingsService != null)
        {
            // Se guarda el manejador para darlo de baja en Dispose: el servicio de ajustes vive toda la app y, sin la baja,
            // retenía cada ficha abierta.
            _alCambiarConfiguracion = config =>
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher != null && !dispatcher.HasShutdownStarted)
                {
                    dispatcher.Invoke(() => BusquedaTorrentHabilitada = config?.BusquedaTorrentHabilitada ?? false);
                }
            };
            _settingsService.ConfiguracionModificada += _alCambiarConfiguracion;
        }
    }

    /// <summary>Al descartar la ficha: deja de escuchar avisos y la configuración (el servicio de ajustes vive toda la app).</summary>
    public void Dispose()
    {
        if (_liberado) return;
        _liberado = true;

        WeakReferenceMessenger.Default.UnregisterAll(this);
        if (_settingsService != null && _alCambiarConfiguracion != null) _settingsService.ConfiguracionModificada -= _alCambiarConfiguracion;
        _enrichmentCoordinator.Dispose();
    }

    /// <summary>El anime de la ficha. Lo pone la ficha.</summary>
    [ObservableProperty] private AnimeItem? _anime;

    private readonly List<EpisodioItem> _todosLosEpisodios = new();

    /// <summary>Todos los episodios del anime (1..N), sin filtrar. Lo que se ve en pantalla es <see cref="EpisodiosDelAnime"/>.</summary>
    public IReadOnlyList<EpisodioItem> Todos => _todosLosEpisodios;

    public ColeccionReemplazable<EpisodioItem> EpisodiosDelAnime { get; } = [];

    [ObservableProperty]
    private bool _ordenAscendente = false;

    /// <summary>Clave fija del filtro activo (<c>EpisodiosOrganizador.Filtro*</c>), no el texto que se ve en el desplegable.</summary>
    [ObservableProperty]
    private string _filtroEpisodios = Core.EpisodiosOrganizador.FiltroTodos;

    public IReadOnlyList<OpcionFiltroEpisodios> OpcionesFiltro { get; } = [
        new(Core.EpisodiosOrganizador.FiltroTodos, "Filtro_Todos"),
        new(Core.EpisodiosOrganizador.FiltroDescargados, "Filtro_Descargados"),
        new(Core.EpisodiosOrganizador.FiltroVistos, "Filtro_Vistos"),
        new(Core.EpisodiosOrganizador.FiltroNoVistos, "Filtro_NoVistos"),
        new(Core.EpisodiosOrganizador.FiltroFavoritos, "Filtro_Favoritos")
    ];

    /// <summary>La opción elegida en el desplegable (va a la par de <see cref="FiltroEpisodios"/>).</summary>
    public OpcionFiltroEpisodios FiltroSeleccionado
    {
        get => OpcionesFiltro.FirstOrDefault(o => o.Clave == FiltroEpisodios) ?? OpcionesFiltro[0];
        set
        {
            if (value != null) FiltroEpisodios = value.Clave;
        }
    }

    [ObservableProperty] private string _mensajeSinEpisodios = LocalizationService.T("Det_SinEpisodios");
    [ObservableProperty] private string _subtituloSinEpisodios = LocalizationService.T("Det_SinEpisodiosSub");

    /// <summary>Hay algún episodio a medias: se ofrece el botón "Reanudar".</summary>
    [ObservableProperty] private bool _tieneCapituloEnProgreso = false;

    // === BANNER DE EPISODIOS FALTANTES (huecos en la carpeta local) ===
    [ObservableProperty] private bool _hayEpisodiosFaltantes;
    [ObservableProperty] private string _episodiosFaltantesTexto = string.Empty;
    private List<int> _numerosEpisodiosFaltantes = new();

    // === DOCTOR DE INTEGRIDAD DE VIDEO ===
    [ObservableProperty] private bool _verificandoIntegridad;

    public bool TieneEpisodios => EpisodiosDelAnime.Count > 0;

    /// <summary>Fase 2d: espeja AppSettings.BusquedaTorrentHabilitada — controla si se
    /// muestra el botón "elegir torrent manualmente" junto al de descargar.</summary>
    [ObservableProperty] private bool _busquedaTorrentHabilitada;

    // === ACCIÓN PRINCIPAL: el botón grande de la lista, que siempre ofrece "lo siguiente" ===

    /// <summary>Hay algo que ofrecer: reanudar el episodio a medias, ver el siguiente sin ver o descargarlo.</summary>
    [ObservableProperty] private bool _tieneAccionPrincipal;
    [ObservableProperty] private string _accionPrincipalTexto = string.Empty;
    /// <summary>Icono de Material Design del botón: "Play" o "DownloadOutline".</summary>
    [ObservableProperty] private string _accionPrincipalIcono = "Play";

    private EpisodioItem? _episodioAccionPrincipal;
    private bool _accionPrincipalEsDescargar;

    /// <summary>
    /// Decide qué ofrece el botón principal. Primero, el episodio que se dejó a medias más recientemente (si su archivo sigue
    /// en disco). Si no hay ninguno, el primer episodio sin ver: "Ver" si está descargado, "Descargar" si no. Con todo visto
    /// (o con ese episodio ya descargándose) no se ofrece nada.
    /// </summary>
    private void ActualizarAccionPrincipal()
    {
        EpisodioItem? episodio = _todosLosEpisodios
            .Where(e => e.TieneProgresoGuardado && e.Descargado)
            .OrderByDescending(e => e.UltimaReproduccion)
            .FirstOrDefault();
        string clave = "Det_AccionReanudarFormato";
        bool descargar = false;

        if (episodio == null)
        {
            episodio = _todosLosEpisodios.Where(e => !e.Visto).OrderBy(e => e.NumeroEpisodio).FirstOrDefault();
            if (episodio is { Descargado: false, IsDownloading: true }) episodio = null;
            descargar = episodio is { Descargado: false };
            clave = descargar ? "Det_AccionDescargarFormato" : "Det_AccionVerFormato";
        }

        _episodioAccionPrincipal = episodio;
        _accionPrincipalEsDescargar = descargar;
        TieneAccionPrincipal = episodio != null;
        AccionPrincipalTexto = episodio == null ? string.Empty : string.Format(LocalizationService.T(clave), episodio.NumeroEpisodio);
        AccionPrincipalIcono = descargar ? "DownloadOutline" : "Play";
    }

    [RelayCommand]
    private Task EjecutarAccionPrincipalAsync()
    {
        var episodio = _episodioAccionPrincipal;
        if (episodio == null) return Task.CompletedTask;
        return _accionPrincipalEsDescargar ? DescargarEpisodioAsync(episodio) : ReproducirEpisodio(episodio);
    }

    // === IR A UN EPISODIO (series largas) ===

    /// <summary>A partir de cuántos episodios se ofrece la casilla "Ir al ep." (con pocos, la lista ya se ve casi entera).</summary>
    internal const int MinimoEpisodiosParaIr = 30;

    public bool MostrarIrAEpisodio => _todosLosEpisodios.Count > MinimoEpisodiosParaIr;

    /// <summary>
    /// El episodio con ese número dentro de la lista que se ve (para que la vista se desplace hasta él). Null si el texto no es
    /// un número; si el episodio no está en la lista actual (por el filtro, o porque no existe) lo avisa y devuelve null.
    /// </summary>
    internal EpisodioItem? BuscarParaIr(string? texto)
    {
        if (!int.TryParse(texto?.Trim(), out int numero) || numero <= 0) return null;

        var episodio = EpisodiosDelAnime.FirstOrDefault(e => e.NumeroEpisodio == numero);
        if (episodio == null)
        {
            _dialogService.MostrarToast(LocalizationService.T("Det_IrAEpisodio"),
                string.Format(LocalizationService.T("Det_IrAEpisodioNoEstaFormato"), numero), "InformationOutline", "#60A5FA");
        }
        return episodio;
    }

    /// <summary>El episodio más avanzado que se ha visto (para tapar los temas de música que serían spoiler).</summary>
    internal int EpisodioMasAltoVisto()
    {
        try { return _todosLosEpisodios.Where(e => e.Visto).Select(e => e.NumeroEpisodio).DefaultIfEmpty(0).Max(); }
        catch (InvalidOperationException) { return 0; } // la lista cambió mientras se leía: basta con el recuento
    }

    /// <summary>Vuelve a aplicar filtro y orden (p. ej. después de que la ficha borre archivos al liberar espacio).</summary>
    internal void Refrescar() => AplicarFiltrosYOrdenamiento();

    /// <summary>
    /// Añade filas (sin archivo) hasta ese número de episodio: lo usa la ficha cuando la cuenta atrás detecta que ya salió un
    /// episodio que la lista aún no muestra. Las filas son 1..N contiguas.
    /// </summary>
    internal void AnadirFilasHasta(int numeroEpisodio)
    {
        if (numeroEpisodio <= _todosLosEpisodios.Count) return;

        for (int numero = _todosLosEpisodios.Count + 1; numero <= numeroEpisodio; numero++)
        {
            _todosLosEpisodios.Add(new EpisodioItem { NumeroEpisodio = numero });
        }
        AplicarFiltrosYOrdenamiento();
    }

    /// <summary>Lo leído de la base de datos y de la carpeta para un anime, listo para ponerlo en la lista.</summary>
    internal sealed record Lectura(List<EpisodioItem> Filas, int EnDisco, int Vistos, long MsDatos);

    /// <summary>
    /// Lee los episodios del anime (base de datos y carpeta, sin internet), entero fuera del hilo de la interfaz y de una sola
    /// vez. Mientras tanto la interfaz está construyendo la vista de la ficha: antes cada paso (registros, carpeta, filas) volvía
    /// a ese hilo ocupado para poder pedir el siguiente, y los datos esperaban a la vista. No cambia la lista: eso lo hace
    /// <see cref="Mostrar"/>, que la ficha llama en cuanto vuelve al hilo de la interfaz (un solo salto de hilo, no dos).
    /// </summary>
    internal async Task<Lectura> LeerAsync(AnimeItem anime, Stopwatch reloj)
    {
        _todosLosEpisodios.Clear();
        EpisodiosDelAnime.Clear();

        int aniListId = anime.AniListId;
        int totalOficial = anime.TotalEpisodios;
        string? rutaCarpeta = anime.RutaCarpeta;
        return await Task.Run(async () =>
        {
            var registrosGuardados = await _databaseService.ObtenerRegistrosPorAnimeAsync(aniListId).ConfigureAwait(false);
            int episodiosVistos = registrosGuardados.Count(r => r.VistoLocal);

            // Escaneamos la carpeta local si existe
            List<EpisodioItem> encontrados = new();
            if (!string.IsNullOrEmpty(rutaCarpeta))
            {
                encontrados = await _fileScannerService.EscanearEpisodiosAsync(rutaCarpeta).ConfigureAwait(false);
            }

            // El total oficial de AniList manda cuando se conoce, con un pequeño margen para no ocultar un preestreno o una
            // filtración real (ver CalcularMaxEpisodio); un archivo local con un número muy por encima de eso
            // ("Episodio 3000.mp4") no debe inflar la lista sin sentido.
            int maxEpisodio = Core.EpisodiosOrganizador.CalcularMaxEpisodio(totalOficial, encontrados, episodiosVistos);

            // Límite de seguridad para prevenir asignaciones anómalas de memoria (máx 3000)
            const int LimiteSeguridadEpisodios = 3000;
            int episodiosACargar = Math.Min(maxEpisodio, LimiteSeguridadEpisodios);

            // La lista se construye sin esperar a generar miniaturas: las que falten se generan en segundo plano y aparecen
            // progresivamente.
            var filas = ConstruirFilasDeEpisodios(aniListId, episodiosACargar, encontrados, registrosGuardados);
            return new Lectura(filas, encontrados.Count, episodiosVistos, reloj.ElapsedMilliseconds);
        });
    }

    /// <summary>Pone en la lista lo leído por <see cref="LeerAsync"/>. En el hilo de la interfaz.</summary>
    internal void Mostrar(AnimeItem anime, Lectura lectura)
    {
        anime.EpisodiosVistos = lectura.Vistos;
        _todosLosEpisodios.AddRange(lectura.Filas);
        AplicarFiltrosYOrdenamiento();
    }

    /// <summary>Cambio de idioma con la ficha abierta: el desplegable de filtro y el aviso de huecos se retraducen.</summary>
    public void Receive(IdiomaCambiadoMensaje message) => Core.HiloUi.Ejecutar(RefrescarTextosTraducidos);

    private void RefrescarTextosTraducidos()
    {
        foreach (var opcion in OpcionesFiltro) opcion.RefrescarTexto();
        ActualizarTextoDeshacer();
        ActualizarEpisodiosFaltantes();
        ActualizarAccionPrincipal();
    }


    public void Receive(EpisodioActualizadoMensaje message)
    {
        if (Anime == null || Anime.AniListId != message.AnimeId) return;

        var episodio = _todosLosEpisodios.FirstOrDefault(e => e.NumeroEpisodio == message.NumeroEpisodio);
        if (episodio != null)
        {
            // Ejecutar en el hilo principal de la UI sin bloquear al emisor
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.HasShutdownStarted)
            {
                _ = dispatcher.InvokeAsync(() => AplicarEpisodioActualizado(episodio, message));
            }
            else
            {
                AplicarEpisodioActualizado(episodio, message);
            }
        }
    }

    /// <summary>
    /// El reproductor avisa del progreso cada 3 s mientras se ve un episodio. Antes cada aviso rehacía la lista entera de la
    /// ficha (que sigue abierta detrás del video): en One Piece, 1180 filas cada 3 s en el hilo de la interfaz, y la lista volvía
    /// arriba. La fila ya se actualiza sola (es observable); la lista solo se rehace si cambió "visto" y el filtro depende de eso.
    /// </summary>
    internal void AplicarEpisodioActualizado(EpisodioItem episodio, EpisodioActualizadoMensaje message)
    {
        bool cambioVisto = episodio.Visto != message.VistoLocal;
        episodio.Visto = message.VistoLocal;
        episodio.ProgresoSegundos = message.ProgresoSegundos;
        if (message.TotalSegundos > 0)
        {
            episodio.TotalSegundos = message.TotalSegundos;
        }

        if (cambioVisto)
        {
            if (Anime != null) Anime.EpisodiosVistos = _todosLosEpisodios.Count(e => e.Visto);
            if (FiltroEpisodios is Core.EpisodiosOrganizador.FiltroVistos or Core.EpisodiosOrganizador.FiltroNoVistos) AplicarFiltrosYOrdenamiento();
            else ActualizarEpisodiosFaltantes(); // un episodio recién visto deja de contar como hueco
        }

        TieneCapituloEnProgreso = _todosLosEpisodios.Any(e => e.TieneProgresoGuardado);
        ActualizarAccionPrincipal();
    }

    public void Receive(DescargaProgresoMensaje message)
    {
        if (Anime == null || Anime.AniListId != message.AniListId) return;

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
                ActualizarAccionPrincipal();

                if (message.IsCompleted)
                {
                    episodio.Descargado = true;
                    episodio.RutaCompleta = message.RutaArchivo;
                    episodio.CalcularTamanoArchivo();
                    ArchivosCambiados?.Invoke();

                    // Miniatura y datos técnicos del archivo nuevo, sin salir de la pestaña.
                    _ = Task.Run(() => PrepararEpisodioRecienDescargadoAsync(episodio, message.AniListId));

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

    /// <summary>
    /// Tras terminar una descarga con la ficha abierta: genera la miniatura, lee los datos técnicos del archivo y lo deja todo
    /// guardado, para que la próxima visita no tenga que repetirlo. No toca lo que ya se sabía del episodio (visto, progreso).
    /// </summary>
    private async Task PrepararEpisodioRecienDescargadoAsync(EpisodioItem episodio, int aniListId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(episodio.RutaCompleta)) return;

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
                    AniListId = aniListId,
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
            catch (Exception ex)
            {
                AppLogger.Debug("EpisodiosFichaViewModel", $"No se pudo guardar el episodio {episodio.NumeroEpisodio} recién descargado: {ex.Message}");
            }

            // PERF-01: refresco coalescido (varios pueden completar a la vez)
            SolicitarRefrescoEpisodios();
        }
        catch (Exception ex)
        {
            AppLogger.Debug("EpisodiosFichaViewModel", $"Error generando miniatura post-descarga: {ex.Message}");
        }
    }


    /// <summary>Una fila por episodio (1..N), con lo que se sabe de cada uno por el disco y por la base de datos. No toca la interfaz.</summary>
    private List<EpisodioItem> ConstruirFilasDeEpisodios(int aniListId, int episodiosACargar, List<EpisodioItem> encontrados, List<RegistroEpisodio> registrosGuardados)
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

            bool estaDescargando = _downloadService.EstaDescargando(aniListId, i, out double prog);

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

            temp.Add(new EpisodioItem
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
            });
        }
        return temp;
    }


    /// <summary>
    /// Enriquecer los episodios locales que aún no tienen metadata técnica o miniatura vía
    /// el bridge Python y guardar el resultado en SQLite para visitas instantáneas futuras.
    /// La implementación vive en EpisodeEnrichmentCoordinator (extraída para reducir el
    /// tamaño de este ViewModel); aquí solo se le pasan las dependencias necesarias.
    /// </summary>
    internal Task EnriquecerEnSegundoPlanoAsync(CancellationToken cancellationToken = default) =>
        Anime == null
            ? Task.CompletedTask
            : _enrichmentCoordinator.EnriquecerEnSegundoPlanoAsync(
                Anime.AniListId, _todosLosEpisodios, _enricher, _databaseService, SolicitarRefrescoEpisodios, cancellationToken);

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
            AppLogger.Debug("EpisodiosFichaViewModel", $"Error analizando duplicados: {ex.Message}");
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

    partial void OnOrdenAscendenteChanged(bool value) => AplicarFiltrosYOrdenamiento();
    partial void OnFiltroEpisodiosChanged(string value)
    {
        OnPropertyChanged(nameof(FiltroSeleccionado));
        AplicarFiltrosYOrdenamiento();
    }

    private void AplicarFiltrosYOrdenamiento()
    {
        if (_todosLosEpisodios == null || _todosLosEpisodios.Count == 0)
        {
            EpisodiosDelAnime.Clear();
            if (Anime != null && (Anime.Estado == "NOT_YET_RELEASED" || Anime.TotalEpisodios == 0))
            {
                MensajeSinEpisodios = LocalizationService.T("Det_SinEpisodiosNoEstrenado");
                SubtituloSinEpisodios = LocalizationService.T("Det_SinEpisodiosNoEstrenadoSub");
            }
            else
            {
                MensajeSinEpisodios = LocalizationService.T("Det_SinEpisodios");
                SubtituloSinEpisodios = LocalizationService.T("Det_SinEpisodiosSub");
            }
            ActualizarAccionPrincipal();
            OnPropertyChanged(nameof(MostrarIrAEpisodio));
            return;
        }

        // ARQ-01: filtrado/orden delegados a la lógica pura extraída (testeable sin UI)
        var query = Core.EpisodiosOrganizador.FiltrarYOrdenar(_todosLosEpisodios, FiltroEpisodios, OrdenAscendente);

        // Si quedan los mismos episodios en el mismo orden (lo normal cuando solo llegó una miniatura, terminó una descarga
        // o se marcó algo con el filtro "Todos"), la lista no se toca: las filas ya se actualizan solas y así no se pierden
        // ni la selección ni el punto por donde ibas. Si cambia, se sustituye con un único aviso en vez de uno por fila.
        var relojLista = Stopwatch.StartNew();
        bool cambio = EpisodiosDelAnime.ReemplazarSiCambia(query);
        RegistrarTiempoDeLista(query.Count, cambio, relojLista);

        if (EpisodiosDelAnime.Count == 0)
        {
            MensajeSinEpisodios = LocalizationService.T("Det_SinEpisodiosFiltro");
            SubtituloSinEpisodios = string.Format(LocalizationService.T("Det_SinEpisodiosFiltroSub"), FiltroSeleccionado.Texto);
        }

        TieneCapituloEnProgreso = _todosLosEpisodios != null && _todosLosEpisodios.Any(e => e.TieneProgresoGuardado);

        ActualizarEpisodiosFaltantes();
        ActualizarAccionPrincipal();
        OnPropertyChanged(nameof(MostrarIrAEpisodio));
    }

    /// <summary>Solo en listas largas (las cortas no dicen nada) y solo con el registro detallado activado.</summary>
    private static void RegistrarTiempoDeLista(int filas, bool cambio, Stopwatch reloj)
    {
        if (filas < 100) return;
        long msColeccion = reloj.ElapsedMilliseconds;

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted || !cambio)
        {
            AppLogger.Debug("EpisodiosFichaViewModel", $"[Perf] Lista de episodios: {filas} filas, {(cambio ? "rehecha" : "sin cambios")} en {msColeccion} ms.");
            return;
        }

        _ = dispatcher.InvokeAsync(
            () => AppLogger.Debug("EpisodiosFichaViewModel", $"[Perf] Lista de episodios: {filas} filas, rehecha en {msColeccion} ms; pintada a los {reloj.ElapsedMilliseconds} ms."),
            System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    /// <summary>
    /// Detecta huecos reales (episodios sin archivo local y sin ver, por debajo del episodio descargado
    /// más alto): un hueco silencioso que haría saltarte una trama por accidente. Ver
    /// <see cref="Core.EpisodiosOrganizador.CalcularFaltantes"/> para lo que NO cuenta como hueco.
    /// </summary>
    private void ActualizarEpisodiosFaltantes()
    {
        _numerosEpisodiosFaltantes = Core.EpisodiosOrganizador.CalcularFaltantes(_todosLosEpisodios);

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

    /// <summary>A partir de cuántos episodios "Descargar faltantes" pregunta antes de poner la cola en marcha.</summary>
    internal const int UmbralConfirmarDescargaFaltantes = 3;

    [RelayCommand]
    private async Task DescargarFaltantesAsync()
    {
        var numeros = _numerosEpisodiosFaltantes.ToHashSet();
        var faltantes = _todosLosEpisodios.Where(e => !e.Descargado && !e.IsDownloading && numeros.Contains(e.NumeroEpisodio)).ToList();
        if (faltantes.Count == 0) return;

        if (faltantes.Count > UmbralConfirmarDescargaFaltantes)
        {
            bool confirmar = await _dialogService.MostrarDialogoAsync(
                LocalizationService.T("Det_DescargarFaltantes"),
                string.Format(LocalizationService.T("Det_DescargarFaltantesConfirmacionFormato"), faltantes.Count),
                true, "DownloadMultiple", "#2563EB");
            if (!confirmar) return;
        }

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


    /// <summary>Cuántas veces se intenta borrar el video si está en uso (200 ms entre intentos). Ajustable solo en pruebas.</summary>
    internal int IntentosBorradoEpisodio { get; set; } = AnimeLocalTracker.Core.BorradoDeArchivos.IntentosPorDefecto;

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

        // 1. Borrar el archivo de video. Fuera del hilo de la interfaz y con reintentos: recién abierta la ficha, la
        //    extracción de miniaturas lo tiene abierto unos instantes. Si no se pudo, se avisa y el episodio se queda como
        //    estaba (antes se daba por quitado aunque el archivo siguiera en el disco, ocupando espacio sin verse).
        int intentos = IntentosBorradoEpisodio;
        bool borrado = await Task.Run(() =>
        {
            try
            {
                if (File.Exists(rutaArchivo)) AnimeLocalTracker.Core.BorradoDeArchivos.BorrarConReintentos(rutaArchivo, intentos);
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("EpisodiosFichaViewModel", $"No se pudo borrar el archivo del episodio {episodio.NumeroEpisodio}: {ex.Message}");
                return false;
            }
        });
        if (!borrado)
        {
            await _dialogService.MostrarDialogoAsync(LocalizationService.T("Det_EliminarEpisodioErrorTitulo"),
                string.Format(LocalizationService.T("Det_EliminarEpisodioErrorMsj"), episodio.NumeroEpisodio),
                false, "AlertCircleOutline", "#EF4444");
            return;
        }

        // 2. Borrar su miniatura (opcional: la ficha puede tenerla abierta en pantalla)
        try { if (File.Exists(rutaMiniatura)) File.Delete(rutaMiniatura); } catch { }

        // 3. Conservar el registro en la base de datos: el historial es un registro
        //    permanente — borrar el archivo NO debe borrar que se vio el episodio.
        try
        {
            await _databaseService.ConservarRegistroTrasEliminarArchivoAsync(Anime?.AniListId ?? 0, episodio.NumeroEpisodio);
        }
        catch (Exception ex)
        {
            AppLogger.Debug("EpisodiosFichaViewModel", $"No se pudo conservar el registro del episodio: {ex.Message}");
        }

        // 4. Reiniciar en la UI solo lo relativo al archivo (visto/progreso/fecha se conservan)
        episodio.QuitarArchivo();

        AplicarFiltrosYOrdenamiento();
        ArchivosCambiados?.Invoke();

        await _dialogService.MostrarDialogoAsync(LocalizationService.T("Det_EpisodioEliminadoTitulo"),
            string.Format(LocalizationService.T("Det_EpisodioEliminadoMsj"), episodio.NumeroEpisodio),
            false, "CheckCircleOutline", "#4CAF50");
    }


    [RelayCommand]
    private async Task AnalizarOpenings()
    {
        if (Anime == null || _pluginService == null) return;
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
        if (episodio == null || Anime == null) return;
        
        if (!episodio.Descargado || !File.Exists(episodio.RutaCompleta))
        {
            // Solo se avisa. Antes además abría una búsqueda de Nyaa en el navegador, un resto de cuando la app no descargaba:
            // ahora el episodio se baja con su propio botón de la fila.
            _dialogService.MostrarToast(LocalizationService.T("Det_EpisodioNoEncontradoTitulo"), string.Format(LocalizationService.T("Det_EpisodioNoEncontradoMsj"), episodio.NumeroEpisodio), "InformationOutline", "#FFC107");
            return;
        }

        try
        {
            // Enviamos un mensaje a la aplicación principal para que abra nuestra nueva ventana de reproductor
            var episodiosDisponibles = _todosLosEpisodios.Where(e => e.Descargado && File.Exists(e.RutaCompleta)).ToList();

            // El reproductor se dibuja encima de la ficha sin descargarla: la música de la ficha no debe sonar bajo el video.
            ReproduccionSolicitada?.Invoke();

            WeakReferenceMessenger.Default.Send(new NavegarMensaje_Reproductor(
                episodio.RutaCompleta,
                Anime.AniListId,
                Anime.Titulo,
                episodio.NumeroEpisodio,
                EpisodiosDisponibles: episodiosDisponibles,
                RutaPortada: Anime.PortadaVisible
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
        if (episodio == null || Anime == null) return;
        
        episodio.Favorito = !episodio.Favorito;

        // Solo la marca de favorito: guardar un registro entero ponía a cero el progreso del episodio.
        await _databaseService.GuardarFavoritoEpisodioAsync(Anime.AniListId, episodio.NumeroEpisodio, episodio.Favorito, episodio.RutaCompleta);
        
        if (FiltroEpisodios == Core.EpisodiosOrganizador.FiltroFavoritos)
        {
            AplicarFiltrosYOrdenamiento();
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
    private Task MarcarVistosAsync(System.Collections.IList? episodiosSeleccionados) =>
        MarcarSeleccionOVisiblesAsync(episodiosSeleccionados, visto: true);

    [RelayCommand]
    private Task MarcarNoVistosAsync(System.Collections.IList? episodiosSeleccionados) =>
        MarcarSeleccionOVisiblesAsync(episodiosSeleccionados, visto: false);

    /// <summary>
    /// Marca los episodios seleccionados; sin selección, todos los de la lista que se ve. En ese segundo caso se pregunta
    /// antes: un clic de más marcaba (o desmarcaba, perdiendo el punto donde te quedaste) la serie entera sin vuelta atrás.
    /// </summary>
    private Task MarcarSeleccionOVisiblesAsync(System.Collections.IList? episodiosSeleccionados, bool visto)
    {
        bool haySeleccion = episodiosSeleccionados != null && episodiosSeleccionados.Count > 0;
        var episodios = haySeleccion
            ? episodiosSeleccionados!.Cast<EpisodioItem>().ToList()
            : EpisodiosDelAnime.Where(e => e.Visto != visto).ToList();

        return MarcarEpisodiosAsync(episodios, visto, pedirConfirmacion: !haySeleccion);
    }

    // === DESHACER EL ÚLTIMO MARCADO ===

    /// <summary>Tras marcar episodios, durante unos segundos se ofrece volver a dejarlos como estaban.</summary>
    [ObservableProperty] private bool _puedeDeshacerMarcado;

    /// <summary>"12 episodios marcados como vistos."</summary>
    [ObservableProperty] private string _deshacerMarcadoTexto = string.Empty;

    /// <summary>Cuánto tiempo se ofrece "Deshacer". Ajustable solo en pruebas.</summary>
    internal TimeSpan DuracionAvisoDeshacer { get; set; } = TimeSpan.FromSeconds(15);

    private sealed record EstadoPrevio(EpisodioItem Episodio, bool Visto, double ProgresoSegundos);

    private List<EstadoPrevio>? _marcadoAnterior;
    private bool _marcadoAnteriorFueVisto;
    private CancellationTokenSource? _ctsAvisoDeshacer;

    private void OfrecerDeshacer(List<EstadoPrevio> anterior, bool visto)
    {
        _marcadoAnterior = anterior;
        _marcadoAnteriorFueVisto = visto;
        ActualizarTextoDeshacer();
        PuedeDeshacerMarcado = true;

        Core.Cancelacion.Reemplazar(ref _ctsAvisoDeshacer);
        _ = OcultarAvisoDeshacerTrasUnRatoAsync(_ctsAvisoDeshacer.Token);
    }

    private void ActualizarTextoDeshacer()
    {
        if (_marcadoAnterior == null) return;
        DeshacerMarcadoTexto = string.Format(
            LocalizationService.T(_marcadoAnteriorFueVisto ? "Det_DeshacerVistosFormato" : "Det_DeshacerNoVistosFormato"), _marcadoAnterior.Count);
    }

    private async Task OcultarAvisoDeshacerTrasUnRatoAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(DuracionAvisoDeshacer, ct);
            Core.HiloUi.Ejecutar(OlvidarMarcadoAnterior);
        }
        catch (OperationCanceledException)
        {
            // Se deshizo, se descartó o llegó otro marcado: ese aviso ya no cuenta.
        }
    }

    [RelayCommand]
    private void OlvidarMarcadoAnterior()
    {
        _ctsAvisoDeshacer?.Cancel();
        _marcadoAnterior = null;
        PuedeDeshacerMarcado = false;
    }

    /// <summary>
    /// Deja los episodios del último marcado como estaban: visto/no visto y el punto donde se había quedado cada uno (marcar
    /// lo pone a cero). La fecha de la última reproducción no se toca al marcar, así que tampoco hay que restaurarla.
    /// </summary>
    [RelayCommand]
    private async Task DeshacerMarcadoAsync()
    {
        var anime = Anime;
        var anterior = _marcadoAnterior;
        OlvidarMarcadoAnterior();
        if (anime == null || anterior == null || anterior.Count == 0) return;

        var registros = new List<RegistroEpisodio>(anterior.Count);
        foreach (var previo in anterior)
        {
            var ep = previo.Episodio;
            ep.Visto = previo.Visto;
            ep.ProgresoSegundos = previo.ProgresoSegundos;
            registros.Add(new RegistroEpisodio
            {
                AniListId = anime.AniListId,
                NumeroEpisodio = ep.NumeroEpisodio,
                VistoLocal = previo.Visto,
                FavoritoLocal = ep.Favorito,
                ProgresoSegundos = previo.ProgresoSegundos,
                TotalSegundos = ep.TotalSegundos,
                RutaArchivo = ep.RutaCompleta ?? string.Empty
            });
        }
        await _databaseService.GuardarRegistrosEpisodioBulkAsync(registros);
        anime.EpisodiosVistos = _todosLosEpisodios.Count(e => e.Visto);
        await _databaseService.ActualizarAnimeAsync(anime);
        WeakReferenceMessenger.Default.Send(new EpisodioActualizadoMensaje(anime.AniListId, 0, false, 0, 0));
        AplicarFiltrosYOrdenamiento();
    }

    /// <summary>Guarda de una vez el estado visto/no visto de varios episodios (el punto de reanudación se pone a cero, la
    /// duración conocida se conserva).</summary>
    private async Task MarcarEpisodiosAsync(List<EpisodioItem> episodios, bool visto, bool pedirConfirmacion)
    {
        var anime = Anime;
        if (anime == null || episodios.Count == 0) return;

        if (pedirConfirmacion && episodios.Count > 1)
        {
            bool confirmar = await _dialogService.MostrarDialogoAsync(
                LocalizationService.T(visto ? "Det_MarcarComoVistos" : "Det_MarcarComoNoVistos"),
                string.Format(LocalizationService.T(visto ? "Det_MarcarVistosConfirmacionFormato" : "Det_MarcarNoVistosConfirmacionFormato"), episodios.Count),
                true, visto ? "CheckAll" : "EyeOffOutline", visto ? "#10B981" : "#EF4444");
            if (!confirmar) return;
        }

        var anterior = episodios.Select(ep => new EstadoPrevio(ep, ep.Visto, ep.ProgresoSegundos)).ToList();
        var listaRegistros = new List<RegistroEpisodio>(episodios.Count);
        foreach (var ep in episodios)
        {
            ep.Visto = visto;
            ep.ProgresoSegundos = 0;
            listaRegistros.Add(new RegistroEpisodio
            {
                AniListId = anime.AniListId,
                NumeroEpisodio = ep.NumeroEpisodio,
                VistoLocal = visto,
                FavoritoLocal = ep.Favorito,
                TotalSegundos = ep.TotalSegundos,
                RutaArchivo = ep.RutaCompleta ?? string.Empty
            });
        }
        await _databaseService.GuardarRegistrosEpisodioBulkAsync(listaRegistros);
        anime.EpisodiosVistos = _todosLosEpisodios.Count(e => e.Visto);
        await _databaseService.ActualizarAnimeAsync(anime);
        WeakReferenceMessenger.Default.Send(new EpisodioActualizadoMensaje(anime.AniListId, 0, false, 0, 0));
        AplicarFiltrosYOrdenamiento();
        OfrecerDeshacer(anterior, visto);
    }

    /// <summary>
    /// Alterna el estado visto/no visto de un único episodio desde el menú contextual.
    /// </summary>
    [RelayCommand]
    private async Task AlternarVistoEpisodioAsync(EpisodioItem? episodio)
    {
        if (episodio == null || Anime == null) return;

        episodio.Visto = !episodio.Visto;
        episodio.ProgresoSegundos = 0; // consistente con el reset que hace la BD al marcar manualmente

        var registro = new RegistroEpisodio
        {
            AniListId = Anime.AniListId,
            NumeroEpisodio = episodio.NumeroEpisodio,
            VistoLocal = episodio.Visto,
            FavoritoLocal = episodio.Favorito,
            RutaArchivo = episodio.RutaCompleta ?? string.Empty
        };

        await _databaseService.GuardarRegistroEpisodioAsync(registro);
        Anime.EpisodiosVistos = _todosLosEpisodios.Count(e => e.Visto);
        await _databaseService.ActualizarAnimeAsync(Anime);
        WeakReferenceMessenger.Default.Send(new EpisodioActualizadoMensaje(Anime.AniListId, episodio.NumeroEpisodio, episodio.Visto, 0, 0));
        AplicarFiltrosYOrdenamiento();
    }

    /// <summary>
    /// Marca como vistos el episodio seleccionado y todos los anteriores (&lt;= N).
    /// </summary>
    [RelayCommand]
    private Task MarcarAnterioresVistosAsync(EpisodioItem? episodio)
    {
        if (episodio == null) return Task.CompletedTask;

        var aMarcar = _todosLosEpisodios.Where(e => e.NumeroEpisodio <= episodio.NumeroEpisodio && !e.Visto).ToList();
        return MarcarEpisodiosAsync(aMarcar, visto: true, pedirConfirmacion: false);
    }

    /// <summary>
    /// Marca toda la temporada / serie completa del anime como vista.
    /// </summary>
    [RelayCommand]
    private Task MarcarTemporadaCompletaAsync() =>
        MarcarEpisodiosAsync(_todosLosEpisodios.Where(e => !e.Visto).ToList(), visto: true, pedirConfirmacion: false);


    [RelayCommand]
    private async Task DescargarEpisodioAsync(EpisodioItem episodio)
    {
        if (episodio == null || Anime == null) return;
        if (episodio.IsDownloading) return;

        episodio.IsDownloading = true;
        episodio.DownloadProgress = 0;
        ActualizarAccionPrincipal();

        var titulosCandidatos = new List<string>();
        if (!string.IsNullOrWhiteSpace(Anime.NombresAlternativos))
        {
            titulosCandidatos.AddRange(Anime.NombresAlternativos.Split([" | ", ";"], StringSplitOptions.RemoveEmptyEntries));
        }

        await _downloadService.IniciarDescargaEpisodioAsync(
            Anime.AniListId,
            Anime.Titulo,
            Anime.RutaCarpeta,
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
        if (episodio == null || Anime == null) return;
        if (episodio.IsDownloading) return;
        if (_nyaaSourceService == null || _selectorTorrentService == null) return;

        var titulosCandidatos = new List<string> { Anime.Titulo };
        if (!string.IsNullOrWhiteSpace(Anime.NombresAlternativos))
        {
            titulosCandidatos.AddRange(Anime.NombresAlternativos.Split([" | ", ";"], StringSplitOptions.RemoveEmptyEntries));
        }

        var configuracion = _settingsService?.ObtenerConfiguracion();
        var candidatos = await _nyaaSourceService.BuscarCandidatosParaElegirAsync(
            titulosCandidatos, episodio.NumeroEpisodio,
            configuracion?.GrupoFansubPreferidoTorrent, configuracion?.ResolucionPreferidaTorrent,
            Anime.AniListId);

        if (candidatos.Count == 0)
        {
            _dialogService.MostrarToast(
                LocalizationService.T("Sel_Titulo"),
                LocalizationService.T("Sel_SinCandidatos"),
                "AlertCircleOutline", "#F59E0B");
            return;
        }

        string tituloEpisodio = $"{Anime.Titulo} — {episodio.TituloVisual}";
        var elegido = await _selectorTorrentService.MostrarSelectorAsync(tituloEpisodio, candidatos);
        if (elegido == null) return; // el usuario canceló

        episodio.IsDownloading = true;
        episodio.DownloadProgress = 0;
        ActualizarAccionPrincipal();

        await _downloadService.IniciarDescargaTorrentManualAsync(
            Anime.AniListId,
            Anime.Titulo,
            Anime.RutaCarpeta,
            episodio.NumeroEpisodio,
            elegido.Value,
            titulosCandidatos);
    }
}
