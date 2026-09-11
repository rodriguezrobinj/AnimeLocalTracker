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
    IRecipient<NavegarMensaje_Historial>,
    IRecipient<NavegarMensaje_Actualizaciones>,
    IRecipient<AbrirBuscadorMensaje>,
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

    public IDialogService DialogService { get; }

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
    [NotifyPropertyChangedFor(nameof(EsHistorialActivo))]
    [NotifyPropertyChangedFor(nameof(EsActualizacionesActivo))]
    private ObservableObject _vistaActual = null!;

    public bool EsGaleriaActiva => VistaActual is GaleriaViewModel || VistaActual is DetalleViewModel;
    public bool EsAgregarAnimeActivo => VistaActual is AgregarAnimeViewModel;
    public bool EsCalendarioActivo => VistaActual is CalendarioViewModel;
    public bool EsDescargasActivas => VistaActual is DescargasViewModel;
    public bool EsConfiguracionActiva => VistaActual is ConfiguracionViewModel;
    public bool EsAcercaDeActivo => VistaActual is AcercaDeViewModel;
    public bool EsEstadisticasActivo => VistaActual is EstadisticasViewModel;
    public bool EsHistorialActivo => VistaActual is HistorialViewModel;
    public bool EsActualizacionesActivo => VistaActual is ActualizacionesViewModel;

    // === BADGE DE DESCARGAS ===
    [ObservableProperty]
    private int _conteoDescargasActivas;

    [ObservableProperty]
    private bool _tieneDescargasActivas;

    // === DIÁLOGOS Y TOASTS ===
    // Delegados a IDialogService (DialogService)



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
        IUpdateService updateService,
        IDialogService dialogService)
    {
        _navigationService = navigationService;
        _animeTrackingService = animeTrackingService;
        _animeLibraryService = animeLibraryService;
        _downloadService = downloadService;
        _updateService = updateService;
        DialogService = dialogService;

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
            string nuevaVersion = update.TargetFullRelease?.Version.ToNormalizedString() ?? LocalizationService.T("Msg_NuevaVersion");
            bool confirmar = await DialogService.MostrarDialogoAsync(
                LocalizationService.T("Dlg_NuevaVersion"),
                string.Format(LocalizationService.T("Dlg_EncontroVersion"), nuevaVersion),
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

        DialogService.MostrarToast(
            LocalizationService.T("Notif_NuevosEpisodios"),
            $"{message.Cantidad} {LocalizationService.T("Notif_ResumenNuevos")}\n{message.Resumen}",
            "NewReleases",
            "#4CAF50"
        );
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
        // Si se abrió el Detalle desde el Calendario, "volver" regresa al calendario
        // (flujo circular calendario → ficha → calendario, sin pasar por la galería).
        if (_vistaAnteriorADetalleCalendario != null && VistaActual is DetalleViewModel)
        {
            VistaActual = _vistaAnteriorADetalleCalendario;
            _vistaAnteriorADetalleCalendario = null;
            return;
        }
        VistaActual = _navigationService.ObtenerGaleria();
    }

    public void Receive(NavegarMensaje_Detalle message) => _ = InicializarDetalleAsync(message);

    private async Task InicializarDetalleAsync(NavegarMensaje_Detalle message)
    {
        try
        {
            var detalleVm = _navigationService.CrearDetalle();
            _vistaAnteriorADetalleCalendario = VistaActual is CalendarioViewModel ? VistaActual : null;
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
        calendarioVm.RefrescarEstadosEmitidos();

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

    // === REPRODUCTOR IN-APP (EMBEBIDO) ===
    [ObservableProperty]
    private ReproductorViewModel? _reproductorActivo;

    // Vista a la que volver al salir del reproductor
    private ObservableObject? _vistaAnteriorAlReproductor;

    // Flujo calendario → ficha: "volver" desde el Detalle regresa al calendario.
    private ObservableObject? _vistaAnteriorADetalleCalendario;

    public void Receive(NavegarMensaje_Reproductor message) => _ = NavegarAlReproductorAsync(message);

    private async Task NavegarAlReproductorAsync(NavegarMensaje_Reproductor message)
    {
        try
        {
            if (ReproductorActivo != null)
            {
                ReproductorActivo.Dispose();
                ReproductorActivo = null;
            }

            _vistaAnteriorAlReproductor = VistaActual;

            var viewModel = _navigationService.CrearReproductor();
            if (viewModel == null) return;

            viewModel.AsegurarPlayerInicializado();
            viewModel.EsModoMini = false;
            ReproductorActivo = viewModel;

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
        // Si el usuario navega a otra pestaña mientras el reproductor está activo en formato completo,
        // pasa automáticamente a modo mini en la esquina para no interrumpir el video.
        if (ReproductorActivo != null && !ReproductorActivo.EsModoMini)
        {
            ReproductorActivo.EsModoMini = true;
        }
    }

    public void Receive(NavegarMensaje_VolverDelReproductor message)
    {
        if (ReproductorActivo != null)
        {
            ReproductorActivo = null;
        }

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
        var calendarioVm = _navigationService.ObtenerCalendario();
        VistaActual = calendarioVm;
        calendarioVm.RefrescarEstadosEmitidos();
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

    [RelayCommand]
    private async Task NavegarHistorial()
    {
        try
        {
            var historialVm = _navigationService.ObtenerHistorial();
            VistaActual = historialVm;
            await historialVm.CargarHistorialAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Error("MainViewModel", "Error navegando a historial", ex);
        }
    }

    public void Receive(NavegarMensaje_Historial message)
    {
        _ = NavegarHistorial();
    }

    [RelayCommand]
    private async Task NavegarActualizaciones()
    {
        try
        {
            var actualizacionesVm = _navigationService.ObtenerActualizaciones();
            VistaActual = actualizacionesVm;
            await actualizacionesVm.CargarActualizacionesAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Error("MainViewModel", "Error navegando a actualizaciones", ex);
        }
    }

    public void Receive(NavegarMensaje_Actualizaciones message)
    {
        _ = NavegarActualizaciones();
    }

    public void Receive(AbrirBuscadorMensaje message)
    {
        NavegarAgregarAnime();
    }

    // ==========================================
    // LÓGICA DE DIÁLOGOS
    // Delegada a IDialogService.
    // ==========================================
    
    
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
                await DialogService.MostrarDialogoAsync(
                    LocalizationService.T("Dlg_AnimeExistente"), 
                    string.Format(LocalizationService.T("Dlg_AnimeExistenteMsj"), animeAPI.Title.Romaji), 
                    false, "InformationOutline", "#FF9800");
                return;
            }

            IsDialogOpen = false;
            await Task.Delay(250); // Permitir que la animación de cierre termine antes de limpiar
            TextoBusqueda = string.Empty;
            ResultadosBusqueda.Clear();

            await DialogService.MostrarDialogoAsync(
                LocalizationService.T("Dlg_AnimeAnadido"), 
                string.Format(LocalizationService.T("Dlg_AnimeAnadidoMsj"), nuevoAnime.RutaCarpeta), 
                false, "FolderPlusOutline", "#4CAF50");
        }
        catch (Exception ex)
        {
            AppLogger.Error("MainViewModel", $"Error al crear/añadir anime '{animeAPI?.Title?.Romaji}'", ex);
            await DialogService.MostrarDialogoAsync(
                LocalizationService.T("Dlg_ErrorTitulo"), 
                string.Format(LocalizationService.T("Dlg_ErrorAnadirAnime"), ex.Message), 
                false, "AlertCircleOutline", "#E53935");
        }
    }
}