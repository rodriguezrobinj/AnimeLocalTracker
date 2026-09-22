using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Services;

namespace AnimeLocalTracker.ViewModels;

public interface INavigationService : System.ComponentModel.INotifyPropertyChanged
{
    ObservableObject VistaActual { get; }
    ReproductorViewModel? ReproductorActivo { get; }

    bool EsGaleriaActiva { get; }
    bool EsAgregarAnimeActivo { get; }
    bool EsCalendarioActivo { get; }
    bool EsDescargasActivas { get; }
    bool EsConfiguracionActiva { get; }
    bool EsAcercaDeActivo { get; }
    bool EsEstadisticasActivo { get; }
    bool EsHistorialActivo { get; }
    bool EsLogrosActivo { get; }
    bool EsActualizacionesActivo { get; }

    GaleriaViewModel ObtenerGaleria();
    AgregarAnimeViewModel ObtenerAgregarAnime();
    CalendarioViewModel ObtenerCalendario();
    DescargasViewModel ObtenerDescargas();
    ConfiguracionViewModel ObtenerConfiguracion();
    AcercaDeViewModel ObtenerAcercaDe();
    EstadisticasViewModel ObtenerEstadisticas();
    HistorialViewModel ObtenerHistorial();
    LogrosViewModel ObtenerLogros();
    ActualizacionesViewModel ObtenerActualizaciones();
    DetalleViewModel CrearDetalle();
    ReproductorViewModel CrearReproductor();
}

public sealed partial class NavigationService : ObservableObject, INavigationService,
    IRecipient<NavegarMensaje_Galeria>,
    IRecipient<NavegarMensaje_AgregarAnime>,
    IRecipient<NavegarMensaje_Detalle>,
    IRecipient<NavegarMensaje_Calendario>,
    IRecipient<NavegarMensaje_Descargas>,
    IRecipient<NavegarMensaje_Configuracion>,
    IRecipient<NavegarMensaje_AcercaDe>,
    IRecipient<NavegarMensaje_Estadisticas>,
    IRecipient<NavegarMensaje_Historial>,
    IRecipient<NavegarMensaje_Logros>,
    IRecipient<NavegarMensaje_Actualizaciones>,
    IRecipient<NavegarMensaje_Reproductor>,
    IRecipient<NavegarMensaje_VolverDelReproductor>
{
    private readonly IServiceProvider _serviceProvider;
    private ObservableObject? _vistaAnteriorADetalleCalendario;
    private ObservableObject? _vistaAnteriorAlReproductor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EsGaleriaActiva))]
    [NotifyPropertyChangedFor(nameof(EsAgregarAnimeActivo))]
    [NotifyPropertyChangedFor(nameof(EsCalendarioActivo))]
    [NotifyPropertyChangedFor(nameof(EsDescargasActivas))]
    [NotifyPropertyChangedFor(nameof(EsConfiguracionActiva))]
    [NotifyPropertyChangedFor(nameof(EsAcercaDeActivo))]
    [NotifyPropertyChangedFor(nameof(EsEstadisticasActivo))]
    [NotifyPropertyChangedFor(nameof(EsHistorialActivo))]
    [NotifyPropertyChangedFor(nameof(EsLogrosActivo))]
    [NotifyPropertyChangedFor(nameof(EsActualizacionesActivo))]
    private ObservableObject _vistaActual = null!;

    [ObservableProperty]
    private ReproductorViewModel? _reproductorActivo;

    public bool EsGaleriaActiva => VistaActual is GaleriaViewModel || VistaActual is DetalleViewModel;
    public bool EsAgregarAnimeActivo => VistaActual is AgregarAnimeViewModel;
    public bool EsCalendarioActivo => VistaActual is CalendarioViewModel;
    public bool EsDescargasActivas => VistaActual is DescargasViewModel;
    public bool EsConfiguracionActiva => VistaActual is ConfiguracionViewModel;
    public bool EsAcercaDeActivo => VistaActual is AcercaDeViewModel;
    public bool EsEstadisticasActivo => VistaActual is EstadisticasViewModel;
    public bool EsHistorialActivo => VistaActual is HistorialViewModel;
    public bool EsLogrosActivo => VistaActual is LogrosViewModel;
    public bool EsActualizacionesActivo => VistaActual is ActualizacionesViewModel;

    public NavigationService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        WeakReferenceMessenger.Default.RegisterAll(this);
    }

    public GaleriaViewModel ObtenerGaleria() => _serviceProvider.GetRequiredService<GaleriaViewModel>();
    public AgregarAnimeViewModel ObtenerAgregarAnime() => _serviceProvider.GetRequiredService<AgregarAnimeViewModel>();
    public CalendarioViewModel ObtenerCalendario() => _serviceProvider.GetRequiredService<CalendarioViewModel>();
    public DescargasViewModel ObtenerDescargas() => _serviceProvider.GetRequiredService<DescargasViewModel>();
    public ConfiguracionViewModel ObtenerConfiguracion() => _serviceProvider.GetRequiredService<ConfiguracionViewModel>();
    public AcercaDeViewModel ObtenerAcercaDe() => _serviceProvider.GetRequiredService<AcercaDeViewModel>();
    public EstadisticasViewModel ObtenerEstadisticas() => _serviceProvider.GetRequiredService<EstadisticasViewModel>();
    public HistorialViewModel ObtenerHistorial() => _serviceProvider.GetRequiredService<HistorialViewModel>();
    public LogrosViewModel ObtenerLogros() => _serviceProvider.GetRequiredService<LogrosViewModel>();
    public ActualizacionesViewModel ObtenerActualizaciones() => _serviceProvider.GetRequiredService<ActualizacionesViewModel>();
    public DetalleViewModel CrearDetalle() => _serviceProvider.GetRequiredService<DetalleViewModel>();
    public ReproductorViewModel CrearReproductor() => _serviceProvider.GetRequiredService<ReproductorViewModel>();

    public void Receive(NavegarMensaje_Galeria message)
    {
        if (_vistaAnteriorADetalleCalendario != null && VistaActual is DetalleViewModel)
        {
            VistaActual = _vistaAnteriorADetalleCalendario;
            _vistaAnteriorADetalleCalendario = null;
            return;
        }
        VistaActual = ObtenerGaleria();
    }

    public void Receive(NavegarMensaje_AgregarAnime message)
    {
        VistaActual = ObtenerAgregarAnime();
    }

    public void Receive(NavegarMensaje_Detalle message)
    {
        _ = InicializarDetalleAsync(message);
    }

    // internal: permite a las pruebas esperar la navegación directamente en vez de depender del
    // fire-and-forget de Receive() (evita timing flaky).
    internal async Task InicializarDetalleAsync(NavegarMensaje_Detalle message)
    {
        try
        {
            // Navegación rápida entre fichas (ej. varios clics seguidos en la galería): la ficha
            // anterior se descarta sin haber terminado de cargar; se cancelan sus tareas de fondo
            // en vez de dejarlas seguir golpeando disco/red por un anime que ya no está en pantalla.
            if (VistaActual is DetalleViewModel anterior) anterior.Dispose();

            var detalleVm = CrearDetalle();
            _vistaAnteriorADetalleCalendario = VistaActual is CalendarioViewModel ? VistaActual : null;
            VistaActual = detalleVm;
            await detalleVm.InicializarAsync(message.AnimeSeleccionado);
        }
        catch (Exception ex)
        {
            AppLogger.Error("NavigationService", "Error al inicializar la vista de detalle", ex);
        }
    }

    public void Receive(NavegarMensaje_Calendario message)
    {
        var calendarioVm = ObtenerCalendario();
        VistaActual = calendarioVm;
        calendarioVm.RefrescarEstadosEmitidos();
        if (calendarioVm.EstaVacio && !calendarioVm.EstaCargando)
        {
            calendarioVm.CargarCalendarioCommand.Execute(null);
        }
    }

    public void Receive(NavegarMensaje_Descargas message)
    {
        var descargas = ObtenerDescargas();
        descargas.Refrescar();
        VistaActual = descargas;
    }

    public void Receive(NavegarMensaje_Configuracion message)
    {
        try
        {
            VistaActual = ObtenerConfiguracion();
        }
        catch (Exception ex)
        {
            AppLogger.Error("NavigationService", "Error navegando a configuración", ex);
        }
    }

    public void Receive(NavegarMensaje_AcercaDe message)
    {
        try 
        {
            VistaActual = ObtenerAcercaDe();
        }
        catch (Exception ex)
        {
            AppLogger.Error("NavigationService", "Error navegando a acerca de", ex);
        }
    }

    public void Receive(NavegarMensaje_Estadisticas message)
    {
        _ = NavegarEstadisticas();
    }

    private async Task NavegarEstadisticas()
    {
        try
        {
            var estadisticasVm = ObtenerEstadisticas();
            VistaActual = estadisticasVm;
            if (estadisticasVm.NecesitaRecargar())
            {
                await estadisticasVm.CargarEstadisticasAsync();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("NavigationService", "Error navegando a estadísticas", ex);
        }
    }

    public void Receive(NavegarMensaje_Historial message)
    {
        _ = NavegarHistorial();
    }

    private async Task NavegarHistorial()
    {
        try
        {
            var historialVm = ObtenerHistorial();
            VistaActual = historialVm;
            if (historialVm.NecesitaRecargar())
            {
                await historialVm.CargarHistorialAsync();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("NavigationService", "Error navegando a historial", ex);
        }
    }

    public void Receive(NavegarMensaje_Logros message)
    {
        _ = NavegarLogros();
    }

    private async Task NavegarLogros()
    {
        try
        {
            var logrosVm = ObtenerLogros();
            VistaActual = logrosVm;
            if (logrosVm.NecesitaRecargar())
            {
                await logrosVm.CargarAsync();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("NavigationService", "Error navegando a logros", ex);
        }
    }

    public void Receive(NavegarMensaje_Actualizaciones message)
    {
        _ = NavegarActualizaciones();
    }

    private async Task NavegarActualizaciones()
    {
        try
        {
            var actualizacionesVm = ObtenerActualizaciones();
            VistaActual = actualizacionesVm;
            if (actualizacionesVm.NecesitaRecargar())
            {
                await actualizacionesVm.CargarActualizacionesAsync();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("NavigationService", "Error navegando a actualizaciones", ex);
        }
    }

    public void Receive(NavegarMensaje_Reproductor message)
    {
        _ = NavegarAlReproductorAsync(message);
    }

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

            var viewModel = CrearReproductor();
            if (viewModel == null) return;

            viewModel.AsegurarPlayerInicializado();
            viewModel.EsModoMini = false;
            ReproductorActivo = viewModel;

            try
            {
                await viewModel.CargarVideoAsync(message.RutaVideo, message.AnimeId, message.TituloAnime, message.Episodio, message.EpisodiosDisponibles, message.RutaPortada);
            }
            catch (Exception ex)
            {
                AppLogger.Error("NavigationService", $"Error al cargar el video '{message.RutaVideo}'", ex);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("NavigationService", "Error al procesar NavegarMensaje_Reproductor", ex);
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
            VistaActual = ObtenerGaleria();
        }
    }

    partial void OnVistaActualChanged(ObservableObject? oldValue, ObservableObject newValue)
    {
        if (ReproductorActivo != null && !ReproductorActivo.EsModoMini)
        {
            ReproductorActivo.EsModoMini = true;
        }
    }
}
