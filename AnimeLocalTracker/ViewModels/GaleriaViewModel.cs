using System;
using System.Collections.ObjectModel;
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
    IRecipient<EpisodioActualizadoMensaje>
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
    [ObservableProperty]
    private ObservableCollection<string> _generosDisponibles = ["Todos los géneros"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HayFiltrosActivos))]
    [NotifyPropertyChangedFor(nameof(ConteoFiltradosTexto))]
    [NotifyPropertyChangedFor(nameof(CantidadFiltrosAvanzadosActivos))]
    [NotifyPropertyChangedFor(nameof(TieneFiltrosAvanzadosActivos))]
    private string _generoSeleccionado = "Todos los géneros";

    public static readonly string[] OpcionesOrdenacion =
    [
        "Título (A - Z)",
        "Título (Z - A)",
        "Mayor Progreso",
        "Menor Progreso",
        "Más Episodios",
        "Menos Episodios",
        "Más Recientes"
    ];

    public string[] ListaOpcionesOrdenacion => OpcionesOrdenacion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CantidadFiltrosAvanzadosActivos))]
    [NotifyPropertyChangedFor(nameof(TieneFiltrosAvanzadosActivos))]
    private string _criterioOrdenSeleccionado = "Título (A - Z)";

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
        (GeneroSeleccionado != "Todos los géneros" && GeneroSeleccionado != "Todos") ||
        SoloConEpisodiosPendientes ||
        SoloConCarpetaLocal;

    public int CantidadFiltrosAvanzadosActivos
    {
        get
        {
            int count = 0;
            if (!string.IsNullOrWhiteSpace(GeneroSeleccionado) && GeneroSeleccionado != "Todos los géneros" && GeneroSeleccionado != "Todos")
                count++;
            if (CriterioOrdenSeleccionado != "Título (A - Z)")
                count++;
            if (SoloConEpisodiosPendientes)
                count++;
            if (SoloConCarpetaLocal)
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
            if (total == 0) return "0 animes";

            if (BibliotecaFiltrada == null || !HayFiltrosActivos)
            {
                return total == 1 ? "1 anime" : $"{total} animes";
            }

            int visibles = BibliotecaFiltrada.Cast<object>().Count();
            return $"Mostrando {visibles} de {total} animes";
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
        GeneroSeleccionado = "Todos los géneros";
        SoloConEpisodiosPendientes = false;
        SoloConCarpetaLocal = false;
        CriterioOrdenSeleccionado = "Título (A - Z)";

        BibliotecaFiltrada?.Refresh();
        AplicarOrdenacion("Título (A - Z)");
        OnPropertyChanged(nameof(SinResultados));
        OnPropertyChanged(nameof(HayFiltrosActivos));
        OnPropertyChanged(nameof(ConteoFiltradosTexto));
        OnPropertyChanged(nameof(CantidadFiltrosAvanzadosActivos));
        OnPropertyChanged(nameof(TieneFiltrosAvanzadosActivos));
    }
    
    [ObservableProperty] private bool _estaConectado;
    [ObservableProperty] private string _nombreUsuarioAniList = "Usuario";
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
        
        _ = CargarBibliotecaAsync();
    }
    
    public void Receive(UsuarioLogeadoMensaje message)
    {
        _ = CargarPerfilUsuarioAsync();
    }

    public void Receive(UsuarioDesconectadoMensaje message)
    {
        EstaConectado = false;
        NombreUsuarioAniList = "Usuario";
        AvatarUsuarioAniList = null;
    }

    public void Receive(AnimeAñadidoMensaje message)
    {
        // Los handlers del messenger NUNCA deben lanzar: una excepción aquí aborta
        // la entrega del mensaje al resto de receptores suscritos.
        try
        {
            if (!BibliotecaLocales.Any(a => a.AniListId == message.NuevoAnime.AniListId))
            {
                message.NuevoAnime.PortadaImagen = _imageCacheService.ObtenerPortada(message.NuevoAnime.AniListId, message.NuevoAnime.UrlPortada);
                BibliotecaLocales.Add(message.NuevoAnime);
                ActualizarGenerosDisponibles();
                OnPropertyChanged(nameof(BibliotecaVacia));
                OnPropertyChanged(nameof(TotalAnimesBiblioteca));
                OnPropertyChanged(nameof(ConteoFiltradosTexto));

                if (message.NuevoAnime.PortadaImagen == null && !string.IsNullOrWhiteSpace(message.NuevoAnime.UrlPortada))
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var img = await _imageCacheService.ObtenerPortadaAsync(message.NuevoAnime.AniListId, message.NuevoAnime.UrlPortada);
                            if (img != null)
                            {
                                System.Windows.Application.Current?.Dispatcher?.Invoke(() => message.NuevoAnime.PortadaImagen = img);
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

                if (string.IsNullOrEmpty(a.EstadoUsuario) || a.EstadoUsuario == "PLANNING")
                {
                    int episodiosVistos = a.EpisodiosVistos;

                    if (episodiosVistos > 0)
                    {
                        if (a.TotalEpisodios > 0 && episodiosVistos >= a.TotalEpisodios)
                        {
                            a.EstadoUsuario = "COMPLETED";
                        }
                        else
                        {
                            a.EstadoUsuario = "CURRENT";
                        }
                        await _databaseService.ActualizarAnimeAsync(a);
                    }
                    else if (string.IsNullOrEmpty(a.EstadoUsuario))
                    {
                        a.EstadoUsuario = "PLANNING";
                        await _databaseService.ActualizarAnimeAsync(a);
                    }
                }

                // Precarga ultra-rápida desde caché en memoria (0ms en scroll).
                // RND-01: los hits de disco/red se cargan en segundo plano por
                // CargarPortadasFaltantesEnSegundoPlanoAsync para no bloquear la UI.
                a.PortadaImagen = _imageCacheService.ObtenerPortadaEnMemoria(a.AniListId);
            }

            BibliotecaLocales = new ObservableCollection<AnimeItem>(animes);
            ActualizarGenerosDisponibles();

            BibliotecaFiltrada = CollectionViewSource.GetDefaultView(BibliotecaLocales);
            BibliotecaFiltrada.Filter = FiltrarAnime;

            AplicarOrdenacion(CriterioOrdenSeleccionado);

            OnPropertyChanged(nameof(BibliotecaFiltrada));
            OnPropertyChanged(nameof(SinResultados));
            OnPropertyChanged(nameof(HayFiltrosActivos));
            OnPropertyChanged(nameof(ConteoFiltradosTexto));

            _ = CargarPortadasFaltantesEnSegundoPlanoAsync(animes);

            await CargarPerfilUsuarioAsync();
            OnPropertyChanged(nameof(BibliotecaVacia));
        }
        catch (Exception ex)
        {
            // Sin esto, un fallo de BD al arrancar deja la galería vacía y en silencio
            AppLogger.Error("GaleriaViewModel", "Error al cargar la biblioteca", ex);
        }
    }

    private async Task CargarPortadasFaltantesEnSegundoPlanoAsync(IEnumerable<AnimeItem> animes)
    {
        var faltantes = animes.Where(a => a.PortadaImagen == null && !string.IsNullOrWhiteSpace(a.UrlPortada)).ToList();
        if (faltantes.Count == 0) return;

        foreach (var anime in faltantes)
        {
            var img = await _imageCacheService.ObtenerPortadaAsync(anime.AniListId, anime.UrlPortada);
            if (img != null)
            {
                if (System.Windows.Application.Current?.Dispatcher != null && !System.Windows.Application.Current.Dispatcher.CheckAccess())
                {
                    // RND-03: InvokeAsync para no bloquear el hilo de pool contra la UI
                    _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() => anime.PortadaImagen = img);
                }
                else
                {
                    anime.PortadaImagen = img;
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
                    "Qué veo hoy",
                    "No tienes animes con carpeta local en la biblioteca.\n\nAgrega un anime con su carpeta de episodios para usar esta función.",
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
                    "Qué veo hoy",
                    "No se encontraron episodios sin ver en tu biblioteca local. ¡Disfruta de tu maratón!",
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
                EpisodiosDisponibles: seleccion.TodosDisponibles
            ));
        }
        catch (Exception ex)
        {
            EstaBuscandoQueVer = false;
            AppLogger.Error("GaleriaViewModel", "Error en Qué veo hoy", ex);
            await _dialogService.MostrarDialogoAsync(
                "Qué veo hoy", "Ocurrió un error al buscar episodios.", false, "AlertCircleOutline", "#E53935");
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

        var nuevaLista = new List<string> { "Todos los géneros" };
        nuevaLista.AddRange(generosUnicos);

        string prevSeleccion = GeneroSeleccionado;
        GenerosDisponibles = new ObservableCollection<string>(nuevaLista);

        if (nuevaLista.Contains(prevSeleccion))
        {
            GeneroSeleccionado = prevSeleccion;
        }
        else
        {
            GeneroSeleccionado = "Todos los géneros";
        }
    }

    public void AplicarOrdenacion(string? criterio = null)
    {
        if (BibliotecaFiltrada == null) return;
        criterio ??= CriterioOrdenSeleccionado;

        using (BibliotecaFiltrada.DeferRefresh())
        {
            BibliotecaFiltrada.SortDescriptions.Clear();
            switch (criterio)
            {
                case "Título (A - Z)":
                    BibliotecaFiltrada.SortDescriptions.Add(new SortDescription(nameof(AnimeItem.Titulo), ListSortDirection.Ascending));
                    break;
                case "Título (Z - A)":
                    BibliotecaFiltrada.SortDescriptions.Add(new SortDescription(nameof(AnimeItem.Titulo), ListSortDirection.Descending));
                    break;
                case "Mayor Progreso":
                    BibliotecaFiltrada.SortDescriptions.Add(new SortDescription(nameof(AnimeItem.ProgresoPorcentaje), ListSortDirection.Descending));
                    BibliotecaFiltrada.SortDescriptions.Add(new SortDescription(nameof(AnimeItem.Titulo), ListSortDirection.Ascending));
                    break;
                case "Menor Progreso":
                    BibliotecaFiltrada.SortDescriptions.Add(new SortDescription(nameof(AnimeItem.ProgresoPorcentaje), ListSortDirection.Ascending));
                    BibliotecaFiltrada.SortDescriptions.Add(new SortDescription(nameof(AnimeItem.Titulo), ListSortDirection.Ascending));
                    break;
                case "Más Episodios":
                    BibliotecaFiltrada.SortDescriptions.Add(new SortDescription(nameof(AnimeItem.TotalEpisodios), ListSortDirection.Descending));
                    BibliotecaFiltrada.SortDescriptions.Add(new SortDescription(nameof(AnimeItem.Titulo), ListSortDirection.Ascending));
                    break;
                case "Menos Episodios":
                    BibliotecaFiltrada.SortDescriptions.Add(new SortDescription(nameof(AnimeItem.TotalEpisodios), ListSortDirection.Ascending));
                    BibliotecaFiltrada.SortDescriptions.Add(new SortDescription(nameof(AnimeItem.Titulo), ListSortDirection.Ascending));
                    break;
                case "Más Recientes":
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
            GeneroSeleccionado != "Todos los géneros" && 
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
                NombreUsuarioAniList = perfil.Name ?? "Usuario";
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
        bool exito = await _authService.IniciarSesionAsync();
        if (exito)
        {
            await _dialogService.MostrarDialogoAsync("Nube Activada", "¡Conectado a AniList exitosamente! Tu progreso ahora se sincronizará.", false, "CloudCheck", "#4CAF50");
        }
        else
        {
            await _dialogService.MostrarDialogoAsync("Autenticación Cancelada", "No se pudo iniciar sesión con AniList o el proceso fue cancelado.", false, "AlertCircle", "#FF5252");
        }
    }

    [RelayCommand]
    private async Task DesconectarAniListAsync()
    {
        MenuUsuarioAbierto = false; // Cierra el menú antes de desloguear
        _authService.CerrarSesion();
        await _dialogService.MostrarDialogoAsync("Sesión Cerrada", "Te has desconectado de AniList correctamente.", false, "Logout", "#FF5252");
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

        foreach (var anime in listaAnimes)
        {
            ProgresoActual++;
            TextoProgreso = $"Sincronizando: {anime.Titulo} ({ProgresoActual}/{ProgresoTotal})";

            var datosFrescos = await _animeTrackingService.ObtenerAnimePorIdAsync(anime.AniListId);
            if (datosFrescos != null)
            {
                int episodiosEmitidos = datosFrescos.NextAiringEpisode != null 
                    ? datosFrescos.NextAiringEpisode.Episode - 1 
                    : (datosFrescos.Episodes ?? 0);
                
                anime.TotalEpisodios = episodiosEmitidos;
                anime.Estado = datosFrescos.Status ?? "UNKNOWN";
                
                // Si está conectado, sincronizamos también el estado personal del usuario
                if (EstaConectado)
                {
                    var token = _authService.ObtenerTokenGuardado();
                    if (!string.IsNullOrEmpty(token))
                    {
                        var seguimiento = await _animeTrackingService.ObtenerSeguimientoUsuarioAsync(anime.AniListId, token);
                        if (seguimiento != null && !string.IsNullOrEmpty(seguimiento.Status))
                        {
                            anime.EstadoUsuario = seguimiento.Status;
                        }
                    }
                }
                
                await _databaseService.ActualizarAnimeAsync(anime);
            }
            
            await Task.Delay(250); 
        }
        
        TextoProgreso = "¡Actualización completada con éxito!";
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

        foreach (var anime in seleccionados)
        {
            anime.EstadoUsuario = nuevoEstado;
            await _databaseService.ActualizarAnimeAsync(anime);
            anime.EstaSeleccionado = false;
        }

        ModoSeleccion = false;
        BibliotecaFiltrada?.Refresh();
        OnPropertyChanged(nameof(SinResultados));
    }
}
