using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Messages;

namespace AnimeLocalTracker.ViewModels;

public partial class MainViewModel : ObservableObject, 
    IRecipient<NavegarMensaje_Galeria>,
    IRecipient<NavegarMensaje_Detalle>,
    IRecipient<NavegarMensaje_Calendario>,
    IRecipient<NavegarMensaje_Descargas>,
    IRecipient<AbrirBuscadorMensaje>,
    IRecipient<MostrarDialogoRequestMessage>,
    IRecipient<NavegarMensaje_Reproductor>,
    IRecipient<NavegarMensaje_VolverDelReproductor>,
    IRecipient<DescargaProgresoMensaje>
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IAnimeTrackingService _animeTrackingService;
    private readonly IDatabaseService _databaseService;
    private readonly IDownloadService _downloadService;

    // === NAVEGACIÓN (ViewModel-First) ===
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EsGaleriaActiva))]
    [NotifyPropertyChangedFor(nameof(EsCalendarioActivo))]
    [NotifyPropertyChangedFor(nameof(EsDescargasActivas))]
    private ObservableObject _vistaActual = null!;

    public bool EsGaleriaActiva => VistaActual is GaleriaViewModel || VistaActual is DetalleViewModel;
    public bool EsCalendarioActivo => VistaActual is CalendarioViewModel;
    public bool EsDescargasActivas => VistaActual is DescargasViewModel;

    // === BADGE DE DESCARGAS ===
    [ObservableProperty]
    private int _conteoDescargasActivas;

    [ObservableProperty]
    private bool _tieneDescargasActivas;

    // === DIÁLOGOS CUSTOM ===
    [ObservableProperty] private bool _dialogoVisible;
    [ObservableProperty] private string _dialogoTitulo = "";
    [ObservableProperty] private string _dialogoMensaje = "";
    [ObservableProperty] private bool _dialogoEsConfirmacion;
    [ObservableProperty] private string _dialogoIcono = "InformationOutline";
    [ObservableProperty] private string _dialogoColor = "#3F51B5";
    
    private TaskCompletionSource<bool>? _dialogTcs;

    // === TOAST NOTIFICATIONS ===
    [ObservableProperty] private bool _toastVisible;
    [ObservableProperty] private string _toastTitulo = "";
    [ObservableProperty] private string _toastMensaje = "";
    [ObservableProperty] private string _toastIcono = "InformationOutline";
    [ObservableProperty] private string _toastColor = "#3F51B5";

    // === BUSCADOR FLOTANTE ===
    [ObservableProperty] private bool _isDialogOpen;
    [ObservableProperty] private ObservableCollection<AniListMedia> _resultadosBusqueda = [];
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private bool _busquedaSinResultados;
    private System.Threading.CancellationTokenSource? _searchCts;
    
    private string _textoBusqueda = string.Empty;
    public string TextoBusqueda
    {
        get => _textoBusqueda;
        set
        {
            SetProperty(ref _textoBusqueda, value);
            BusquedaSinResultados = false; // Resetear al escribir
            EjecutarBusquedaEnVivoAsync(value);
        }
    }

    public MainViewModel(
        IServiceProvider serviceProvider, 
        IAnimeTrackingService animeTrackingService, 
        IDatabaseService databaseService,
        IDownloadService downloadService)
    {
        _serviceProvider = serviceProvider;
        _animeTrackingService = animeTrackingService;
        _databaseService = databaseService;
        _downloadService = downloadService;

        WeakReferenceMessenger.Default.RegisterAll(this);

        // Cargamos la vista inicial
        VistaActual = _serviceProvider.GetRequiredService<GaleriaViewModel>();
        ActualizarConteoDescargas();
    }

    public void Receive(DescargaProgresoMensaje message)
    {
        ActualizarConteoDescargas();
    }

    private void ActualizarConteoDescargas()
    {
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            var activas = _downloadService.ObtenerDescargasActivas();
            ConteoDescargasActivas = System.Linq.Enumerable.Count(activas, d => d.IsDownloading);
            TieneDescargasActivas = ConteoDescargasActivas > 0;
        });
    }

    // ==========================================
    // RECEPTORES DE MENSAJES
    // ==========================================
    public void Receive(NavegarMensaje_Galeria message)
    {
        VistaActual = _serviceProvider.GetRequiredService<GaleriaViewModel>();
    }

    public async void Receive(NavegarMensaje_Detalle message)
    {
        var detalleVm = _serviceProvider.GetRequiredService<DetalleViewModel>();
        VistaActual = detalleVm;
        await detalleVm.InicializarAsync(message.AnimeSeleccionado);
    }

    public void Receive(NavegarMensaje_Calendario message)
    {
        VistaActual = _serviceProvider.GetRequiredService<CalendarioViewModel>();
    }

    public void Receive(NavegarMensaje_Descargas message)
    {
        VistaActual = _serviceProvider.GetRequiredService<DescargasViewModel>();
    }

    public void Receive(NavegarMensaje_Reproductor message)
    {
        // Guardar la vista actual antes de navegar al reproductor
        _vistaAnteriorAlReproductor = VistaActual;

        var viewModel = _serviceProvider.GetRequiredService<ReproductorViewModel>();
        viewModel.CargarVideo(message.RutaVideo, message.AnimeId, message.TituloAnime, message.Episodio, message.EpisodiosDisponibles);
        VistaActual = viewModel;
    }

    // Vista a la que volver al salir del reproductor
    private ObservableObject? _vistaAnteriorAlReproductor;

    public void Receive(NavegarMensaje_VolverDelReproductor message)
    {
        if (_vistaAnteriorAlReproductor != null)
        {
            VistaActual = _vistaAnteriorAlReproductor;
            _vistaAnteriorAlReproductor = null;
        }
        else
        {
            VistaActual = _serviceProvider.GetRequiredService<GaleriaViewModel>();
        }
    }

    [RelayCommand]
    private void NavegarGaleria()
    {
        VistaActual = _serviceProvider.GetRequiredService<GaleriaViewModel>();
    }

    [RelayCommand]
    private void NavegarCalendario()
    {
        VistaActual = _serviceProvider.GetRequiredService<CalendarioViewModel>();
    }

    [RelayCommand]
    private void NavegarDescargas()
    {
        VistaActual = _serviceProvider.GetRequiredService<DescargasViewModel>();
    }

    public void Receive(AbrirBuscadorMensaje message)
    {
        TextoBusqueda = string.Empty;
        ResultadosBusqueda.Clear();
        IsDialogOpen = true;
    }

    public void Receive(MostrarDialogoRequestMessage message)
    {
        // Respondemos a la petición asíncrona enviada por otros ViewModels
        message.Reply(MostrarDialogoLocalAsync(message.Titulo, message.Mensaje, message.EsConfirmacion, message.Icono, message.Color));
    }

    // ==========================================
    // LÓGICA DE DIÁLOGOS
    // ==========================================
    private async Task<bool> MostrarDialogoLocalAsync(string titulo, string mensaje, bool esConfirmacion, string icono, string color)
    {
        if (!esConfirmacion)
        {
            // Si no es confirmación, mostrar una notificación (Toast) residual
            ToastTitulo = titulo;
            ToastMensaje = mensaje;
            ToastIcono = icono;
            ToastColor = color;
            ToastVisible = true;
            
            // Ocultar automáticamente después de 3.5 segundos
            _ = Task.Run(async () => 
            {
                await Task.Delay(3500);
                System.Windows.Application.Current.Dispatcher.Invoke(() => ToastVisible = false);
            });
            
            // Retorna true automáticamente porque no requiere la interacción del usuario
            return true;
        }

        // Si es confirmación, mostrar el diálogo bloqueante estándar
        DialogoTitulo = titulo;
        DialogoMensaje = mensaje;
        DialogoEsConfirmacion = esConfirmacion;
        DialogoIcono = icono;
        DialogoColor = color;
        
        DialogoVisible = true;
        
        _dialogTcs = new TaskCompletionSource<bool>();
        return await _dialogTcs.Task;
    }

    [RelayCommand]
    private void AceptarDialogo()
    {
        DialogoVisible = false;
        _dialogTcs?.TrySetResult(true);
    }

    [RelayCommand]
    private void CancelarDialogo()
    {
        DialogoVisible = false;
        _dialogTcs?.TrySetResult(false);
    }
    
    [RelayCommand]
    private void CerrarDialogoBusqueda()
    {
        IsDialogOpen = false;
    }

    // ==========================================
    // LÓGICA DE BÚSQUEDA Y CREACIÓN DE ANIME
    // ==========================================
    private async void EjecutarBusquedaEnVivoAsync(string busqueda)
    {
        if (string.IsNullOrWhiteSpace(busqueda) || busqueda.Length < 3)
        {
            ResultadosBusqueda.Clear();
            IsSearching = false;
            BusquedaSinResultados = false;
            return;
        }

        _searchCts?.Cancel();
        _searchCts = new System.Threading.CancellationTokenSource();
        var token = _searchCts.Token;

        try
        {
            IsSearching = true;
            await Task.Delay(500, token); 
            
            if (!token.IsCancellationRequested)
            {
                var resultados = await _animeTrackingService.BuscarAnimesEnVivoAsync(busqueda);
                ResultadosBusqueda.Clear();
                foreach (var r in resultados) ResultadosBusqueda.Add(r);
                
                BusquedaSinResultados = ResultadosBusqueda.Count == 0;
            }
        }
        catch (TaskCanceledException ex)
        {
            AppLogger.Debug("MainViewModel", $"Búsqueda en vivo cancelada por nuevo término: {ex.Message}");
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsSearching = false;
            }
        }
    }

    [RelayCommand]
    private async Task SeleccionarYCrearAnimeAsync(AniListMedia animeAPI)
    {
        if (animeAPI?.Title?.Romaji == null) return;

        // Validar si el anime ya existe en la biblioteca
        var animesGuardados = await _databaseService.ObtenerTodosLosAnimesAsync();
        if (animesGuardados.Any(a => a.AniListId == animeAPI.Id))
        {
            IsDialogOpen = false;
            await Task.Delay(250); // Permitir que la animación de cierre termine
            TextoBusqueda = string.Empty;
            ResultadosBusqueda.Clear();
            await MostrarDialogoLocalAsync("Anime Existente", $"El anime '{animeAPI.Title.Romaji}' ya se encuentra en tu biblioteca.", false, "InformationOutline", "#FF9800");
            return;
        }

        string nombreSeguro = string.Join("_", animeAPI.Title.Romaji.Split(System.IO.Path.GetInvalidFileNameChars()));
        string rutaBaseVideos = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Anime");
        string nuevaRutaCarpeta = System.IO.Path.Combine(rutaBaseVideos, nombreSeguro);

        if (!System.IO.Directory.Exists(nuevaRutaCarpeta))
        {
            System.IO.Directory.CreateDirectory(nuevaRutaCarpeta);
        }

        int episodiosEmitidos = 0;
        string estadoAnime = animeAPI.Status?.ToUpperInvariant() ?? "UNKNOWN";

        if (estadoAnime == "NOT_YET_RELEASED")
        {
            episodiosEmitidos = 0;
        }
        else if (estadoAnime == "RELEASING")
        {
            if (animeAPI.NextAiringEpisode != null)
            {
                episodiosEmitidos = Math.Max(0, animeAPI.NextAiringEpisode.Episode - 1);
            }
            else
            {
                episodiosEmitidos = animeAPI.Episodes ?? 0;
            }
        }
        else // FINISHED, etc.
        {
            episodiosEmitidos = animeAPI.Episodes ?? 0;
        }
            
        var titulosAlt = new System.Collections.Generic.List<string>();
        if (!string.IsNullOrWhiteSpace(animeAPI.Title.English)) titulosAlt.Add(animeAPI.Title.English);
        if (!string.IsNullOrWhiteSpace(animeAPI.Title.UserPreferred) && animeAPI.Title.UserPreferred != animeAPI.Title.Romaji) titulosAlt.Add(animeAPI.Title.UserPreferred);
        if (animeAPI.Synonyms != null) titulosAlt.AddRange(System.Linq.Enumerable.Where(animeAPI.Synonyms, s => !string.IsNullOrWhiteSpace(s)));

        var nuevoAnimeLocal = new AnimeItem
        {
            AniListId = animeAPI.Id,
            Titulo = animeAPI.Title.Romaji,
            NombresAlternativos = string.Join(" | ", System.Linq.Enumerable.Distinct(titulosAlt)),
            UrlPortada = animeAPI.CoverImage?.ExtraLarge ?? animeAPI.CoverImage?.Large ?? "",
            RutaCarpeta = nuevaRutaCarpeta,
            Estado = animeAPI.Status ?? "UNKNOWN",
            TotalEpisodios = episodiosEmitidos,
            Generos = animeAPI.Genres != null ? string.Join(", ", animeAPI.Genres) : "",
            Sinopsis = animeAPI.Description ?? ""
        };

        await _databaseService.GuardarAnimeAsync(nuevoAnimeLocal);

        // Notificamos a la Galeria que se añadió un anime
        WeakReferenceMessenger.Default.Send(new AnimeAñadidoMensaje(nuevoAnimeLocal));

        IsDialogOpen = false;
        await Task.Delay(250); // Permitir que la animación de cierre termine antes de limpiar
        TextoBusqueda = string.Empty;
        ResultadosBusqueda.Clear();
        
        await MostrarDialogoLocalAsync("Anime Añadido", $"Carpeta creada automáticamente en:\n{nuevaRutaCarpeta}", false, "FolderPlusOutline", "#4CAF50");
    }
}