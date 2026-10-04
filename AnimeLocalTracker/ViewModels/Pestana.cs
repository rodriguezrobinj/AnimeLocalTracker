using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Services;

namespace AnimeLocalTracker.ViewModels;

/// <summary>
/// Una pestaña de la barra lateral: qué icono y texto lleva, si va en el grupo de abajo y qué vistas la dejan marcada como
/// activa. La primera vista es la que se muestra al abrirla; las demás solo la marcan (la Biblioteca sigue marcada dentro de
/// una ficha; Configuración, dentro del visor de registros).
/// </summary>
public sealed record Pestana(string Clave, string Icono, string ClaveTexto, bool Inferior, Type[] Vistas)
{
    /// <summary>Tipo del ViewModel que se muestra al abrirla (registrado en DI, con su DataTemplate en App.xaml).</summary>
    public Type Vista => Vistas[0];

    public bool MarcadaPor(object? vista) => vista != null && Array.IndexOf(Vistas, vista.GetType()) >= 0;

    /// <summary>Navega a esta pestaña desde cualquier parte de la app.</summary>
    public void Abrir() => WeakReferenceMessenger.Default.Send(new NavegarMensaje_Pestana(this));
}

/// <summary>
/// Lo implementa el ViewModel de una pestaña que tiene algo que hacer cada vez que se entra en ella (cargar si los datos
/// caducaron, refrescar…). <see cref="NavigationService"/> lo llama justo después de mostrarla, sin conocer la pestaña.
/// </summary>
public interface IAlEntrarEnPestana
{
    Task AlEntrarAsync();
}

/// <summary>
/// La tabla de pestañas, en el orden en que aparecen en la barra lateral. Añadir una pestaña es añadir aquí su fila, registrar
/// su ViewModel en DI y darle un DataTemplate en App.xaml (ver ui-wpf-vistas.md, punto 1).
/// </summary>
public static class Pestanas
{
    public static readonly Pestana Galeria = new("Galeria", "ViewGallery", "Nav_Galeria", false, [typeof(GaleriaViewModel), typeof(DetalleViewModel)]);
    public static readonly Pestana Historial = new("Historial", "History", "Nav_Historial", false, [typeof(HistorialViewModel)]);
    public static readonly Pestana Actualizaciones = new("Actualizaciones", "NewReleases", "Nav_Actualizaciones", false, [typeof(ActualizacionesViewModel)]);
    public static readonly Pestana AgregarAnime = new("AgregarAnime", "MagnifyPlus", "Nav_Agregar", false, [typeof(AgregarAnimeViewModel)]);
    public static readonly Pestana Calendario = new("Calendario", "CalendarClock", "Nav_Calendario", false, [typeof(CalendarioViewModel)]);
    public static readonly Pestana Estadisticas = new("Estadisticas", "ChartBar", "Nav_Estadisticas", false, [typeof(EstadisticasViewModel)]);
    public static readonly Pestana Logros = new("Logros", "TrophyOutline", "Nav_Logros", false, [typeof(LogrosViewModel)]);
    public static readonly Pestana Descargas = new("Descargas", "DownloadMultiple", "Nav_Descargas", false, [typeof(DescargasViewModel)]);
    public static readonly Pestana Configuracion = new("Configuracion", "Cog", "Nav_Configuracion", true, [typeof(ConfiguracionViewModel), typeof(VisorRegistrosViewModel)]);
    public static readonly Pestana AcercaDe = new("AcercaDe", "InformationOutline", "Nav_AcercaDe", true, [typeof(AcercaDeViewModel)]);

    public static IReadOnlyList<Pestana> Todas { get; } =
        [Galeria, Historial, Actualizaciones, AgregarAnime, Calendario, Estadisticas, Logros, Descargas, Configuracion, AcercaDe];
}

/// <summary>Lo que pinta un botón de la barra lateral (plantilla BotonPestana de MainWindow.xaml).</summary>
public sealed partial class PestanaItemViewModel(Pestana pestana, int orden) : ObservableObject
{
    public Pestana Pestana { get; } = pestana;

    /// <summary>Posición en la barra, empezando en 1: es el orden de tabulación del botón.</summary>
    public int Orden { get; } = orden;

    public string Icono => Pestana.Icono;

    /// <summary>Nombre de la pestaña en el idioma de la app (ToolTip y nombre accesible del botón).</summary>
    public string Texto => LocalizationService.T(Pestana.ClaveTexto);

    [ObservableProperty]
    private bool _esActiva;

    /// <summary>Número sobre el botón (descargas en curso). Con 0 no se muestra.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TieneInsignia))]
    private int _insignia;

    public bool TieneInsignia => Insignia > 0;

    [RelayCommand]
    private void Abrir() => Pestana.Abrir();

    /// <summary>Tras un cambio de idioma.</summary>
    public void RefrescarTexto() => OnPropertyChanged(nameof(Texto));
}
