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
    IRecipient<NuevosEpisodiosMensaje>
{
    private readonly INavigationService _navigationService;
    private readonly IAnimeTrackingService _animeTrackingService;
    private readonly AnimeLibraryService _animeLibraryService;
    private readonly IDownloadService _downloadService;
    private readonly IUpdateService _updateService;

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
        IDialogService dialogService)
    {
        _navigationService = navigationService;
        _animeTrackingService = animeTrackingService;
        _animeLibraryService = animeLibraryService;
        _downloadService = downloadService;
        _updateService = updateService;
        DialogService = dialogService;

        WeakReferenceMessenger.Default.RegisterAll(this);

        // Cargamos la vista inicial a través del servicio de navegación
        WeakReferenceMessenger.Default.Send(new NavegarMensaje_Galeria());
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