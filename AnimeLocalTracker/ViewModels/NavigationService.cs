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


    /// <summary>¿La vista que se está mostrando deja marcada esta pestaña en la barra lateral?</summary>
    bool EstaActiva(Pestana pestana);

    /// <summary>El ViewModel que se muestra al abrir la pestaña (para construir su vista por adelantado).</summary>
    ObservableObject ObtenerVista(Pestana pestana);

    VisorRegistrosViewModel ObtenerVisorRegistros();
    DetalleViewModel CrearDetalle();
    ReproductorViewModel CrearReproductor();
}

public sealed partial class NavigationService : ObservableObject, INavigationService,
    IRecipient<NavegarMensaje_Pestana>,
    IRecipient<NavegarMensaje_Detalle>,
    IRecipient<NavegarMensaje_VisorRegistros>,
    IRecipient<NavegarMensaje_Reproductor>,
    IRecipient<NavegarMensaje_VolverDelReproductor>
{
    private readonly IServiceProvider _serviceProvider;
    private ObservableObject? _vistaAnteriorADetalleCalendario;
    private ObservableObject? _vistaAnteriorAlReproductor;

    [ObservableProperty]
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

    public bool EstaActiva(Pestana pestana) => pestana.MarcadaPor(VistaActual);

    public NavigationService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        WeakReferenceMessenger.Default.RegisterAll(this);
    }

    public ObservableObject ObtenerVista(Pestana pestana) => (ObservableObject)_serviceProvider.GetRequiredService(pestana.Vista);

    public void Receive(NavegarMensaje_Pestana message)
    {
        // Volver a la Biblioteca desde una ficha que se abrió en el Calendario devuelve al Calendario.
        if (message.Pestana == Pestanas.Galeria && _vistaAnteriorADetalleCalendario != null && VistaActual is DetalleViewModel)
        {
            VistaActual = _vistaAnteriorADetalleCalendario;
            _vistaAnteriorADetalleCalendario = null;
            return;
        }

        _ = AbrirPestanaAsync(message.Pestana);
    }

    /// <summary>Muestra la vista de la pestaña y, si tiene algo que hacer al entrar (cargar, refrescar), se lo pide. La vista
    /// queda puesta antes del primer await: quien envía el mensaje ya la ve como actual al volver.</summary>
    private async Task AbrirPestanaAsync(Pestana pestana)
    {
        try
        {
            var vista = ObtenerVista(pestana);
            VistaActual = vista;
            if (vista is IAlEntrarEnPestana alEntrar) await alEntrar.AlEntrarAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Error("NavigationService", $"Error navegando a {pestana.Clave}", ex);
        }
    }

    public VisorRegistrosViewModel ObtenerVisorRegistros() => _serviceProvider.GetRequiredService<VisorRegistrosViewModel>();
    /// <summary>
    /// Una ficha nueva por visita. Se crea con la fábrica registrada en App: pedida directamente al contenedor, este guarda una
    /// referencia a cada instancia desechable hasta que la app se cierra, y ninguna ficha se liberaba nunca.
    /// </summary>
    public DetalleViewModel CrearDetalle() =>
        _serviceProvider.GetService<Func<DetalleViewModel>>()?.Invoke() ?? _serviceProvider.GetRequiredService<DetalleViewModel>();
    public ReproductorViewModel CrearReproductor() => _serviceProvider.GetRequiredService<ReproductorViewModel>();

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
            VistaActual = ObtenerVista(Pestanas.Galeria);
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
