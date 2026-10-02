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

    /// <summary>El reproductor que se dibuja DENTRO de la ventana principal: el activo en su formato habitual. En modo mini vive en
    /// su propia ventana flotante (ver MainWindow.EntrarModoPiP) y la ventana principal queda libre para navegar.</summary>
    ReproductorViewModel? ReproductorEnVentanaPrincipal { get; }

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
    bool EsVisorRegistrosActivo { get; }

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
    VisorRegistrosViewModel ObtenerVisorRegistros();
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
    IRecipient<NavegarMensaje_VisorRegistros>,
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
    [NotifyPropertyChangedFor(nameof(EsVisorRegistrosActivo))]
    private ObservableObject _vistaActual = null!;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReproductorEnVentanaPrincipal))]
    private ReproductorViewModel? _reproductorActivo;

    public ReproductorViewModel? ReproductorEnVentanaPrincipal => ReproductorActivo is { EsModoMini: false } reproductor ? reproductor : null;

    partial void OnReproductorActivoChanged(ReproductorViewModel? oldValue, ReproductorViewModel? newValue)
    {
        if (oldValue != null) oldValue.PropertyChanged -= ReproductorActivo_PropertyChanged;
        if (newValue != null) newValue.PropertyChanged += ReproductorActivo_PropertyChanged;
    }

    private void ReproductorActivo_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReproductorViewModel.EsModoMini)) OnPropertyChanged(nameof(ReproductorEnVentanaPrincipal));
    }

    public bool EsGaleriaActiva => VistaActual is GaleriaViewModel || VistaActual is DetalleViewModel;
    public bool EsAgregarAnimeActivo => VistaActual is AgregarAnimeViewModel;
    public bool EsCalendarioActivo => VistaActual is CalendarioViewModel;
    public bool EsDescargasActivas => VistaActual is DescargasViewModel;
    // El visor de registros se abre desde Configuración: el botón de Configuración sigue marcado.
    public bool EsConfiguracionActiva => VistaActual is ConfiguracionViewModel || VistaActual is VisorRegistrosViewModel;
    public bool EsAcercaDeActivo => VistaActual is AcercaDeViewModel;
    public bool EsEstadisticasActivo => VistaActual is EstadisticasViewModel;
    public bool EsHistorialActivo => VistaActual is HistorialViewModel;
    public bool EsLogrosActivo => VistaActual is LogrosViewModel;
    public bool EsActualizacionesActivo => VistaActual is ActualizacionesViewModel;
    public bool EsVisorRegistrosActivo => VistaActual is VisorRegistrosViewModel;

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
    public VisorRegistrosViewModel ObtenerVisorRegistros() => _serviceProvider.GetRequiredService<VisorRegistrosViewModel>();
    /// <summary>
    /// Una ficha nueva por visita. Se crea con la fábrica registrada en App: pedida directamente al contenedor, este guarda una
    /// referencia a cada instancia desechable hasta que la app se cierra, y ninguna ficha se liberaba nunca.
    /// </summary>
    public DetalleViewModel CrearDetalle() =>
        _serviceProvider.GetService<Func<DetalleViewModel>>()?.Invoke() ?? _serviceProvider.GetRequiredService<DetalleViewModel>();
    public ReproductorViewModel CrearReproductor() => _serviceProvider.GetRequiredService<ReproductorViewModel>();

    public void Receive(NavegarMensaje_Galeria message)
    {
        if (_vistaAnteriorADetalleCalendario != null && VistaActual is DetalleViewModel)
        {
            VistaActual = _vistaAnteriorADetalleCalendario;
            _vistaAnteriorADetalleCalendario = null;
            return;
        }
        var galeria = ObtenerGaleria();
        VistaActual = galeria;
        _ = AlEntrarEnGaleriaAsync(galeria);
    }

    /// <summary>Si la Galería estaba en su sección de minijuegos, la refresca al volver (animes disponibles y récords).</summary>
    private static async Task AlEntrarEnGaleriaAsync(GaleriaViewModel galeria)
    {
        try
        {
            await galeria.AlEntrarAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Error("NavigationService", "Error al entrar en la galería", ex);
        }
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
            // La ficha anterior (si se venía de otra) se descarta en OnVistaActualChanged al asignar la nueva.
            var detalleVm = CrearDetalle();
            _vistaAnteriorADetalleCalendario = VistaActual is CalendarioViewModel ? VistaActual : null;
            VistaActual = detalleVm;
            await detalleVm.InicializarAsync(message.AnimeSeleccionado);
            if (!string.IsNullOrWhiteSpace(message.ReproducirTemaClave)) await detalleVm.Musica.AbrirMusicaYReproducirAsync(message.ReproducirTemaClave);
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

    public void Receive(NavegarMensaje_VisorRegistros message)
    {
        _ = NavegarVisorRegistros();
    }

    private async Task NavegarVisorRegistros()
    {
        try
        {
            var visor = ObtenerVisorRegistros();
            VistaActual = visor;
            // Lo escrito desde la última visita tiene que aparecer. Si ya estaba abierta la sesión actual basta con añadir lo
            // nuevo (releer el archivo entero y volver a dibujar la lista costaba 0,2-0,5 s en cada visita); la primera vez, o
            // con otro archivo elegido, se relee todo.
            if (visor.SesionActualYaCargada)
            {
                AppLogger.Flush(); // que lo último ya esté en el archivo
                await visor.RefrescarEnVivoAsync();
            }
            else await visor.CargarAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Error("NavigationService", "Error navegando al visor de registros", ex);
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

            // Un episodio empieza a sonar: la música que seguía de fondo se corta.
            _serviceProvider.GetService<IMusicaDeFondoService>()?.Detener();

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
        else if (VistaActual == null)
        {
            VistaActual = ObtenerGaleria();
        }
        // Si no había vista anterior es que el usuario se movió por la app con el mini reproductor abierto: al cerrarlo se queda
        // donde está (antes lo devolvía a la ficha desde la que abrió el episodio).
    }

    partial void OnVistaActualChanged(ObservableObject? oldValue, ObservableObject newValue)
    {
        MedidorRendimiento.MedirNavegacion(oldValue, newValue);

        // Al salir de una ficha (hacia otra ficha o hacia cualquier pestaña) se descarta: cancela sus cargas de fondo, deja de
        // escuchar avisos y suelta el reproductor de música. Antes solo se hacía al pasar de una ficha a otra. Cada visita crea
        // una ficha nueva, así que la anterior no se vuelve a mostrar.
        if (oldValue is DetalleViewModel fichaAnterior && !ReferenceEquals(oldValue, newValue)) fichaAnterior.Dispose();

        if (ReproductorActivo == null) return;

        // Navegar con el reproductor abierto: el episodio sigue en el mini reproductor (su propia ventana) y la app queda libre.
        _vistaAnteriorAlReproductor = null;
        if (!ReproductorActivo.EsModoMini) ReproductorActivo.MinimizarAMini();
    }
}
