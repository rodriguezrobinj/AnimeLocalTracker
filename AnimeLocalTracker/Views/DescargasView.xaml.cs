using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AnimeLocalTracker.Views;

public partial class DescargasView : UserControl
{
    private Window? _ventana;

    public DescargasView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _ventana = Window.GetWindow(this);
            if (_ventana != null) _ventana.PreviewMouseDown += Ventana_PreviewMouseDown;
        };
        Unloaded += (_, _) =>
        {
            if (_ventana != null) _ventana.PreviewMouseDown -= Ventana_PreviewMouseDown;
            _ventana = null;
            MenuOpcionesPopup.IsOpen = false;
        };
    }

    // El popup usa StaysOpen=True y se cierra a mano: con StaysOpen=False, el clic sobre el propio botón ⋮
    // cerraba el popup por captura del ratón y su Click lo volvía a abrir (nunca se podía cerrar con el botón).
    private void MenuOpcionesBtn_Click(object sender, RoutedEventArgs e)
    {
        MenuOpcionesPopup.IsOpen = !MenuOpcionesPopup.IsOpen;
    }

    /// <summary>Un clic en cualquier otro punto de la ventana cierra el menú (el propio botón ⋮ lo gestiona su Click).</summary>
    private void Ventana_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!MenuOpcionesPopup.IsOpen) return;
        if (e.OriginalSource is DependencyObject origen && EsDescendienteDe(origen, MenuOpcionesBtn)) return;
        MenuOpcionesPopup.IsOpen = false;
    }

    private static bool EsDescendienteDe(DependencyObject nodo, DependencyObject ancestro)
    {
        for (var actual = nodo; actual != null; actual = actual is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(actual) : LogicalTreeHelper.GetParent(actual))
        {
            if (ReferenceEquals(actual, ancestro)) return true;
        }
        return false;
    }

    private void CerrarMenuOpciones_Click(object sender, RoutedEventArgs e)
    {
        MenuOpcionesPopup.IsOpen = false;
    }
}
