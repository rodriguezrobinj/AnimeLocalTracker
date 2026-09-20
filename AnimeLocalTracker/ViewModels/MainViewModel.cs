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
    IRecipient<AbrirBuscadorMensaje>,
    IRecipient<DescargaProgresoMensaje>,
    IRecipient<NuevosEpisodiosMensaje>,
    IRecipient<MostrarDialogoRequestMessage>
{
    private readonly INavigationService _navigationService;
    private readonly IAnimeTrackingService _animeTrackingService;
    private readonly AnimeLibraryService _animeLibraryService;
    private readonly IDownloadService _downloadService;
    private readonly IUpdateService _updateService;
    private readonly IDatabaseService _databaseService;
    private readonly IFileScannerService _fileScannerService;
    private readonly NewEpisodeNotifier _newEpisodeNotifier;
    private readonly ISystemTrayService _systemTrayService;

    public NavigationService Navigation => (NavigationService)_navigationService;
    public IDialogService DialogService { get; }

    public string VersionAppTexto => _updateService.ObtenerVersionActual();

    // === BADGE DE DESCARGAS ===
    [ObservableProperty]
    private int _conteoDescargasActivas;

    [ObservableProperty]
    private bool _tieneDescargasActivas;

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
        IDialogService dialogService,
        IDatabaseService databaseService,
        IFileScannerService fileScannerService,
        NewEpisodeNotifier newEpisodeNotifier,
        ISystemTrayService systemTrayService)
    {
        _navigationService = navigationService;
        _animeTrackingService = animeTrackingService;
        _animeLibraryService = animeLibraryService;
        _downloadService = downloadService;
        _updateService = updateService;
        DialogService = dialogService;
        _databaseService = databaseService;
        _fileScannerService = fileScannerService;
        _newEpisodeNotifier = newEpisodeNotifier;
        _systemTrayService = systemTrayService;
        _systemTrayService.ReanudarUltimoAnimeSolicitado += OnReanudarUltimoAnimeSolicitado;
        _systemTrayService.BuscarNuevosEpisodiosSolicitado += OnBuscarNuevosEpisodiosSolicitado;

        WeakReferenceMessenger.Default.RegisterAll(this);

        // Cargamos la vista inicial a través del servicio de navegación
        WeakReferenceMessenger.Default.Send(new NavegarMensaje_Galeria());
        ActualizarConteoDescargas();
    }

    /// <summary>Menú "Reanudar último anime" de la bandeja del sistema: navega directo al
    /// episodio más reciente del historial (mismo criterio que Historial: UltimaReproduccion
    /// desc), replicando la construcción de EpisodiosDisponibles que hace HistorialViewModel.ReanudarAsync.</summary>
    private async void OnReanudarUltimoAnimeSolicitado(object? sender, EventArgs e)
    {
        try
        {
            var recientes = await _databaseService.ObtenerHistorialEpisodiosAsync(1);
            var registro = recientes.Count > 0 ? recientes[0] : null;
            if (registro == null || string.IsNullOrWhiteSpace(registro.RutaArchivo) || !System.IO.File.Exists(registro.RutaArchivo))
            {
                _systemTrayService.MostrarNotificacion(LocalizationService.T("Tray_Titulo"), LocalizationService.T(
                    registro == null ? "Tray_SinUltimoAnime" : "Tray_ArchivoNoEncontrado"));
                return;
            }

            var anime = await _databaseService.ObtenerAnimePorIdAsync(registro.AniListId);
            var episodiosDisponibles = new System.Collections.Generic.List<EpisodioItem>();
            if (anime != null && !string.IsNullOrWhiteSpace(anime.RutaCarpeta))
            {
                try { episodiosDisponibles = await _fileScannerService.EscanearEpisodiosAsync(anime.RutaCarpeta) ?? new(); }
                catch (Exception ex) { AppLogger.Debug("MainViewModel", $"No se pudo escanear episodios para reanudar desde bandeja: {ex.Message}"); }
            }

            WeakReferenceMessenger.Default.Send(new NavegarMensaje_Reproductor(
                registro.RutaArchivo, registro.AniListId, anime?.Titulo ?? $"Anime {registro.AniListId}", registro.NumeroEpisodio,
                EpisodiosDisponibles: episodiosDisponibles, RutaPortada: anime?.PortadaVisible));
        }
        catch (Exception ex)
        {
            AppLogger.Error("MainViewModel", "Error reanudando último anime desde la bandeja", ex);
        }
    }

    /// <summary>Menú "Buscar nuevos episodios" de la bandeja: dispara el mismo chequeo que corre
    /// periódicamente en segundo plano, y confirma el resultado con una notificación nativa (la
    /// ventana puede estar oculta, donde el toast normal de NuevosEpisodiosMensaje no se vería).</summary>
    private async void OnBuscarNuevosEpisodiosSolicitado(object? sender, EventArgs e)
    {
        try
        {
            int encontrados = await _newEpisodeNotifier.BuscarYNotificarNuevosAsync();
            _systemTrayService.MostrarNotificacion(
                LocalizationService.T("Tray_Titulo"),
                encontrados > 0
                    ? string.Format(LocalizationService.T("Tray_NuevosEncontradosFormato"), encontrados)
                    : LocalizationService.T("Tray_SinNuevos"));
        }
        catch (Exception ex)
        {
            AppLogger.Error("MainViewModel", "Error buscando episodios nuevos desde la bandeja", ex);
        }
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

    [RelayCommand]
    private void NavegarGaleria() => WeakReferenceMessenger.Default.Send(new NavegarMensaje_Galeria());

    [RelayCommand]
    private void NavegarAgregarAnime() => WeakReferenceMessenger.Default.Send(new NavegarMensaje_AgregarAnime());

    [RelayCommand]
    private void NavegarCalendario() => WeakReferenceMessenger.Default.Send(new NavegarMensaje_Calendario());

    [RelayCommand]
    private void NavegarDescargas() => WeakReferenceMessenger.Default.Send(new NavegarMensaje_Descargas());

    [RelayCommand]
    private void NavegarConfiguracion() => WeakReferenceMessenger.Default.Send(new NavegarMensaje_Configuracion());

    [RelayCommand]
    private void NavegarAcercaDe() => WeakReferenceMessenger.Default.Send(new NavegarMensaje_AcercaDe());

    [RelayCommand]
    private void NavegarEstadisticas() => WeakReferenceMessenger.Default.Send(new NavegarMensaje_Estadisticas());

    [RelayCommand]
    private void NavegarLogros() => WeakReferenceMessenger.Default.Send(new NavegarMensaje_Logros());

    /// <summary>
    /// Avisos y diálogos que otros ViewModels piden por mensaje (reproductor: "Episodio marcado como visto",
    /// AniSkip, reanudar…; calendario). El receptor se perdió en el refactor de IDialogService y esos avisos
    /// dejaron de mostrarse; ahora se delega en IDialogService, que ya sabe mostrar toasts y confirmaciones.
    /// </summary>
    public void Receive(MostrarDialogoRequestMessage message)
    {
        // MainViewModel es transient: puede haber varias instancias registradas y solo una debe responder.
        if (message.HasReceivedResponse) return;

        try
        {
            message.Reply(DialogService.MostrarDialogoAsync(message.Titulo, message.Mensaje, message.EsConfirmacion, message.Icono, message.Color));
        }
        catch (InvalidOperationException)
        {
            // Otra instancia respondió entre la comprobación y el Reply: no es un error.
        }
        catch (Exception ex)
        {
            AppLogger.Debug("MainViewModel", $"Error respondiendo a MostrarDialogoRequestMessage: {ex.Message}");
        }
    }

    [RelayCommand]
    private void NavegarHistorial() => WeakReferenceMessenger.Default.Send(new NavegarMensaje_Historial());

    [RelayCommand]
    private void NavegarActualizaciones() => WeakReferenceMessenger.Default.Send(new NavegarMensaje_Actualizaciones());

    public void Receive(AbrirBuscadorMensaje message)
    {
        NavegarAgregarAnime();
    }

    [RelayCommand]
    private void CerrarDialogoBusqueda()
    {
        CancelarBusquedaPendiente();
        IsDialogOpen = false;
    }

    private void CancelarBusquedaPendiente()
    {
        try
        {
            _searchCts?.Cancel();
        }
        catch (ObjectDisposedException) { }
    }

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

        CancelarBusquedaPendiente();

        var cts = new System.Threading.CancellationTokenSource();
        _searchCts = cts;

        try
        {
            IsSearching = true;
            await Task.Delay(400, cts.Token);

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
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLogger.Error("MainViewModel", $"Error durante la búsqueda en vivo para '{busqueda}'", ex);
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts))
            {
                IsSearching = false;
            }
            cts.Dispose();
        }
    }

    [RelayCommand]
    private async Task SeleccionarYCrearAnimeAsync(AniListMedia animeAPI)
    {
        if (animeAPI?.Title?.Romaji == null) return;

        try
        {
            var nuevoAnime = await _animeLibraryService.CrearYGuardarAnimeAsync(animeAPI, animeAPI.Title.Romaji);

            if (nuevoAnime == null)
            {
                IsDialogOpen = false;
                await Task.Delay(250);
                TextoBusqueda = string.Empty;
                ResultadosBusqueda.Clear();
                await DialogService.MostrarDialogoAsync(
                    LocalizationService.T("Dlg_AnimeExistente"), 
                    string.Format(LocalizationService.T("Dlg_AnimeExistenteMsj"), animeAPI.Title.Romaji), 
                    false, "InformationOutline", "#FF9800");
                return;
            }

            IsDialogOpen = false;
            await Task.Delay(250);
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