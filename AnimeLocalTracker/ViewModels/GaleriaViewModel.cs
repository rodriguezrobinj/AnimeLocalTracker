using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Messages;

namespace AnimeLocalTracker.ViewModels;

public partial class GaleriaViewModel : ObservableObject, IAlEntrarEnPestana, IDisposable,
    IRecipient<UsuarioLogeadoMensaje>,
    IRecipient<AnimeAñadidoMensaje>,
    IRecipient<UsuarioDesconectadoMensaje>,
    IRecipient<EpisodioActualizadoMensaje>,
    IRecipient<IdiomaCambiadoMensaje>
{
    private readonly IAnimeTrackingService _animeTrackingService;
    private readonly IDatabaseService _databaseService;
    private readonly IAuthService _authService;
    private readonly IDialogService _dialogService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IImageCacheService _imageCacheService;
    private readonly IPerfilAniListService? _perfilAniList;
    private readonly IFileScannerService _fileScannerService;
    private readonly PrecargaBiblioteca? _precarga;
    
    public bool BibliotecaVacia => !EstaCargando && BibliotecaLocales.Count == 0;

    public bool SinResultados => BibliotecaLocales.Count > 0 && (BibliotecaFiltrada?.IsEmpty ?? false);

    public int TotalAnimesBiblioteca => BibliotecaLocales.Count;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BibliotecaVacia))]
    [NotifyPropertyChangedFor(nameof(SinResultados))]
    [NotifyPropertyChangedFor(nameof(TotalAnimesBiblioteca))]
    [NotifyPropertyChangedFor(nameof(ConteoFiltradosTexto))]
    private ObservableCollection<AnimeItem> _bibliotecaLocales = [];

    partial void OnBibliotecaLocalesChanged(ObservableCollection<AnimeItem> value) => QueVeoHoy.AlCambiarLaBiblioteca();

    /// <summary>El panel "Qué veo hoy" (ruleta de portadas): su propio ViewModel, que usa esta biblioteca.</summary>
    public QueVeoHoyViewModel QueVeoHoy { get; }

    public void Dispose()
    {
        QueVeoHoy.Dispose();
        GC.SuppressFinalize(this);
    }

    public ICollectionView? BibliotecaFiltrada { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HayFiltrosActivos))]
    [NotifyPropertyChangedFor(nameof(ConteoFiltradosTexto))]
    private string _textoBusqueda = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EsFiltroTodos))]
    [NotifyPropertyChangedFor(nameof(EsFiltroViendo))]
    [NotifyPropertyChangedFor(nameof(EsFiltroCompletados))]
    [NotifyPropertyChangedFor(nameof(EsFiltroPlaneando))]
    [NotifyPropertyChangedFor(nameof(EsFiltroEnPausa))]
    [NotifyPropertyChangedFor(nameof(EsFiltroAbandonados))]
    [NotifyPropertyChangedFor(nameof(HayFiltrosActivos))]
    [NotifyPropertyChangedFor(nameof(ConteoFiltradosTexto))]
    private string _filtroEstado = "Todos"; // Todos, Viendo, Completados, Planeando, EnPausa, Abandonados

    public bool EsFiltroEnPausa => FiltroEstado == "EnPausa";
    public bool EsFiltroAbandonados => FiltroEstado == "Abandonados";

    public bool EsFiltroTodos => FiltroEstado == "Todos";
    public bool EsFiltroViendo => FiltroEstado == "Viendo";
    public bool EsFiltroCompletados => FiltroEstado == "Completados";
    public bool EsFiltroPlaneando => FiltroEstado == "Planeando";

    // --- FILTROS AVANZADOS Y ORDENACIÓN ---
    public static string TodosLosGeneros => LocalizationService.T("Gal_TodosLosGeneros");

    [ObservableProperty]
    private ObservableCollection<string> _generosDisponibles = [TodosLosGeneros];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HayFiltrosActivos))]
    [NotifyPropertyChangedFor(nameof(ConteoFiltradosTexto))]
    [NotifyPropertyChangedFor(nameof(CantidadFiltrosAvanzadosActivos))]
    [NotifyPropertyChangedFor(nameof(TieneFiltrosAvanzadosActivos))]
    private string _generoSeleccionado = TodosLosGeneros;

    // --- FILTROS DE TEMPORADA Y AÑO ---
    /// <summary>
    /// "Todas las temporadas". El filtro guarda el código de AniList (WINTER…) y la vista lo traduce al mostrarlo
    /// (TemporadaTextoConverter): antes guardaba el texto traducido y al cambiar de idioma el filtro se perdía.
    /// </summary>
    public const string TodasLasTemporadas = "";
    public static string TodosLosAños => LocalizationService.T("Gal_TodosLosAnios");

    [ObservableProperty]
    private ObservableCollection<string> _temporadasDisponibles = [TodasLasTemporadas];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HayFiltrosActivos))]
    [NotifyPropertyChangedFor(nameof(ConteoFiltradosTexto))]
    [NotifyPropertyChangedFor(nameof(CantidadFiltrosAvanzadosActivos))]
    [NotifyPropertyChangedFor(nameof(TieneFiltrosAvanzadosActivos))]
    private string _temporadaSeleccionada = TodasLasTemporadas;

    [ObservableProperty]
    private ObservableCollection<string> _aniosDisponibles = [TodosLosAños];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HayFiltrosActivos))]
    [NotifyPropertyChangedFor(nameof(ConteoFiltradosTexto))]
    [NotifyPropertyChangedFor(nameof(CantidadFiltrosAvanzadosActivos))]
    [NotifyPropertyChangedFor(nameof(TieneFiltrosAvanzadosActivos))]
    private string _anioSeleccionado = TodosLosAños;

    // --- FAVORITO INDIVIDUAL POR ANIME (no es un estado/categoría) ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HayFiltrosActivos))]
    [NotifyPropertyChangedFor(nameof(ConteoFiltradosTexto))]
    [NotifyPropertyChangedFor(nameof(CantidadFiltrosAvanzadosActivos))]
    [NotifyPropertyChangedFor(nameof(TieneFiltrosAvanzadosActivos))]
    private bool _soloFavoritos;

    public static string[] OpcionesOrdenacion =>
    [
        LocalizationService.T("Gal_OrdenTituloAZ"),
        LocalizationService.T("Gal_OrdenTituloZA"),
        LocalizationService.T("Gal_OrdenMayorProgreso"),
        LocalizationService.T("Gal_OrdenMenorProgreso"),
        LocalizationService.T("Gal_OrdenMasEpisodios"),
        LocalizationService.T("Gal_OrdenMenosEpisodios"),
        LocalizationService.T("Gal_OrdenMasRecientes"),
        LocalizationService.T("Gal_OrdenAnadidosRecientes")
    ];

    public string[] ListaOpcionesOrdenacion => OpcionesOrdenacion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CantidadFiltrosAvanzadosActivos))]
    [NotifyPropertyChangedFor(nameof(TieneFiltrosAvanzadosActivos))]
    private string _criterioOrdenSeleccionado = OpcionesOrdenacion[0];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HayFiltrosActivos))]
    [NotifyPropertyChangedFor(nameof(ConteoFiltradosTexto))]
    [NotifyPropertyChangedFor(nameof(CantidadFiltrosAvanzadosActivos))]
    [NotifyPropertyChangedFor(nameof(TieneFiltrosAvanzadosActivos))]
    private bool _soloConEpisodiosPendientes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HayFiltrosActivos))]
    [NotifyPropertyChangedFor(nameof(ConteoFiltradosTexto))]
    [NotifyPropertyChangedFor(nameof(CantidadFiltrosAvanzadosActivos))]
    [NotifyPropertyChangedFor(nameof(TieneFiltrosAvanzadosActivos))]
    private bool _soloConCarpetaLocal;

    [ObservableProperty]
    private bool _panelFiltrosVisible = false;

    public bool HayFiltrosActivos =>
        !string.IsNullOrWhiteSpace(TextoBusqueda) ||
        FiltroEstado != "Todos" ||
        (GeneroSeleccionado != TodosLosGeneros && GeneroSeleccionado != "Todos") ||
        TemporadaSeleccionada != TodasLasTemporadas ||
        AnioSeleccionado != TodosLosAños ||
        SoloConEpisodiosPendientes ||
        SoloConCarpetaLocal ||
        SoloFavoritos;

    public int CantidadFiltrosAvanzadosActivos
    {
        get
        {
            int count = 0;
            if (!string.IsNullOrWhiteSpace(GeneroSeleccionado) && GeneroSeleccionado != TodosLosGeneros && GeneroSeleccionado != "Todos")
                count++;
            if (TemporadaSeleccionada != TodasLasTemporadas)
                count++;
            if (AnioSeleccionado != TodosLosAños)
                count++;
            if (CriterioOrdenSeleccionado != OpcionesOrdenacion[0])
                count++;
            if (SoloConEpisodiosPendientes)
                count++;
            if (SoloConCarpetaLocal)
                count++;
            if (SoloFavoritos)
                count++;
            return count;
        }
    }

    public bool TieneFiltrosAvanzadosActivos => CantidadFiltrosAvanzadosActivos > 0;

    public string ConteoFiltradosTexto
    {
        get
        {
            int total = BibliotecaLocales.Count;
            if (total == 0) return LocalizationService.T("Gal_ConteoCero");

            if (BibliotecaFiltrada == null || !HayFiltrosActivos)
            {
                return total == 1 ? LocalizationService.T("Gal_ConteoUno") : string.Format(LocalizationService.T("Gal_ConteoVarios"), total);
            }

            int visibles = BibliotecaFiltrada.Cast<object>().Count();
            return string.Format(LocalizationService.T("Gal_ConteoFiltrado"), visibles, total);
        }
    }

    /// <summary>Vuelve a filtrar la lista y avisa de lo que depende de qué animes se ven.</summary>
    private void RefrescarFiltro()
    {
        BibliotecaFiltrada?.Refresh();
        OnPropertyChanged(nameof(SinResultados));
        OnPropertyChanged(nameof(ConteoFiltradosTexto));
        OnPropertyChanged(nameof(SeleccionadosTexto));
    }

    partial void OnTextoBusquedaChanged(string value) => RefrescarFiltro();
    partial void OnGeneroSeleccionadoChanged(string value) => RefrescarFiltro();
    partial void OnTemporadaSeleccionadaChanged(string value) => RefrescarFiltro();
    partial void OnAnioSeleccionadoChanged(string value) => RefrescarFiltro();
    partial void OnSoloFavoritosChanged(bool value) => RefrescarFiltro();
    partial void OnSoloConEpisodiosPendientesChanged(bool value) => RefrescarFiltro();
    partial void OnSoloConCarpetaLocalChanged(bool value) => RefrescarFiltro();

    partial void OnCriterioOrdenSeleccionadoChanged(string value)
    {
        AplicarOrdenacion(value);
    }

    [RelayCommand]
    private void CambiarFiltroEstado(string nuevoFiltro)
    {
        FiltroEstado = nuevoFiltro;
        RefrescarFiltro();
    }

    [RelayCommand]
    private void TogglePanelFiltros()
    {
        PanelFiltrosVisible = !PanelFiltrosVisible;
    }

    [RelayCommand]
    public void LimpiarFiltros()
    {
        TextoBusqueda = string.Empty;
        FiltroEstado = "Todos";
        GeneroSeleccionado = TodosLosGeneros;
        TemporadaSeleccionada = TodasLasTemporadas;
        AnioSeleccionado = TodosLosAños;
        SoloConEpisodiosPendientes = false;
        SoloConCarpetaLocal = false;
        SoloFavoritos = false;
        CriterioOrdenSeleccionado = OpcionesOrdenacion[0];

        BibliotecaFiltrada?.Refresh();
        AplicarOrdenacion(OpcionesOrdenacion[0]);
        OnPropertyChanged(nameof(SinResultados));
        OnPropertyChanged(nameof(HayFiltrosActivos));
        OnPropertyChanged(nameof(ConteoFiltradosTexto));
        OnPropertyChanged(nameof(CantidadFiltrosAvanzadosActivos));
        OnPropertyChanged(nameof(TieneFiltrosAvanzadosActivos));
    }

    /// <summary>
    /// LOC-08: re-genera los desplegables de filtros (género/temporada/año/orden), que son
    /// listas de texto plano y no se refrescan solas al cambiar de idioma como sí lo hacen
    /// los bindings a LocalizationService.Instance[Clave].
    /// </summary>
    private void AplicarCambioIdioma()
    {
        int ordenIdx = Array.IndexOf(OpcionesOrdenacion, CriterioOrdenSeleccionado);
        OnPropertyChanged(nameof(ListaOpcionesOrdenacion));
        CriterioOrdenSeleccionado = OpcionesOrdenacion[ordenIdx >= 0 ? ordenIdx : 0];

        ActualizarGenerosDisponibles();
        ActualizarTemporadasYAniosDisponibles();
        RefrescarFiltro();
    }

    [ObservableProperty] private bool _estaConectado;
    [ObservableProperty] private string _nombreUsuarioAniList = LocalizationService.T("Gal_UsuarioDefault");
    [ObservableProperty] private string? _avatarUsuarioAniList;
    
    /// <summary>Hasta que llega la biblioteca al abrir la app: sin esto lo primero que se dibujaba era "biblioteca vacía".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BibliotecaVacia))]
    private bool _estaCargando = true;

    [ObservableProperty] private bool _estaActualizando;
    [ObservableProperty] private string _textoProgreso = string.Empty;

    // --- PROPIEDADES DE SELECCIÓN MÚLTIPLE ---
    [ObservableProperty] private bool _modoSeleccion;

    /// <summary>Animes marcados que además se ven con el filtro actual: son los únicos a los que se aplica "Mover a".</summary>
    private List<AnimeItem> SeleccionadosVisibles() =>
        (BibliotecaFiltrada?.Cast<AnimeItem>() ?? BibliotecaLocales).Where(a => a.EstaSeleccionado).ToList();

    public string SeleccionadosTexto => string.Format(LocalizationService.T("Gal_SeleccionadosFormato"), SeleccionadosVisibles().Count);

    /// <summary>Cuánto se deja a la vista el mensaje final de "Actualizar biblioteca". Ajustable solo en pruebas.</summary>
    internal TimeSpan PausaAviso { get; set; } = TimeSpan.FromSeconds(2);

    public GaleriaViewModel(
        IAnimeTrackingService animeTrackingService, 
        IDatabaseService databaseService, 
        IAuthService authService, 
        IDialogService dialogService, 
        IHttpClientFactory httpClientFactory,
        IImageCacheService imageCacheService,
        IFileScannerService? fileScannerService = null,
        MinijuegosViewModel? minijuegos = null,
        IPerfilAniListService? perfilAniList = null,
        PrecargaBiblioteca? precarga = null)
    {
        _precarga = precarga;
        _minijuegos = minijuegos;
        // Los juegos usan la biblioteca que la Galería ya tiene en memoria en vez de leerla otra vez de la base de datos.
        _minijuegos?.UsarBiblioteca(() => BibliotecaLocales);
        _perfilAniList = perfilAniList;
        _animeTrackingService = animeTrackingService;
        _databaseService = databaseService;
        _authService = authService;
        _dialogService = dialogService;
        _httpClientFactory = httpClientFactory;
        _imageCacheService = imageCacheService;
        _fileScannerService = fileScannerService ?? new FileScannerService();
        QueVeoHoy = new QueVeoHoyViewModel(() => BibliotecaLocales, _fileScannerService, _databaseService, _imageCacheService);

        WeakReferenceMessenger.Default.Register<UsuarioLogeadoMensaje>(this);
        WeakReferenceMessenger.Default.Register<AnimeAñadidoMensaje>(this);
        WeakReferenceMessenger.Default.Register<UsuarioDesconectadoMensaje>(this);
        WeakReferenceMessenger.Default.Register<EpisodioActualizadoMensaje>(this);
        WeakReferenceMessenger.Default.Register<IdiomaCambiadoMensaje>(this);

        _ = CargarBibliotecaAsync();
    }

    /// <summary>LOC-08 (via WeakReferenceMessenger, no suscripción directa al evento estático
    /// de LocalizationService: eso acumulaba suscriptores para siempre en tests).</summary>
    public void Receive(IdiomaCambiadoMensaje message) => AplicarCambioIdioma();

    public void Receive(UsuarioLogeadoMensaje message)
    {
        _ = CargarPerfilUsuarioAsync();
    }

    public void Receive(UsuarioDesconectadoMensaje message)
    {
        EstaConectado = false;
        NombreUsuarioAniList = LocalizationService.T("Gal_UsuarioDefault");
        AvatarUsuarioAniList = null;
    }

    public void Receive(AnimeAñadidoMensaje message)
    {
        // Los handlers del messenger NUNCA deben lanzar: una excepción aquí aborta
        // la entrega del mensaje al resto de receptores suscritos.
        try
        {
            if (System.Windows.Application.Current?.Dispatcher is { } d && !d.CheckAccess() && System.Windows.Application.Current.MainWindow != null)
            {
                d.InvokeAsync(() => Receive(message));
                return;
            }

            if (!BibliotecaLocales.Any(a => a.AniListId == message.NuevoAnime.AniListId))
            {
                _ = _imageCacheService.ObtenerPortada(message.NuevoAnime.AniListId, message.NuevoAnime.UrlPortada);
                message.NuevoAnime.ResolverPortadaLocal();
                message.NuevoAnime.NotificarPortadaActualizada();
                BibliotecaLocales.Add(message.NuevoAnime);
                ActualizarGenerosDisponibles();
                ActualizarTemporadasYAniosDisponibles();
                OnPropertyChanged(nameof(BibliotecaVacia)); QueVeoHoy.AlCambiarLaBiblioteca();
                OnPropertyChanged(nameof(TotalAnimesBiblioteca));
                OnPropertyChanged(nameof(ConteoFiltradosTexto));

                if (_imageCacheService.ObtenerPortadaEnMemoria(message.NuevoAnime.AniListId) == null && !string.IsNullOrWhiteSpace(message.NuevoAnime.UrlPortada))
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var img = await _imageCacheService.ObtenerPortadaAsync(message.NuevoAnime.AniListId, message.NuevoAnime.UrlPortada);
                            if (img != null)
                            {
                                message.NuevoAnime.ResolverPortadaLocal();
                                System.Windows.Application.Current?.Dispatcher?.Invoke(() => message.NuevoAnime.NotificarPortadaActualizada());
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Debug("GaleriaViewModel", $"Error cargando portada de nuevo anime: {ex.Message}");
                        }
                    });
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("GaleriaViewModel", "Error al procesar AnimeAñadidoMensaje", ex);
        }
    }

    public void Receive(EpisodioActualizadoMensaje message)
    {
        // La tarjeta solo muestra cuántos episodios están vistos: el progreso de cada 3 s del reproductor no lo cambia, y antes
        // cada aviso releía de la base de datos todos los episodios del anime (1180 en One Piece) para contarlos otra vez.
        if (message.SoloProgreso) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var registros = await _databaseService.ObtenerRegistrosPorAnimeAsync(message.AnimeId) ?? new List<RegistroEpisodio>();
                int vistos = registros.Count(r => r.VistoLocal);

                // El mensaje puede llegar desde cualquier hilo: la colección y la lista filtrada solo se tocan en el de la interfaz.
                void Aplicar()
                {
                    var anime = BibliotecaLocales.FirstOrDefault(a => a.AniListId == message.AnimeId);
                    if (anime == null || anime.EpisodiosVistos == vistos) return;

                    anime.EpisodiosVistos = vistos;
                    // "Pendientes" y el orden por progreso dependen de lo visto: sin refrescar, el anime se quedaba donde estaba.
                    RefrescarFiltro();
                }

                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher == null) Aplicar();
                else if (!dispatcher.HasShutdownStarted) _ = dispatcher.InvokeAsync(Aplicar);
            }
            catch (Exception ex)
            {
                AppLogger.Error("GaleriaViewModel", "Error al actualizar episodio visto en galería", ex);
            }
        });
    }
    
    private async Task CargarBibliotecaAsync()
    {
        try
        {
            var reloj = System.Diagnostics.Stopwatch.StartNew();
            List<Models.AnimeItem>? animes = null;
            Dictionary<int, int>? vistosPorAnime = null;

            // Al abrir la app, la biblioteca (y las portadas de la primera pantalla) ya se están leyendo desde antes de
            // existir la ventana (ver PrecargaBiblioteca). Solo la primera vez; si falla, se lee aquí como siempre.
            var despachadorArranque = System.Windows.Application.Current?.Dispatcher;
            bool desdePrecarga = false;
            if (_precarga?.Consumir() is { } lecturaAdelantada)
            {
                try
                {
                    var datos = await lecturaAdelantada;
                    (animes, vistosPorAnime) = (datos.Animes, datos.VistosPorAnime);
                    desdePrecarga = despachadorArranque != null && despachadorArranque.CheckAccess();
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("GaleriaViewModel", $"La lectura adelantada de la biblioteca falló; se lee de nuevo: {ex.Message}");
                }
            }
            animes ??= await _databaseService.ObtenerTodosLosAnimesAsync() ?? new List<Models.AnimeItem>();
            // Solo hace falta cuántos episodios vistos tiene cada anime: lo cuenta la base de datos (antes se traían los miles
            // de registros de episodios enteros para contarlos aquí).
            vistosPorAnime ??= await _databaseService.ObtenerEpisodiosVistosPorAnimeAsync() ?? new Dictionary<int, int>();
            long msDatos = reloj.ElapsedMilliseconds;

            // Primero la ventana y después las tarjetas: construirlas detiene la interfaz unas décimas, y con la ventana ya a
            // la vista esa espera se nota mucho menos que con la pantalla todavía vacía.
            if (desdePrecarga) await despachadorArranque!.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);

            // MIGRACIÓN INTELIGENTE: Recuperar el estado basado en lo que realmente has visto localmente
            foreach (var a in animes)
            {
                a.EpisodiosVistos = vistosPorAnime.GetValueOrDefault(a.AniListId);

                // FUN-018: la migración de estado solo persiste cuando el estado CAMBIA de
                // verdad (antes se escribía la BD por cada anime en cada carga de biblioteca).
                if (string.IsNullOrEmpty(a.EstadoUsuario) || a.EstadoUsuario == "PLANNING")
                {
                    string estadoAnterior = a.EstadoUsuario;
                    int episodiosVistos = a.EpisodiosVistos;

                    if (episodiosVistos > 0)
                    {
                        a.EstadoUsuario = (a.TotalEpisodios > 0 && episodiosVistos >= a.TotalEpisodios)
                            ? "COMPLETED"
                            : "CURRENT";
                    }
                    else if (string.IsNullOrEmpty(a.EstadoUsuario))
                    {
                        a.EstadoUsuario = "PLANNING";
                    }

                    if (!string.Equals(estadoAnterior, a.EstadoUsuario, StringComparison.Ordinal))
                    {
                        await _databaseService.ActualizarAnimeAsync(a);
                    }
                }

                // La ruta local de la portada ya la resolvió DatabaseService.ObtenerTodosLosAnimesAsync en un hilo de
                // fondo: repetir aquí el File.Exists de cada anime bloqueaba el hilo de UI (~190 accesos a disco).
                // Las imágenes las carga CargarPortadasFaltantesEnSegundoPlanoAsync (RND-01).
            }

            BibliotecaLocales = new ObservableCollection<AnimeItem>(animes);
            ActualizarGenerosDisponibles();
            ActualizarTemporadasYAniosDisponibles();

            BibliotecaFiltrada = CollectionViewSource.GetDefaultView(BibliotecaLocales);
            BibliotecaFiltrada.Filter = FiltrarAnime;

            AplicarOrdenacion(CriterioOrdenSeleccionado);

            OnPropertyChanged(nameof(BibliotecaFiltrada));
            OnPropertyChanged(nameof(SinResultados));
            OnPropertyChanged(nameof(HayFiltrosActivos));
            OnPropertyChanged(nameof(ConteoFiltradosTexto));
            // Cuándo quedaron dibujadas las tarjetas: ContextIdle llega cuando la interfaz ya no tiene nada pendiente.
            long msEntregada = reloj.ElapsedMilliseconds;
            long msEntregadaDesdeInicio = MedidorRendimiento.MsDesdeInicio;
            void RegistrarCarga() => AppLogger.Info("GaleriaViewModel",
                $"[Perf] Biblioteca: {animes.Count} animes y sus episodios vistos leídos en {msDatos} ms; lista entregada a la pantalla a los {msEntregada} ms " +
                $"y tarjetas dibujadas {reloj.ElapsedMilliseconds - msEntregada} ms después ({msEntregadaDesdeInicio} y {MedidorRendimiento.MsDesdeInicio} ms desde que se abrió la app).");
            if (System.Windows.Application.Current?.Dispatcher is { } despachador)
                _ = despachador.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, RegistrarCarga);
            else
                RegistrarCarga();

            // El resto de portadas (las de la primera pantalla ya vienen de la lectura adelantada): cuando las tarjetas ya
            // están dibujadas, para que decodificarlas no compita con la interfaz por el procesador.
            if (desdePrecarga)
                _ = despachadorArranque!.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, () => { _ = CargarPortadasFaltantesEnSegundoPlanoAsync(animes); });
            else
                _ = CargarPortadasFaltantesEnSegundoPlanoAsync(animes);
            _ = CargarTemporadasYAniosFaltantesEnSegundoPlanoAsync(animes);

            await CargarPerfilUsuarioAsync();
            OnPropertyChanged(nameof(BibliotecaVacia)); QueVeoHoy.AlCambiarLaBiblioteca();
        }
        catch (Exception ex)
        {
            // Sin esto, un fallo de BD al arrancar deja la galería vacía y en silencio
            AppLogger.Error("GaleriaViewModel", "Error al cargar la biblioteca", ex);
        }
        finally
        {
            EstaCargando = false;
        }
    }

    /// <summary>
    /// Backfill en segundo plano de Temporada/AnioLanzamiento para animes que ya estaban
    /// en la biblioteca ANTES de esta funcionalidad (por eso los combos de la galería
    /// aparecían vacíos: nunca se capturó esa info al añadirlos). Mismo patrón que
    /// CargarPortadasFaltantesEnSegundoPlanoAsync: no bloquea el arranque de la galería.
    /// </summary>
    private async Task CargarTemporadasYAniosFaltantesEnSegundoPlanoAsync(IEnumerable<AnimeItem> animes)
    {
        var faltantes = animes.Where(a => string.IsNullOrWhiteSpace(a.Temporada) && a.AnioLanzamiento <= 0).ToList();
        if (faltantes.Count == 0) return;

        try
        {
            var datos = await _animeTrackingService.ObtenerAnimesPorIdsLoteAsync(faltantes.Select(a => a.AniListId));
            if (datos.Count == 0) return;

            var actualizaciones = new List<(AnimeItem Anime, string Temporada, int Anio)>();
            foreach (var anime in faltantes)
            {
                if (!datos.TryGetValue(anime.AniListId, out var media)) continue;

                string temporada = media.Season ?? string.Empty;
                int anio = media.StartDate?.Year ?? 0;
                if (string.IsNullOrEmpty(temporada) && anio <= 0) continue;

                actualizaciones.Add((anime, temporada, anio));
            }

            if (actualizaciones.Count == 0) return;

            void AplicarCambios()
            {
                foreach (var (anime, temporada, anio) in actualizaciones)
                {
                    anime.Temporada = temporada;
                    anime.AnioLanzamiento = anio;
                }
                ActualizarTemporadasYAniosDisponibles();
                BibliotecaFiltrada?.Refresh();
            }

            // RND-03: mutar propiedades de AnimeItem (enlazadas a la UI) requiere el hilo
            // de UI; la escritura en BD, no.
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                await dispatcher.InvokeAsync(AplicarCambios);
            }
            else
            {
                AplicarCambios();
            }

            await _databaseService.ActualizarAnimesAsync(actualizaciones.Select(a => a.Anime));
        }
        catch (Exception ex)
        {
            AppLogger.Debug("GaleriaViewModel", $"Error al completar temporada/año en segundo plano: {ex.Message}");
        }
    }

    /// <summary>
    /// Carga en segundo plano las portadas que aún no están en memoria. Dos claves de velocidad:
    /// (1) se piden en el orden en que la galería las muestra, así las tarjetas visibles se llenan
    /// primero (antes: orden de la BD, y las visibles quedaban casi al final); (2) se decodifican
    /// varias a la vez (antes: una por una, esperando cada decode antes de empezar la siguiente).
    /// </summary>
    private async Task CargarPortadasFaltantesEnSegundoPlanoAsync(IEnumerable<AnimeItem> animes)
    {
        var todos = animes as IReadOnlyList<AnimeItem> ?? animes.ToList();

        // Se calcula de forma síncrona (antes del primer await) porque enumerar la vista de la galería
        // requiere el hilo de UI.
        var enOrdenVisual = (BibliotecaFiltrada?.Cast<AnimeItem>().ToList()) ?? new List<AnimeItem>();
        var faltantes = enOrdenVisual
            .Concat(todos.Except(enOrdenVisual))
            .Where(a => !string.IsNullOrWhiteSpace(a.UrlPortada) && _imageCacheService.ObtenerPortadaEnMemoria(a.AniListId) == null)
            .ToList();
        if (faltantes.Count == 0) return;

        var reloj = System.Diagnostics.Stopwatch.StartNew();
        var listoEn = new System.Collections.Concurrent.ConcurrentDictionary<int, long>();
        var dispatcher = System.Windows.Application.Current?.Dispatcher;

        // La decodificación es trabajo de CPU: se usa la mitad de los hilos del procesador y el resto queda para la interfaz
        // (con todos menos uno, en un equipo de 2 núcleos la galería se arrastraba mientras cargaban las portadas).
        int paralelismo = Math.Clamp(Environment.ProcessorCount / 2, 1, 6);

        await Parallel.ForEachAsync(faltantes, new ParallelOptions { MaxDegreeOfParallelism = paralelismo }, async (anime, cancelacion) =>
        {
            var img = await _imageCacheService.ObtenerPortadaAsync(anime.AniListId, anime.UrlPortada);
            if (img == null) return;
            anime.ResolverPortadaLocal(); // aquí, en segundo plano: la Ficha usará el archivo y no la URL

            listoEn[anime.AniListId] = reloj.ElapsedMilliseconds;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                // RND-03: InvokeAsync para no bloquear el hilo de pool contra la UI
                _ = dispatcher.InvokeAsync(() => anime.NotificarPortadaActualizada());
            }
            else
            {
                anime.NotificarPortadaActualizada();
            }
        });

        // Telemetría ligera: cuándo estuvieron listas las 24 primeras tarjetas (las que se ven al abrir).
        var visibles = enOrdenVisual.Count > 0 ? enOrdenVisual.Take(24) : todos.Take(24);
        long visiblesMs = visibles.Where(v => listoEn.ContainsKey(v.AniListId)).Select(v => listoEn[v.AniListId]).DefaultIfEmpty(0).Max();
        AppLogger.Info("GaleriaViewModel", $"[Perf] Portadas: {listoEn.Count}/{faltantes.Count} listas en {reloj.ElapsedMilliseconds} ms; primeras 24 tarjetas visibles completas a los {visiblesMs} ms.");
    }

    
    public void ActualizarGenerosDisponibles()
    {
        var generosUnicos = BibliotecaLocales
            .SelectMany(a => a.GenerosLista)
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Select(g => g.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g)
            .ToList();

        var nuevaLista = new List<string> { TodosLosGeneros };
        nuevaLista.AddRange(generosUnicos);

        string prevSeleccion = GeneroSeleccionado;
        GenerosDisponibles = new ObservableCollection<string>(nuevaLista);

        if (nuevaLista.Contains(prevSeleccion))
        {
            GeneroSeleccionado = prevSeleccion;
        }
        else
        {
            GeneroSeleccionado = TodosLosGeneros;
        }
    }

    // Orden cronológico de temporada (no alfabético): Invierno → Primavera → Verano → Otoño
    private static readonly string[] OrdenTemporadas = ["WINTER", "SPRING", "SUMMER", "FALL"];

    public void ActualizarTemporadasYAniosDisponibles()
    {
        var temporadasUnicas = BibliotecaLocales
            .Where(a => !string.IsNullOrWhiteSpace(a.Temporada))
            .Select(a => a.Temporada)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var nuevaListaTemporadas = new List<string> { TodasLasTemporadas };
        nuevaListaTemporadas.AddRange(
            OrdenTemporadas.Where(t => temporadasUnicas.Contains(t, StringComparer.OrdinalIgnoreCase)));

        string prevTemporada = TemporadaSeleccionada;
        TemporadasDisponibles = new ObservableCollection<string>(nuevaListaTemporadas);
        TemporadaSeleccionada = nuevaListaTemporadas.Contains(prevTemporada) ? prevTemporada : TodasLasTemporadas;

        var aniosUnicos = BibliotecaLocales
            .Where(a => a.AnioLanzamiento > 0)
            .Select(a => a.AnioLanzamiento)
            .Distinct()
            .OrderByDescending(a => a)
            .Select(a => a.ToString())
            .ToList();

        var nuevaListaAnios = new List<string> { TodosLosAños };
        nuevaListaAnios.AddRange(aniosUnicos);

        string prevAnio = AnioSeleccionado;
        AniosDisponibles = new ObservableCollection<string>(nuevaListaAnios);
        AnioSeleccionado = nuevaListaAnios.Contains(prevAnio) ? prevAnio : TodosLosAños;
    }

    public void AplicarOrdenacion(string? criterio = null)
    {
        if (BibliotecaFiltrada == null) return;
        criterio ??= CriterioOrdenSeleccionado;

        // LOC-08: el criterio es texto localizado (cambia con el idioma) — comparar por
        // posición dentro de OpcionesOrdenacion en vez de contra literales en español.
        int idx = Array.IndexOf(OpcionesOrdenacion, criterio);

        using (BibliotecaFiltrada.DeferRefresh())
        {
            BibliotecaFiltrada.SortDescriptions.Clear();
            switch (idx)
            {
                case 0: // Título (A - Z)
                    BibliotecaFiltrada.SortDescriptions.Add(new SortDescription(nameof(AnimeItem.Titulo), ListSortDirection.Ascending));
                    break;
                case 1: // Título (Z - A)
                    BibliotecaFiltrada.SortDescriptions.Add(new SortDescription(nameof(AnimeItem.Titulo), ListSortDirection.Descending));
                    break;
                case 2: // Mayor Progreso
                    BibliotecaFiltrada.SortDescriptions.Add(new SortDescription(nameof(AnimeItem.ProgresoPorcentaje), ListSortDirection.Descending));
                    BibliotecaFiltrada.SortDescriptions.Add(new SortDescription(nameof(AnimeItem.Titulo), ListSortDirection.Ascending));
                    break;
                case 3: // Menor Progreso
                    BibliotecaFiltrada.SortDescriptions.Add(new SortDescription(nameof(AnimeItem.ProgresoPorcentaje), ListSortDirection.Ascending));
                    BibliotecaFiltrada.SortDescriptions.Add(new SortDescription(nameof(AnimeItem.Titulo), ListSortDirection.Ascending));
                    break;
                case 4: // Más Episodios
                    BibliotecaFiltrada.SortDescriptions.Add(new SortDescription(nameof(AnimeItem.TotalEpisodios), ListSortDirection.Descending));
                    BibliotecaFiltrada.SortDescriptions.Add(new SortDescription(nameof(AnimeItem.Titulo), ListSortDirection.Ascending));
                    break;
                case 5: // Menos Episodios
                    BibliotecaFiltrada.SortDescriptions.Add(new SortDescription(nameof(AnimeItem.TotalEpisodios), ListSortDirection.Ascending));
                    BibliotecaFiltrada.SortDescriptions.Add(new SortDescription(nameof(AnimeItem.Titulo), ListSortDirection.Ascending));
                    break;
                case 6: // Estreno más reciente (antes "Más recientes": ordenaba por el número de AniList sin decirlo)
                    BibliotecaFiltrada.SortDescriptions.Add(new SortDescription(nameof(AnimeItem.OrdenEstreno), ListSortDirection.Descending));
                    BibliotecaFiltrada.SortDescriptions.Add(new SortDescription(nameof(AnimeItem.AniListId), ListSortDirection.Descending));
                    break;
                case 7: // Añadidos recientemente: los que no tienen fecha de alta (anteriores a guardarla) quedan al final
                    BibliotecaFiltrada.SortDescriptions.Add(new SortDescription(nameof(AnimeItem.FechaAgregadoUtc), ListSortDirection.Descending));
                    BibliotecaFiltrada.SortDescriptions.Add(new SortDescription(nameof(AnimeItem.AniListId), ListSortDirection.Descending));
                    break;
                default:
                    BibliotecaFiltrada.SortDescriptions.Add(new SortDescription(nameof(AnimeItem.Titulo), ListSortDirection.Ascending));
                    break;
            }
        }
    }

    /// <summary>"pokemon" encuentra "Pokémon": ni mayúsculas ni tildes cuentan al buscar.</summary>
    private static bool ContieneSinTildes(string? texto, string busqueda) =>
        !string.IsNullOrEmpty(texto) && System.Globalization.CultureInfo.InvariantCulture.CompareInfo.IndexOf(
            texto, busqueda, System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace) >= 0;

    private bool FiltrarAnime(object obj)
    {
        if (obj is not AnimeItem anime) return false;

        // 1. Filtro por texto (título o nombres alternativos)
        if (!string.IsNullOrWhiteSpace(TextoBusqueda))
        {
            if (!ContieneSinTildes(anime.Titulo, TextoBusqueda) && !ContieneSinTildes(anime.NombresAlternativos, TextoBusqueda))
            {
                return false;
            }
        }

        // 2. Filtro por estado
        if (FiltroEstado != "Todos")
        {
            bool coincide = FiltroEstado switch
            {
                "Viendo" => anime.EstadoUsuario is "CURRENT" or "REPEATING", // verlo de nuevo también es estar viéndolo
                "Completados" => anime.EstadoUsuario == "COMPLETED",
                "Planeando" => anime.EstadoUsuario == "PLANNING",
                "EnPausa" => anime.EstadoUsuario == "PAUSED",
                "Abandonados" => anime.EstadoUsuario == "DROPPED",
                _ => true
            };
            if (!coincide) return false;
        }

        // 3. Filtro por género
        if (!string.IsNullOrWhiteSpace(GeneroSeleccionado) &&
            GeneroSeleccionado != TodosLosGeneros &&
            GeneroSeleccionado != "Todos")
        {
            if (string.IsNullOrWhiteSpace(anime.Generos) || 
                !anime.GenerosLista.Any(g => g.Equals(GeneroSeleccionado, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        // 4. Filtro: solo con episodios pendientes
        if (SoloConEpisodiosPendientes)
        {
            if (anime.TotalEpisodios > 0 && anime.EpisodiosVistos >= anime.TotalEpisodios)
            {
                return false;
            }
        }

        // 5. Filtro: solo con carpeta local asociada
        if (SoloConCarpetaLocal)
        {
            if (string.IsNullOrWhiteSpace(anime.RutaCarpeta))
            {
                return false;
            }
        }

        // 6. Filtro por temporada de estreno
        if (TemporadaSeleccionada != TodasLasTemporadas)
        {
            if (!string.Equals(anime.Temporada, TemporadaSeleccionada, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        // 7. Filtro por año de estreno
        if (AnioSeleccionado != TodosLosAños)
        {
            if (anime.AnioLanzamiento <= 0 || anime.AnioLanzamiento.ToString() != AnioSeleccionado)
            {
                return false;
            }
        }

        // 8. Filtro: solo favoritos (individual por anime, no un estado/categoría)
        if (SoloFavoritos && !anime.EsFavorito)
        {
            return false;
        }

        return true;
    }

    private async Task CargarPerfilUsuarioAsync()
    {
        var token = _authService.ObtenerTokenGuardado();
        if (!string.IsNullOrEmpty(token))
        {
            EstaConectado = true;
            // Con copia local: sin conexión siguen tu nombre y tu avatar (antes salía el usuario por defecto).
            if (_perfilAniList != null)
            {
                var guardado = await _perfilAniList.ObtenerAsync(token);
                if (guardado != null)
                {
                    NombreUsuarioAniList = string.IsNullOrWhiteSpace(guardado.Nombre) ? LocalizationService.T("Gal_UsuarioDefault") : guardado.Nombre;
                    AvatarUsuarioAniList = guardado.Avatar;
                }
                return;
            }

            var perfil = await _animeTrackingService.ObtenerPerfilUsuarioAsync(token);
            if (perfil != null)
            {
                NombreUsuarioAniList = perfil.Name ?? LocalizationService.T("Gal_UsuarioDefault");
                AvatarUsuarioAniList = perfil.Avatar?.Large;
            }
        }
        else
        {
            EstaConectado = false;
        }
    }

    [RelayCommand]
    private void AñadirAnimeManual()
    {
        Pestanas.AgregarAnime.Abrir();
    }

    /// <summary>Favorito individual por anime: no altera EstadoUsuario ni ningún otro filtro/categoría.</summary>
    [RelayCommand]
    private async Task AlternarFavoritoAsync(AnimeItem? anime)
    {
        if (anime == null) return;

        anime.EsFavorito = !anime.EsFavorito;
        await _databaseService.ActualizarAnimeAsync(anime);

        if (SoloFavoritos)
        {
            BibliotecaFiltrada?.Refresh();
            OnPropertyChanged(nameof(SinResultados));
            OnPropertyChanged(nameof(ConteoFiltradosTexto));
        }
    }

    [RelayCommand]
    private async Task EliminarAnimeAsync(AnimeItem? anime)
    {
        if (anime == null) return;

        if (!await EliminacionAnime.ConfirmarYEliminarAsync(anime, _dialogService, _databaseService)) return;

        BibliotecaLocales.Remove(anime);
        ActualizarGenerosDisponibles();
        ActualizarTemporadasYAniosDisponibles();
        OnPropertyChanged(nameof(BibliotecaVacia)); QueVeoHoy.AlCambiarLaBiblioteca();
        OnPropertyChanged(nameof(TotalAnimesBiblioteca));
        OnPropertyChanged(nameof(ConteoFiltradosTexto));
    }

    private bool _menuUsuarioAbierto;
    public bool MenuUsuarioAbierto
    {
        get => _menuUsuarioAbierto;
        set => SetProperty(ref _menuUsuarioAbierto, value);
    }

    [RelayCommand]
    private void ToggleMenuUsuario()
    {
        MenuUsuarioAbierto = !MenuUsuarioAbierto;
    }

    [RelayCommand]
    private void CerrarMenuUsuario()
    {
        MenuUsuarioAbierto = false;
    }

    [RelayCommand]
    private async Task ConectarAniListAsync()
    {
        MenuUsuarioAbierto = false;

        // PRI-02: consentimiento informado ANTES del OAuth — la primera vez se explica
        // qué se lee y qué se escribe en AniList (antes el navegador se abría sin copy).
        bool consentimiento = await _dialogService.MostrarDialogoAsync(
            LocalizationService.T("Gal_ConectarAniListTitulo"),
            LocalizationService.T("Gal_ConectarAniListMsj"),
            true,
            "ShieldAccount",
            "#60A5FA");

        if (!consentimiento) return;

        bool exito = await _authService.IniciarSesionAsync();
        if (exito)
        {
            await _dialogService.MostrarDialogoAsync(LocalizationService.T("Gal_NubeActivadaTitulo"), LocalizationService.T("Gal_NubeActivadaMsj"), false, "CloudCheck", "#4CAF50");
        }
        else
        {
            await _dialogService.MostrarDialogoAsync(LocalizationService.T("Gal_AutenticacionCanceladaTitulo"), LocalizationService.T("Gal_AutenticacionCanceladaMsj"), false, "AlertCircle", "#FF5252");
        }
    }

    [RelayCommand]
    private async Task DesconectarAniListAsync()
    {
        MenuUsuarioAbierto = false; // Cierra el menú antes de desloguear
        _authService.CerrarSesion();
        await _dialogService.MostrarDialogoAsync(LocalizationService.T("Gal_SesionCerradaTitulo"), LocalizationService.T("Gal_SesionCerradaMsj"), false, "Logout", "#FF5252");
    }

    [RelayCommand]
    private async Task ActualizarBibliotecaAsync()
    {
        if (EstaActualizando) return;

        var listaAnimes = BibliotecaLocales.ToList();
        if (listaAnimes.Count == 0) return;

        EstaActualizando = true;
        try
        {
            await ActualizarBibliotecaInternoAsync(listaAnimes);
        }
        catch (Exception ex)
        {
            AppLogger.Error("GaleriaViewModel", "Error al actualizar la biblioteca", ex);
            _dialogService.MostrarToast(LocalizationService.T("Gal_ActualizacionErrorTitulo"), LocalizationService.T("Gal_ActualizacionErrorMsj"), "AlertCircle", "#EF4444");
        }
        finally
        {
            // Sin esto, un fallo a medias dejaba el panel en pantalla y el botón sin responder hasta reiniciar la app.
            EstaActualizando = false;
        }
    }

    private async Task ActualizarBibliotecaInternoAsync(List<AnimeItem> listaAnimes)
    {
        TextoProgreso = LocalizationService.T("Gal_ConsultandoAniList");

        // RND-02: una consulta loteada (50 animes por request) reemplaza las ~300
        // llamadas seriales (anime + seguimiento por separado) con Task.Delay(250).
        var token = EstaConectado ? _authService.ObtenerTokenGuardado() : null;
        var datosLote = await _animeTrackingService.ObtenerAnimesPorIdsLoteAsync(
            listaAnimes.Select(a => a.AniListId), token);
        if (datosLote.Count == 0)
        {
            // Sin conexión (o AniList caído): antes recorría la lista sin cambiar nada y decía "¡Actualización completada con éxito!".
            TextoProgreso = LocalizationService.T("Gal_ActualizacionSinConexion");
            await Task.Delay(PausaAviso * 1.5);
            return;
        }

        // Un estado cambiado aquí que aún no llegó a AniList (sin conexión) manda sobre lo que AniList diga: se enviará después.
        var estadosSinEnviar = (await _databaseService.ObtenerSeguimientosPendientesAsync() ?? new List<SeguimientoLocal>())
            .Select(s => s.AniListId).ToHashSet();

        var modificados = new List<Models.AnimeItem>();
        var proximasEmisiones = new List<Models.ProximaEmisionLocal>();
        foreach (var anime in listaAnimes)
        {
            if (!datosLote.TryGetValue(anime.AniListId, out var datosFrescos)) continue;

            int episodiosEmitidos = datosFrescos.EpisodiosEmitidos(anime.TotalEpisodios);

            // PERF-06: detectar cambios y persistir por lote al final (antes 1 UPDATE por anime).
            bool cambio = anime.TotalEpisodios != episodiosEmitidos
                          || !string.Equals(anime.Estado, datosFrescos.Status ?? "UNKNOWN", StringComparison.Ordinal);

            anime.TotalEpisodios = episodiosEmitidos;
            anime.Estado = datosFrescos.Status ?? "UNKNOWN";

            // Backfill de Temporada/AnioLanzamiento para animes añadidos antes de esta
            // funcionalidad (mismos datos que ya trae este lote, sin llamadas extra).
            if (string.IsNullOrWhiteSpace(anime.Temporada) && anime.AnioLanzamiento <= 0)
            {
                string temporadaFresca = datosFrescos.Season ?? string.Empty;
                int anioFresco = datosFrescos.StartDate?.Year ?? 0;
                if (!string.IsNullOrEmpty(temporadaFresca) || anioFresco > 0)
                {
                    anime.Temporada = temporadaFresca;
                    anime.AnioLanzamiento = anioFresco;
                    cambio = true;
                }
            }

            // El estado personal del usuario viene embebido en la misma consulta loteada
            // (mediaListEntry del usuario autenticado): sin llamadas extra por anime.
            if (token != null && !estadosSinEnviar.Contains(anime.AniListId)
                && datosFrescos.MediaListEntry != null && !string.IsNullOrEmpty(datosFrescos.MediaListEntry.Status))
            {
                if (!string.Equals(anime.EstadoUsuario, datosFrescos.MediaListEntry.Status, StringComparison.Ordinal))
                {
                    anime.EstadoUsuario = datosFrescos.MediaListEntry.Status;
                    cambio = true;
                }
            }

            // Cuenta atrás del próximo episodio: la misma respuesta ya trae la hora de emisión, así que
            // "Actualizar biblioteca" refresca todos los contadores sin peticiones extra.
            var copiaEmision = ProximaEmisionService.CopiaDesdeConsulta(anime.AniListId, datosFrescos.Status, datosFrescos.NextAiringEpisode, DateTime.UtcNow);
            if (copiaEmision != null) proximasEmisiones.Add(copiaEmision);

            if (cambio) modificados.Add(anime);
        }

        // Todas en una transacción (antes, una escritura suelta por anime en emisión).
        try { await _databaseService.GuardarProximasEmisionesAsync(proximasEmisiones); }
        catch (Exception ex) { AppLogger.Debug("GaleriaViewModel", $"No se pudieron guardar las próximas emisiones: {ex.Message}"); }

        if (modificados.Count > 0)
        {
            await _databaseService.ActualizarAnimesAsync(modificados);
            ActualizarTemporadasYAniosDisponibles();
            BibliotecaFiltrada?.Refresh();
        }

        TextoProgreso = LocalizationService.T("Gal_ActualizacionCompletada");
        await Task.Delay(PausaAviso);
    }

    [RelayCommand]
    private void AbrirDetalle(AnimeItem anime)
    {
        if (ModoSeleccion)
        {
            anime.EstaSeleccionado = !anime.EstaSeleccionado;
            OnPropertyChanged(nameof(SeleccionadosTexto));
            return;
        }

        // Enviamos el mensaje al MainViewModel para que cambie la VistaActual
        // Como dependemos de inyección de dependencias para DetalleViewModel, 
        // pasamos el anime en el mensaje, y MainViewModel creará el ViewModel a través de DI o de una Factory.
        WeakReferenceMessenger.Default.Send(new NavegarMensaje_Detalle(anime));
    }

    [RelayCommand]
    private void ToggleModoSeleccion()
    {
        ModoSeleccion = !ModoSeleccion;
    }

    /// <summary>Marca todos los animes que se ven con el filtro actual (los ocultos no se tocan).</summary>
    [RelayCommand]
    private void SeleccionarTodos()
    {
        foreach (var anime in (BibliotecaFiltrada?.Cast<AnimeItem>() ?? BibliotecaLocales).ToList()) anime.EstaSeleccionado = true;
        OnPropertyChanged(nameof(SeleccionadosTexto));
    }

    /// <summary>Al salir del modo selección (por el botón o tras "Mover a") no queda nada marcado, tampoco lo que un filtro oculta.</summary>
    partial void OnModoSeleccionChanged(bool value)
    {
        if (!value)
        {
            foreach (var anime in BibliotecaLocales) anime.EstaSeleccionado = false;
        }
        OnPropertyChanged(nameof(SeleccionadosTexto));
    }

    [RelayCommand]
    private async Task CategorizarSeleccionadosAsync(string nuevoEstado)
    {
        // Solo los que se ven: mover también lo que un filtro ocultó después de marcarlo era cambiar animes a ciegas.
        var seleccionados = SeleccionadosVisibles();
        if (seleccionados.Count == 0) return;

        // PERF-06: una sola transacción para el lote de seleccionados.
        foreach (var anime in seleccionados) anime.EstadoUsuario = nuevoEstado;
        await _databaseService.ActualizarAnimesAsync(seleccionados);

        ModoSeleccion = false;
        RefrescarFiltro();

        await EnviarEstadoAAniListAsync(seleccionados, nuevoEstado);
    }

    /// <summary>
    /// Lleva a AniList el estado nuevo (solo el estado: nota, progreso y fechas no se tocan). Antes el cambio se quedaba en
    /// este equipo y la siguiente "Actualizar biblioteca" lo deshacía con lo que dijera AniList.
    /// </summary>
    private async Task EnviarEstadoAAniListAsync(List<AnimeItem> animes, string estado)
    {
        var token = _authService.ObtenerTokenGuardado();
        if (string.IsNullOrEmpty(token)) return;

        int pendientes = 0, sinEnviar = 0;
        foreach (var anime in animes)
        {
            try
            {
                bool enviado = await _animeTrackingService.GuardarEstadoSeguimientoAsync(anime.AniListId, estado, token);
                var copia = await _databaseService.ObtenerSeguimientoLocalAsync(anime.AniListId);
                if (copia == null)
                {
                    // Sin copia del seguimiento no se deja nada pendiente: uno inventado (nota 0, sin fechas) borraría
                    // en AniList la nota real cuando se enviara.
                    if (!enviado) sinEnviar++;
                    continue;
                }

                copia.Estado = estado;
                if (!enviado)
                {
                    copia.Pendiente = true; // lo envía la sincronización cuando vuelva la conexión
                    copia.ModificadoUtc = DateTime.UtcNow;
                    pendientes++;
                }
                await _databaseService.GuardarSeguimientoLocalAsync(copia);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("GaleriaViewModel", $"No se pudo enviar a AniList el estado de {anime.AniListId}: {ex.Message}");
                sinEnviar++;
            }
        }

        if (pendientes == 0 && sinEnviar == 0) return;

        var lineas = new List<string>();
        if (pendientes > 0) lineas.Add(string.Format(LocalizationService.T("Gal_EstadoPendienteMsj"), pendientes));
        if (sinEnviar > 0) lineas.Add(string.Format(LocalizationService.T("Gal_EstadoSinEnviarMsj"), sinEnviar));
        _dialogService.MostrarToast(LocalizationService.T("Gal_EstadoSoloLocalTitulo"), string.Join(" ", lineas), "CloudOffOutline", "#60A5FA");
    }
}
