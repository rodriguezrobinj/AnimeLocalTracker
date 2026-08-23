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
    IRecipient<NavegarMensaje_AgregarAnime>,
    IRecipient<NavegarMensaje_Detalle>,
    IRecipient<NavegarMensaje_Calendario>,
    IRecipient<NavegarMensaje_Descargas>,
    IRecipient<NavegarMensaje_Configuracion>,
    IRecipient<NavegarMensaje_AcercaDe>,
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
    private readonly IUpdateService _updateService;
    private readonly ISettingsService _settingsService;

    public string VersionAppTexto => _updateService.ObtenerVersionActual();

    // === NAVEGACIÓN (ViewModel-First) ===
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EsGaleriaActiva))]
    [NotifyPropertyChangedFor(nameof(EsAgregarAnimeActivo))]
    [NotifyPropertyChangedFor(nameof(EsCalendarioActivo))]
    [NotifyPropertyChangedFor(nameof(EsDescargasActivas))]
    [NotifyPropertyChangedFor(nameof(EsConfiguracionActiva))]
    [NotifyPropertyChangedFor(nameof(EsAcercaDeActivo))]
    private ObservableObject _vistaActual = null!;

    public bool EsGaleriaActiva => VistaActual is GaleriaViewModel || VistaActual is DetalleViewModel;
    public bool EsAgregarAnimeActivo => VistaActual is AgregarAnimeViewModel;
    public bool EsCalendarioActivo => VistaActual is CalendarioViewModel;
    public bool EsDescargasActivas => VistaActual is DescargasViewModel;
    public bool EsConfiguracionActiva => VistaActual is ConfiguracionViewModel;
    public bool EsAcercaDeActivo => VistaActual is AcercaDeViewModel;

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
            EjecutarBusquedaEnVivoAsyncCore(value);
        }
    }

    public MainViewModel(
        IServiceProvider serviceProvider, 
        IAnimeTrackingService animeTrackingService, 
        IDatabaseService databaseService,
        IDownloadService downloadService,
        IUpdateService updateService,
        ISettingsService settingsService)
    {
        _serviceProvider = serviceProvider;
        _animeTrackingService = animeTrackingService;
        _databaseService = databaseService;
        _downloadService = downloadService;
        _updateService = updateService;
        _settingsService = settingsService;

        WeakReferenceMessenger.Default.RegisterAll(this);

        // Cargamos la vista inicial
        VistaActual = _serviceProvider.GetRequiredService<GaleriaViewModel>();
        ActualizarConteoDescargas();
    }

    [RelayCommand]
    public async Task BuscarActualizacionesManualAsync()
    {
        var update = await _updateService.ComprobarActualizacionesAsync(esManual: true);
        if (update != null)
        {
            string nuevaVersion = update.TargetFullRelease?.Version.ToNormalizedString() ?? "nueva versión";
            bool confirmar = await MostrarDialogoLocalAsync(
                "Nueva versión disponible",
                $"Se encontró la versión {nuevaVersion}. ¿Deseas descargarla e instalarla ahora?",
                true,
                "Update",
                "#2196F3");

            if (confirmar)
            {
                bool descargado = await _updateService.DescargarActualizacionAsync(update);
                if (descargado)
                {
                    _updateService.AplicarActualizacionYReiniciar(update);
                }
            }
        }
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
        try
        {
            var detalleVm = _serviceProvider.GetRequiredService<DetalleViewModel>();
            VistaActual = detalleVm;
            await detalleVm.InicializarAsync(message.AnimeSeleccionado);
        }
        catch (Exception ex)
        {
            AppLogger.Error("MainViewModel", "Error al inicializar la vista de detalle", ex);
        }
    }

    public void Receive(NavegarMensaje_Calendario message)
    {
        var calendarioVm = _serviceProvider.GetRequiredService<CalendarioViewModel>();
        VistaActual = calendarioVm;

        // El calendario es singleton: si la carga inicial falló (red/rate-limit) o está vacío,
        // reintentar al navegar para que no quede pegado en columnas vacías.
        if (calendarioVm.EstaVacio && !calendarioVm.EstaCargando)
        {
            calendarioVm.CargarCalendarioCommand.Execute(null);
        }
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
    private void NavegarAgregarAnime()
    {
        VistaActual = _serviceProvider.GetRequiredService<AgregarAnimeViewModel>();
    }

    public void Receive(NavegarMensaje_AgregarAnime message)
    {
        NavegarAgregarAnime();
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

    [RelayCommand]
    private void NavegarConfiguracion()
    {
        try 
        {
            VistaActual = _serviceProvider.GetRequiredService<ConfiguracionViewModel>();
        }
        catch (Exception ex)
        {
            System.IO.File.AppendAllText("crash.log", $"[{DateTime.Now:O}] [MainViewModel] Error en NavegarConfiguracion: {ex}\n");
            AppLogger.Error("MainViewModel", "Error navegando a configuración", ex);
        }
    }

    public void Receive(NavegarMensaje_Configuracion message)
    {
        NavegarConfiguracion();
    }

    [RelayCommand]
    private void NavegarAcercaDe()
    {
        try 
        {
            VistaActual = _serviceProvider.GetRequiredService<AcercaDeViewModel>();
        }
        catch (Exception ex)
        {
            AppLogger.Error("MainViewModel", "Error navegando a acerca de", ex);
        }
    }

    public void Receive(NavegarMensaje_AcercaDe message)
    {
        NavegarAcercaDe();
    }

    public void Receive(AbrirBuscadorMensaje message)
    {
        NavegarAgregarAnime();
    }

    public void Receive(MostrarDialogoRequestMessage message)
    {
        if (message.HasReceivedResponse)
        {
            return;
        }

        try
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                // Marshaling al hilo de UI para evitar accesos cruzados (Flyleaf/descargas).
                dispatcher.Invoke(() => ResponderDialogo(message));
            }
            else
            {
                ResponderDialogo(message);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("MainViewModel", $"Error respondiendo a MostrarDialogoRequestMessage: {ex.Message}");
        }
    }

    private void ResponderDialogo(MostrarDialogoRequestMessage message)
    {
        if (message.HasReceivedResponse)
        {
            return;
        }

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
                try
                {
                    await Task.Delay(3500);
                    System.Windows.Application.Current?.Dispatcher?.Invoke(() => ToastVisible = false);
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("MainViewModel", $"Error al ocultar toast: {ex.Message}");
                }
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
        CancelarBusquedaPendiente();
        IsDialogOpen = false;
    }

    /// <summary>
    /// Cancela de forma segura cualquier búsqueda pendiente.
    /// Solo cancela el token — no dispone el CTS inmediatamente,
    /// ya que tareas async previas aún pueden referenciar el token.
    /// </summary>
    private void CancelarBusquedaPendiente()
    {
        try
        {
            _searchCts?.Cancel();
        }
        catch (ObjectDisposedException) { }
    }

    // ==========================================
    // LÓGICA DE BÚSQUEDA Y CREACIÓN DE ANIME
    // ==========================================

    private async void EjecutarBusquedaEnVivoAsyncCore(string busqueda)
    {
        if (string.IsNullOrWhiteSpace(busqueda) || busqueda.Length < 3)
        {
            CancelarBusquedaPendiente();
            ResultadosBusqueda.Clear();
            IsSearching = false;
            BusquedaSinResultados = false;
            return;
        }

        // Cancelar la búsqueda anterior (solo Cancel, nunca Dispose desde aquí)
        CancelarBusquedaPendiente();

        // Crear un nuevo CTS para esta búsqueda
        var cts = new System.Threading.CancellationTokenSource();
        _searchCts = cts;

        try
        {
            IsSearching = true;
            await Task.Delay(400, cts.Token);

            // Verificar si mientras esperábamos, otra búsqueda nos canceló
            if (cts.Token.IsCancellationRequested) return;

            var resultados = await _animeTrackingService.BuscarAnimesEnVivoAsync(busqueda, cts.Token);

            if (cts.Token.IsCancellationRequested) return;

            ResultadosBusqueda.Clear();
            foreach (var r in resultados)
            {
                ResultadosBusqueda.Add(r);
            }
            BusquedaSinResultados = ResultadosBusqueda.Count == 0;
        }
        catch (OperationCanceledException)
        {
            // Normal: el usuario escribió otro carácter y cancelamos esta búsqueda.
        }
        catch (Exception ex)
        {
            AppLogger.Error("MainViewModel", $"Error durante la búsqueda en vivo para '{busqueda}'", ex);
        }
        finally
        {
            // Solo actualizar IsSearching si ESTE CTS sigue siendo el activo.
            // Si _searchCts ya apunta a otro objeto, otra búsqueda tomó el control.
            if (ReferenceEquals(_searchCts, cts))
            {
                IsSearching = false;
            }

            // Ahora sí es seguro disponer: ya salimos de todas las operaciones async.
            cts.Dispose();
        }
    }

    [RelayCommand]
    private async Task SeleccionarYCrearAnimeAsync(AniListMedia animeAPI)
    {
        if (animeAPI?.Title?.Romaji == null) return;

        try
        {
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
            string rutaBaseVideos = _settingsService.ObtenerRutaBaseAnimes();
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
        catch (Exception ex)
        {
            AppLogger.Error("MainViewModel", $"Error al crear/añadir anime '{animeAPI?.Title?.Romaji}'", ex);
            await MostrarDialogoLocalAsync("Error", $"No se pudo añadir el anime: {ex.Message}", false, "AlertCircleOutline", "#E53935");
        }
    }
}