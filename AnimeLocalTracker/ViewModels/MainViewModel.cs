using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
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
    IRecipient<NavegarMensaje_Estadisticas>,
    IRecipient<AbrirBuscadorMensaje>,
    IRecipient<MostrarDialogoRequestMessage>,
    IRecipient<NavegarMensaje_Reproductor>,
    IRecipient<NavegarMensaje_VolverDelReproductor>,
    IRecipient<DescargaProgresoMensaje>,
    IRecipient<NuevosEpisodiosMensaje>
{
    private readonly INavigationService _navigationService;
    private readonly IAnimeTrackingService _animeTrackingService;
    private readonly AnimeLibraryService _animeLibraryService;
    private readonly IDownloadService _downloadService;
    private readonly IUpdateService _updateService;

    public string VersionAppTexto => _updateService.ObtenerVersionActual();

    // === NAVEGACIÓN (ViewModel-First) ===
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EsGaleriaActiva))]
    [NotifyPropertyChangedFor(nameof(EsAgregarAnimeActivo))]
    [NotifyPropertyChangedFor(nameof(EsCalendarioActivo))]
    [NotifyPropertyChangedFor(nameof(EsDescargasActivas))]
    [NotifyPropertyChangedFor(nameof(EsConfiguracionActiva))]
    [NotifyPropertyChangedFor(nameof(EsAcercaDeActivo))]
    [NotifyPropertyChangedFor(nameof(EsEstadisticasActivo))]
    private ObservableObject _vistaActual = null!;

    public bool EsGaleriaActiva => VistaActual is GaleriaViewModel || VistaActual is DetalleViewModel;
    public bool EsAgregarAnimeActivo => VistaActual is AgregarAnimeViewModel;
    public bool EsCalendarioActivo => VistaActual is CalendarioViewModel;
    public bool EsDescargasActivas => VistaActual is DescargasViewModel;
    public bool EsConfiguracionActiva => VistaActual is ConfiguracionViewModel;
    public bool EsAcercaDeActivo => VistaActual is AcercaDeViewModel;
    public bool EsEstadisticasActivo => VistaActual is EstadisticasViewModel;

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
            _ = EjecutarBusquedaEnVivoAsyncCore(value);
        }
    }

    public MainViewModel(
        INavigationService navigationService, 
        IAnimeTrackingService animeTrackingService, 
        AnimeLibraryService animeLibraryService,
        IDownloadService downloadService,
        IUpdateService updateService)
    {
        _navigationService = navigationService;
        _animeTrackingService = animeTrackingService;
        _animeLibraryService = animeLibraryService;
        _downloadService = downloadService;
        _updateService = updateService;

        WeakReferenceMessenger.Default.RegisterAll(this);

        // Cargamos la vista inicial
        VistaActual = _navigationService.ObtenerGaleria();
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

    public void Receive(NuevosEpisodiosMensaje message)
    {
        if (message.Cantidad <= 0) return;

        ToastTitulo = LocalizationService.T("Notif_NuevosEpisodios");
        ToastMensaje = $"{message.Cantidad} {LocalizationService.T("Notif_ResumenNuevos")}\n{message.Resumen}";
        ToastIcono = "NewReleases";
        ToastColor = "#4CAF50";
        ToastVisible = true;

        // Ocultar automáticamente después de 6 segundos
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(6000);
                System.Windows.Application.Current?.Dispatcher?.Invoke(() => ToastVisible = false);
            }
            catch { }
        });
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
        VistaActual = _navigationService.ObtenerGaleria();
    }

    public void Receive(NavegarMensaje_Detalle message) => _ = InicializarDetalleAsync(message);

    private async Task InicializarDetalleAsync(NavegarMensaje_Detalle message)
    {
        try
        {
            var detalleVm = _navigationService.CrearDetalle();
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
        var calendarioVm = _navigationService.ObtenerCalendario();
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
        VistaActual = _navigationService.ObtenerDescargas();
    }

    public void Receive(NavegarMensaje_Reproductor message) => _ = NavegarAlReproductorAsync(message);

    private async Task NavegarAlReproductorAsync(NavegarMensaje_Reproductor message)
    {
        try
        {
            // Guardar la vista actual antes de navegar al reproductor
            _vistaAnteriorAlReproductor = VistaActual;

            var viewModel = _navigationService.CrearReproductor();
            if (viewModel == null) return;

            // 1. Crear el objeto Player antes de montar la vista para que FlyleafHost enlace un Player no nulo
            viewModel.AsegurarPlayerInicializado();

            // 2. Montar la vista en el Visual Tree de WPF PRIMERO para que FlyleafHost capture el contexto Direct3D
            VistaActual = viewModel;

            // 3. Abrir el video con FlyleafHost ya activo en pantalla (elimina la pantalla negra por carrera)
            try
            {
                await viewModel.CargarVideoAsync(message.RutaVideo, message.AnimeId, message.TituloAnime, message.Episodio, message.EpisodiosDisponibles);
            }
            catch (Exception ex)
            {
                AppLogger.Error("MainViewModel", $"Error al cargar el video '{message.RutaVideo}'", ex);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("MainViewModel", "Error al procesar NavegarMensaje_Reproductor", ex);
        }
    }

    partial void OnVistaActualChanged(ObservableObject? oldValue, ObservableObject newValue)
    {
        // Al salir del reproductor por CUALQUIER vía (no solo el botón Cerrar), liberar su
        // player: si queda vivo sigue reproduciendo audio en segundo plano (instancias fantasma).
        if (oldValue is ReproductorViewModel reproductorAnterior && !ReferenceEquals(reproductorAnterior, newValue))
        {
            reproductorAnterior.Dispose();
        }
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
            VistaActual = _navigationService.ObtenerGaleria();
        }
    }

    [RelayCommand]
    private void NavegarGaleria()
    {
        VistaActual = _navigationService.ObtenerGaleria();
    }

    [RelayCommand]
    private void NavegarAgregarAnime()
    {
        VistaActual = _navigationService.ObtenerAgregarAnime();
    }

    public void Receive(NavegarMensaje_AgregarAnime message)
    {
        NavegarAgregarAnime();
    }

    [RelayCommand]
    private void NavegarCalendario()
    {
        VistaActual = _navigationService.ObtenerCalendario();
    }

    [RelayCommand]
    private void NavegarDescargas()
    {
        VistaActual = _navigationService.ObtenerDescargas();
    }

    [RelayCommand]
    private void NavegarConfiguracion()
    {
        try
        {
            VistaActual = _navigationService.ObtenerConfiguracion();
        }
        catch (Exception ex)
        {
            // AppLogger escribe en %LocalAppData%: seguro incluso instalado en Program Files.
            // (Antes se escribía un crash.log relativo al EXE, lo que lanzaba dentro del catch.)
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
            VistaActual = _navigationService.ObtenerAcercaDe();
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

    [RelayCommand]
    private async Task NavegarEstadisticas()
    {
        try
        {
            var estadisticasVm = _navigationService.ObtenerEstadisticas();
            VistaActual = estadisticasVm;
            await estadisticasVm.CargarEstadisticasAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Error("MainViewModel", "Error navegando a estadísticas", ex);
        }
    }

    public void Receive(NavegarMensaje_Estadisticas message)
    {
        _ = NavegarEstadisticas();
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

    private async Task EjecutarBusquedaEnVivoAsyncCore(string busqueda)
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
            // ARQ-02: toda la lógica de creación (validación, carpeta, episodios, persistencia)
            // vive en AnimeLibraryService; este ViewModel solo gestiona su estado de UI.
            var nuevoAnime = await _animeLibraryService.CrearYGuardarAnimeAsync(animeAPI, animeAPI.Title.Romaji);

            if (nuevoAnime == null)
            {
                IsDialogOpen = false;
                await Task.Delay(250); // Permitir que la animación de cierre termine
                TextoBusqueda = string.Empty;
                ResultadosBusqueda.Clear();
                await MostrarDialogoLocalAsync("Anime Existente", $"El anime '{animeAPI.Title.Romaji}' ya se encuentra en tu biblioteca.", false, "InformationOutline", "#FF9800");
                return;
            }

            IsDialogOpen = false;
            await Task.Delay(250); // Permitir que la animación de cierre termine antes de limpiar
            TextoBusqueda = string.Empty;
            ResultadosBusqueda.Clear();

            await MostrarDialogoLocalAsync("Anime Añadido", $"Carpeta creada automáticamente en:\n{nuevoAnime.RutaCarpeta}", false, "FolderPlusOutline", "#4CAF50");
        }
        catch (Exception ex)
        {
            AppLogger.Error("MainViewModel", $"Error al crear/añadir anime '{animeAPI?.Title?.Romaji}'", ex);
            await MostrarDialogoLocalAsync("Error", $"No se pudo añadir el anime: {ex.Message}", false, "AlertCircleOutline", "#E53935");
        }
    }
}