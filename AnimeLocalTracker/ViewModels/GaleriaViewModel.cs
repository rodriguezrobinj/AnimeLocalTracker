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

public partial class GaleriaViewModel : ObservableObject,
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
    private readonly IFileScannerService _fileScannerService;
    
    public bool BibliotecaVacia => BibliotecaLocales.Count == 0;

    public bool SinResultados => BibliotecaLocales.Count > 0 && (BibliotecaFiltrada?.IsEmpty ?? false);

    public int TotalAnimesBiblioteca => BibliotecaLocales.Count;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BibliotecaVacia))]
    [NotifyPropertyChangedFor(nameof(SinResultados))]
    [NotifyPropertyChangedFor(nameof(TotalAnimesBiblioteca))]
    [NotifyPropertyChangedFor(nameof(SePuedeAyudarAverQueVer))]
    [NotifyPropertyChangedFor(nameof(ConteoFiltradosTexto))]
    [NotifyCanExecuteChangedFor(nameof(ElegirQueVerHoyCommand))]
    private ObservableCollection<AnimeItem> _bibliotecaLocales = [];

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
    [NotifyPropertyChangedFor(nameof(HayFiltrosActivos))]
    [NotifyPropertyChangedFor(nameof(ConteoFiltradosTexto))]
    private string _filtroEstado = "Todos"; // Todos, Viendo, Completados, Planeando

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
    public static string TodasLasTemporadas => LocalizationService.T("Gal_TodasLasTemporadas");
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
        LocalizationService.T("Gal_OrdenMasRecientes")
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

    public double UltimoScrollOffset { get; set; } = 0;

    partial void OnTextoBusquedaChanged(string value)
    {
        BibliotecaFiltrada?.Refresh();
        OnPropertyChanged(nameof(SinResultados));
        OnPropertyChanged(nameof(ConteoFiltradosTexto));
    }

    partial void OnGeneroSeleccionadoChanged(string value)
    {
        BibliotecaFiltrada?.Refresh();
        OnPropertyChanged(nameof(SinResultados));
        OnPropertyChanged(nameof(ConteoFiltradosTexto));
    }

    partial void OnTemporadaSeleccionadaChanged(string value)
    {
        BibliotecaFiltrada?.Refresh();
        OnPropertyChanged(nameof(SinResultados));
        OnPropertyChanged(nameof(ConteoFiltradosTexto));
    }

    partial void OnAnioSeleccionadoChanged(string value)
    {
        BibliotecaFiltrada?.Refresh();
        OnPropertyChanged(nameof(SinResultados));
        OnPropertyChanged(nameof(ConteoFiltradosTexto));
    }

    partial void OnSoloFavoritosChanged(bool value)
    {
        BibliotecaFiltrada?.Refresh();
        OnPropertyChanged(nameof(SinResultados));
        OnPropertyChanged(nameof(ConteoFiltradosTexto));
    }

    partial void OnCriterioOrdenSeleccionadoChanged(string value)
    {
        AplicarOrdenacion(value);
    }

    partial void OnSoloConEpisodiosPendientesChanged(bool value)
    {
        BibliotecaFiltrada?.Refresh();
        OnPropertyChanged(nameof(SinResultados));
        OnPropertyChanged(nameof(ConteoFiltradosTexto));
    }

    partial void OnSoloConCarpetaLocalChanged(bool value)
    {
        BibliotecaFiltrada?.Refresh();
        OnPropertyChanged(nameof(SinResultados));
        OnPropertyChanged(nameof(ConteoFiltradosTexto));
    }

    [RelayCommand]
    private void CambiarFiltroEstado(string nuevoFiltro)
    {
        FiltroEstado = nuevoFiltro;
        BibliotecaFiltrada?.Refresh();
        OnPropertyChanged(nameof(SinResultados));
        OnPropertyChanged(nameof(ConteoFiltradosTexto));
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
        BibliotecaFiltrada?.Refresh();
        OnPropertyChanged(nameof(ConteoFiltradosTexto));
    }

    [ObservableProperty] private bool _estaConectado;
    [ObservableProperty] private string _nombreUsuarioAniList = LocalizationService.T("Gal_UsuarioDefault");
    [ObservableProperty] private string? _avatarUsuarioAniList;
    
    [ObservableProperty] private bool _estaActualizando;
    [ObservableProperty] private int _progresoTotal;
    [ObservableProperty] private int _progresoActual;
    [ObservableProperty] private string _textoProgreso = string.Empty;

    // --- PROPIEDADES DE SELECCIÓN MÚLTIPLE ---
    [ObservableProperty] private bool _modoSeleccion;

    public GaleriaViewModel(
        IAnimeTrackingService animeTrackingService, 
        IDatabaseService databaseService, 
        IAuthService authService, 
        IDialogService dialogService, 
        IHttpClientFactory httpClientFactory,
        IImageCacheService imageCacheService,
        IFileScannerService? fileScannerService = null)
    {
        _animeTrackingService = animeTrackingService;
        _databaseService = databaseService;
        _authService = authService;
        _dialogService = dialogService;
        _httpClientFactory = httpClientFactory;
        _imageCacheService = imageCacheService;
        _fileScannerService = fileScannerService ?? new FileScannerService();
        
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
                message.NuevoAnime.NotificarPortadaActualizada();
                BibliotecaLocales.Add(message.NuevoAnime);
                ActualizarGenerosDisponibles();
                ActualizarTemporadasYAniosDisponibles();
                OnPropertyChanged(nameof(BibliotecaVacia));
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
        try
        {
            var anime = BibliotecaLocales.FirstOrDefault(a => a.AniListId == message.AnimeId);
            if (anime != null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var registros = await _databaseService.ObtenerRegistrosPorAnimeAsync(anime.AniListId);
                        int vistos = registros.Count(r => r.VistoLocal);
                        var dispatcher = System.Windows.Application.Current?.Dispatcher;
                        if (dispatcher != null && !dispatcher.HasShutdownStarted)
                        {
                            _ = dispatcher.InvokeAsync(() => anime.EpisodiosVistos = vistos);
                        }
                    }
                    catch { }
                });
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("GaleriaViewModel", "Error al actualizar episodio visto en galería", ex);
        }
    }
    
    private async Task CargarBibliotecaAsync()
    {
        try
        {
            var animes = await _databaseService.ObtenerTodosLosAnimesAsync() ?? new List<Models.AnimeItem>();
            var todosRegistros = await _databaseService.ObtenerTodosLosRegistrosAsync() ?? new List<Models.RegistroEpisodio>();
            var registrosPorAnime = todosRegistros.GroupBy(r => r.AniListId)
                                                  .ToDictionary(g => g.Key, g => g.ToList());

            // MIGRACIÓN INTELIGENTE: Recuperar el estado basado en lo que realmente has visto localmente
            foreach (var a in animes)
            {
                if (registrosPorAnime.TryGetValue(a.AniListId, out var registros))
                {
                    a.EpisodiosVistos = registros.Count(r => r.VistoLocal);
                }
                else
                {
                    a.EpisodiosVistos = 0;
                }

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

                // Precarga ultra-rápida desde caché en memoria (0ms en scroll).
                // RND-01: los hits de disco/red se cargan en segundo plano por
                // CargarPortadasFaltantesEnSegundoPlanoAsync para no bloquear la UI.
                // a.PortadaImagen fue reemplazado por AnimeCoverMultiConverter.
                a.ResolverPortadaLocal();
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

            _ = CargarPortadasFaltantesEnSegundoPlanoAsync(animes);
            _ = CargarTemporadasYAniosFaltantesEnSegundoPlanoAsync(animes);

            await CargarPerfilUsuarioAsync();
            OnPropertyChanged(nameof(BibliotecaVacia));
        }
        catch (Exception ex)
        {
            // Sin esto, un fallo de BD al arrancar deja la galería vacía y en silencio
            AppLogger.Error("GaleriaViewModel", "Error al cargar la biblioteca", ex);
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

    private async Task CargarPortadasFaltantesEnSegundoPlanoAsync(IEnumerable<AnimeItem> animes)
    {
        var faltantes = animes.Where(a => _imageCacheService.ObtenerPortadaEnMemoria(a.AniListId) == null && !string.IsNullOrWhiteSpace(a.UrlPortada)).ToList();
        if (faltantes.Count == 0) return;

        foreach (var anime in faltantes)
        {
            var img = await _imageCacheService.ObtenerPortadaAsync(anime.AniListId, anime.UrlPortada);
            if (img != null)
            {
                if (System.Windows.Application.Current?.Dispatcher != null && !System.Windows.Application.Current.Dispatcher.CheckAccess())
                {
                    // RND-03: InvokeAsync para no bloquear el hilo de pool contra la UI
                    _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() => anime.NotificarPortadaActualizada());
                }
                else
                {
                    anime.NotificarPortadaActualizada();
                }
            }
        }
    }

    // ── "QUÉ VEO HOY": episodio no visto aleatorio de la biblioteca ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SePuedeAyudarAverQueVer))]
    [NotifyCanExecuteChangedFor(nameof(ElegirQueVerHoyCommand))]
    private bool _estaBuscandoQueVer;

    public bool SePuedeAyudarAverQueVer => !EstaBuscandoQueVer && !BibliotecaVacia;
    /// <summary>
    /// "Qué veo hoy": elige un anime al azar entre los que tienen episodios locales pendientes
    /// (priorizando animes en curso "CURRENT") y reproduce su SIGUIENTE episodio no visto
    /// en orden cronológico para mantener la continuidad de la trama.
    /// </summary>
    [RelayCommand(CanExecute = nameof(SePuedeAyudarAverQueVer))]
    private async Task ElegirQueVerHoyAsync()
    {
        if (EstaBuscandoQueVer) return;
        EstaBuscandoQueVer = true;

        try
        {
            var animes = BibliotecaLocales
                .Where(a => !string.IsNullOrWhiteSpace(a.RutaCarpeta))
                .ToList();

            if (animes.Count == 0)
            {
                EstaBuscandoQueVer = false;
                await _dialogService.MostrarDialogoAsync(
                    LocalizationService.T("Gal_QueVeoHoyTitulo"),
                    LocalizationService.T("Gal_QueVeoHoySinCarpetaMsj"),
                    false, "Dice", "#60A5FA");
                return;
            }

            // Escaneo en hilo de fondo para no bloquear la UI
            var elegido = await Task.Run(async () =>
            {
                var enCurso = animes.Where(a =>
                    !string.IsNullOrEmpty(a.EstadoUsuario) && a.EstadoUsuario == "CURRENT").ToList();
                var candidatos = enCurso.Count > 0 ? enCurso : animes;

                var animesConSiguienteEpisodio = new List<(AnimeItem Anime, EpisodioItem SiguienteEpisodio, List<EpisodioItem> TodosDisponibles)>();

                foreach (var anime in candidatos)
                {
                    try
                    {
                        var episodios = (await _fileScannerService.EscanearEpisodiosAsync(anime.RutaCarpeta!))
                            .Where(e => !string.IsNullOrWhiteSpace(e.RutaCompleta))
                            .Where(e => e.NumeroEpisodio > 0) // FUN-004: sin número no es candidato
                            .OrderBy(e => e.NumeroEpisodio)
                            .ToList();

                        if (episodios.Count == 0) continue;

                        var registros = await _databaseService.ObtenerRegistrosPorAnimeAsync(anime.AniListId);
                        var vistos = new HashSet<int>(registros.Where(r => r.VistoLocal).Select(r => r.NumeroEpisodio));

                        var siguiente = episodios.FirstOrDefault(ep => !vistos.Contains(ep.NumeroEpisodio));
                        if (siguiente != null)
                        {
                            animesConSiguienteEpisodio.Add((anime, siguiente, episodios));
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Debug("GaleriaViewModel", $"Qué veo hoy: error escaneando {anime.Titulo}: {ex.Message}");
                    }
                }

                // Fallback a toda la biblioteca si los de en curso ya fueron vistos por completo
                if (animesConSiguienteEpisodio.Count == 0 && enCurso.Count > 0 && candidatos == enCurso)
                {
                    var otrosAnimes = animes.Where(a => a.EstadoUsuario != "CURRENT").ToList();
                    foreach (var anime in otrosAnimes)
                    {
                        try
                        {
                            var episodios = (await _fileScannerService.EscanearEpisodiosAsync(anime.RutaCarpeta!))
                                .Where(e => !string.IsNullOrWhiteSpace(e.RutaCompleta))
                                .OrderBy(e => e.NumeroEpisodio)
                                .ToList();

                            if (episodios.Count == 0) continue;

                            var registros = await _databaseService.ObtenerRegistrosPorAnimeAsync(anime.AniListId);
                            var vistos = new HashSet<int>(registros.Where(r => r.VistoLocal).Select(r => r.NumeroEpisodio));

                            var siguiente = episodios.FirstOrDefault(ep => !vistos.Contains(ep.NumeroEpisodio));
                            if (siguiente != null)
                            {
                                animesConSiguienteEpisodio.Add((anime, siguiente, episodios));
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Debug("GaleriaViewModel", $"Qué veo hoy: error escaneando {anime.Titulo}: {ex.Message}");
                        }
                    }
                }

                if (animesConSiguienteEpisodio.Count == 0)
                    return ((AnimeItem Anime, EpisodioItem SiguienteEpisodio, List<EpisodioItem> TodosDisponibles)?)null;

                var random = new Random();
                return animesConSiguienteEpisodio[random.Next(animesConSiguienteEpisodio.Count)];
            });

            EstaBuscandoQueVer = false;

            if (elegido == null)
            {
                await _dialogService.MostrarDialogoAsync(
                    LocalizationService.T("Gal_QueVeoHoyTitulo"),
                    LocalizationService.T("Gal_QueVeoHoySinEpisodiosMsj"),
                    false, "EmoticonHappyOutline", "#4CAF50");
                return;
            }

            var seleccion = elegido.Value;

            // Navegar al reproductor en el hilo principal de la UI
            WeakReferenceMessenger.Default.Send(new NavegarMensaje_Reproductor(
                seleccion.SiguienteEpisodio.RutaCompleta,
                seleccion.Anime.AniListId,
                seleccion.Anime.Titulo,
                seleccion.SiguienteEpisodio.NumeroEpisodio,
                EpisodiosDisponibles: seleccion.TodosDisponibles,
                RutaPortada: seleccion.Anime.PortadaVisible
            ));
        }
        catch (Exception ex)
        {
            EstaBuscandoQueVer = false;
            AppLogger.Error("GaleriaViewModel", "Error en Qué veo hoy", ex);
            await _dialogService.MostrarDialogoAsync(
                LocalizationService.T("Gal_QueVeoHoyTitulo"), LocalizationService.T("Gal_QueVeoHoyErrorMsj"), false, "AlertCircleOutline", "#E53935");
        }
        finally
        {
            EstaBuscandoQueVer = false;
        }
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

    private static string TemporadaATexto(string codigo) => codigo switch
    {
        "WINTER" => LocalizationService.T("Temporada_Invierno"),
        "SPRING" => LocalizationService.T("Temporada_Primavera"),
        "SUMMER" => LocalizationService.T("Temporada_Verano"),
        "FALL" => LocalizationService.T("Temporada_Otonio"),
        _ => codigo
    };

    public void ActualizarTemporadasYAniosDisponibles()
    {
        var temporadasUnicas = BibliotecaLocales
            .Where(a => !string.IsNullOrWhiteSpace(a.Temporada))
            .Select(a => a.Temporada)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var nuevaListaTemporadas = new List<string> { TodasLasTemporadas };
        nuevaListaTemporadas.AddRange(
            OrdenTemporadas.Where(t => temporadasUnicas.Contains(t, StringComparer.OrdinalIgnoreCase))
                           .Select(TemporadaATexto));

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
                case 6: // Más Recientes
                    BibliotecaFiltrada.SortDescriptions.Add(new SortDescription(nameof(AnimeItem.AniListId), ListSortDirection.Descending));
                    break;
                default:
                    BibliotecaFiltrada.SortDescriptions.Add(new SortDescription(nameof(AnimeItem.Titulo), ListSortDirection.Ascending));
                    break;
            }
        }
    }

    private bool FiltrarAnime(object obj)
    {
        if (obj is not AnimeItem anime) return false;

        // 1. Filtro por texto (título o nombres alternativos)
        if (!string.IsNullOrWhiteSpace(TextoBusqueda))
        {
            bool coincideTitulo = anime.Titulo.Contains(TextoBusqueda, StringComparison.OrdinalIgnoreCase);
            bool coincideAlt = !string.IsNullOrWhiteSpace(anime.NombresAlternativos) && 
                               anime.NombresAlternativos.Contains(TextoBusqueda, StringComparison.OrdinalIgnoreCase);
            if (!coincideTitulo && !coincideAlt)
            {
                return false;
            }
        }

        // 2. Filtro por estado
        if (FiltroEstado != "Todos")
        {
            string estadoEsperado = FiltroEstado switch
            {
                "Viendo" => "CURRENT",
                "Completados" => "COMPLETED",
                "Planeando" => "PLANNING",
                _ => ""
            };
            
            if (!string.IsNullOrEmpty(estadoEsperado) && anime.EstadoUsuario != estadoEsperado)
            {
                return false;
            }
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
            if (anime.TemporadaVisual != TemporadaSeleccionada)
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
        WeakReferenceMessenger.Default.Send(new NavegarMensaje_AgregarAnime());
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

        bool confirmacion = await _dialogService.MostrarDialogoAsync(
            LocalizationService.T("Bib_EliminarTitulo"),
            string.Format(LocalizationService.T("Bib_EliminarMsj"), anime.Titulo),
            true, "HeartBrokenOutline", "#EF4444");

        if (!confirmacion) return;

        // Opción extra: borrar también los archivos del disco (mismo flujo que DetalleViewModel)
        bool borrarArchivos = await _dialogService.MostrarDialogoAsync(
            LocalizationService.T("Bib_BorrarArchivosTitulo"),
            string.Format(LocalizationService.T("Bib_BorrarArchivosMsj"), string.IsNullOrWhiteSpace(anime.RutaCarpeta) ? LocalizationService.T("Bib_SinCarpetaLocal") : anime.RutaCarpeta),
            true, "FolderOutline", "#EF4444");

        string? carpeta = anime.RutaCarpeta;

        await _databaseService.EliminarAnimeAsync(anime);
        BibliotecaLocales.Remove(anime);
        ActualizarGenerosDisponibles();
        ActualizarTemporadasYAniosDisponibles();
        OnPropertyChanged(nameof(BibliotecaVacia));
        OnPropertyChanged(nameof(TotalAnimesBiblioteca));
        OnPropertyChanged(nameof(ConteoFiltradosTexto));

        if (borrarArchivos && !string.IsNullOrWhiteSpace(carpeta) && Directory.Exists(carpeta))
        {
            try
            {
                Directory.Delete(carpeta, recursive: true);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("GaleriaViewModel", $"No se pudo borrar la carpeta del anime: {ex.Message}");
            }
        }
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
        ProgresoTotal = listaAnimes.Count;
        ProgresoActual = 0;
        TextoProgreso = LocalizationService.T("Gal_ConsultandoAniList");

        // RND-02: una consulta loteada (50 animes por request) reemplaza las ~300
        // llamadas seriales (anime + seguimiento por separado) con Task.Delay(250).
        var token = EstaConectado ? _authService.ObtenerTokenGuardado() : null;
        var datosLote = await _animeTrackingService.ObtenerAnimesPorIdsLoteAsync(
            listaAnimes.Select(a => a.AniListId), token);

        int procesados = 0;
        var modificados = new List<Models.AnimeItem>();
        foreach (var anime in listaAnimes)
        {
            procesados++;
            ProgresoActual = procesados;
            TextoProgreso = string.Format(LocalizationService.T("Gal_SincronizandoFormato"), anime.Titulo, procesados, ProgresoTotal);

            if (!datosLote.TryGetValue(anime.AniListId, out var datosFrescos)) continue;

            int episodiosEmitidos = datosFrescos.NextAiringEpisode != null
                ? datosFrescos.NextAiringEpisode.Episode - 1
                : (datosFrescos.Episodes ?? 0);

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
            if (token != null && datosFrescos.MediaListEntry != null && !string.IsNullOrEmpty(datosFrescos.MediaListEntry.Status))
            {
                if (!string.Equals(anime.EstadoUsuario, datosFrescos.MediaListEntry.Status, StringComparison.Ordinal))
                {
                    anime.EstadoUsuario = datosFrescos.MediaListEntry.Status;
                    cambio = true;
                }
            }

            if (cambio) modificados.Add(anime);
        }

        if (modificados.Count > 0)
        {
            await _databaseService.ActualizarAnimesAsync(modificados);
            ActualizarTemporadasYAniosDisponibles();
            BibliotecaFiltrada?.Refresh();
        }

        TextoProgreso = LocalizationService.T("Gal_ActualizacionCompletada");
        await Task.Delay(2000); 
        EstaActualizando = false;
    }

    [RelayCommand]
    private void AbrirDetalle(AnimeItem anime)
    {
        if (ModoSeleccion)
        {
            anime.EstaSeleccionado = !anime.EstaSeleccionado;
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
        if (!ModoSeleccion)
        {
            // Deseleccionar todos al salir del modo
            foreach (var anime in BibliotecaLocales)
            {
                anime.EstaSeleccionado = false;
            }
        }
    }

    [RelayCommand]
    private async Task CategorizarSeleccionadosAsync(string nuevoEstado)
    {
        var seleccionados = BibliotecaLocales.Where(a => a.EstaSeleccionado).ToList();
        if (seleccionados.Count == 0) return;

        // PERF-06: una sola transacción para el lote de seleccionados.
        foreach (var anime in seleccionados)
        {
            anime.EstadoUsuario = nuevoEstado;
            anime.EstaSeleccionado = false;
        }
        await _databaseService.ActualizarAnimesAsync(seleccionados);

        ModoSeleccion = false;
        BibliotecaFiltrada?.Refresh();
        OnPropertyChanged(nameof(SinResultados));
    }
}
