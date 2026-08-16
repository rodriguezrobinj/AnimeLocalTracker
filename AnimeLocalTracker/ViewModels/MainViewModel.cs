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
    IRecipient<AbrirBuscadorMensaje>,
    IRecipient<MostrarDialogoRequestMessage>
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IAnimeTrackingService _animeTrackingService;
    private readonly IDatabaseService _databaseService;

    // === NAVEGACIÓN (ViewModel-First) ===
    [ObservableProperty]
    private ObservableObject _vistaActual = null!;

    // === DIÁLOGOS CUSTOM ===
    [ObservableProperty] private bool _dialogoVisible;
    [ObservableProperty] private string _dialogoTitulo = "";
    [ObservableProperty] private string _dialogoMensaje = "";
    [ObservableProperty] private bool _dialogoEsConfirmacion;
    [ObservableProperty] private string _dialogoIcono = "InformationOutline";
    [ObservableProperty] private string _dialogoColor = "#3F51B5";
    
    private TaskCompletionSource<bool>? _dialogTcs;

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

    public MainViewModel(IServiceProvider serviceProvider, IAnimeTrackingService animeTrackingService, IDatabaseService databaseService)
    {
        _serviceProvider = serviceProvider;
        _animeTrackingService = animeTrackingService;
        _databaseService = databaseService;

        WeakReferenceMessenger.Default.RegisterAll(this);

        // Cargamos la vista inicial
        VistaActual = _serviceProvider.GetRequiredService<GaleriaViewModel>();
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
        catch (TaskCanceledException) { }
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

        string nombreSeguro = string.Join("_", animeAPI.Title.Romaji.Split(System.IO.Path.GetInvalidFileNameChars()));
        string rutaBaseVideos = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Anime");
        string nuevaRutaCarpeta = System.IO.Path.Combine(rutaBaseVideos, nombreSeguro);

        if (!System.IO.Directory.Exists(nuevaRutaCarpeta))
        {
            System.IO.Directory.CreateDirectory(nuevaRutaCarpeta);
        }

        int episodiosEmitidos = animeAPI.NextAiringEpisode != null 
            ? animeAPI.NextAiringEpisode.Episode - 1 
            : (animeAPI.Episodes ?? 12);
            
        var nuevoAnimeLocal = new AnimeItem
        {
            AniListId = animeAPI.Id,
            Titulo = animeAPI.Title.Romaji,
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
        TextoBusqueda = string.Empty;
        ResultadosBusqueda.Clear();
        
        await MostrarDialogoLocalAsync("Anime Añadido", $"Carpeta creada automáticamente en:\n{nuevaRutaCarpeta}", false, "FolderPlusOutline", "#4CAF50");
    }
}