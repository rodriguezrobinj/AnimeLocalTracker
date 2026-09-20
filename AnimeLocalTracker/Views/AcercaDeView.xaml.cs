using System.Windows.Controls;
using System.Windows.Input;

namespace AnimeLocalTracker.Views;

public partial class AcercaDeView : UserControl
{
    public AcercaDeView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// El visor de Markdown lleva su propio ScrollViewer interno (sin barra, porque el contenido
    /// se muestra completo) y se tragaba la rueda del ratón: al pasar el cursor sobre el registro
    /// de cambios la página dejaba de desplazarse. Se reenvía al scroll de la página.
    /// </summary>
    private void Novedades_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        PaginaScroll.ScrollToVerticalOffset(PaginaScroll.VerticalOffset - e.Delta);
        e.Handled = true;
    }
}
