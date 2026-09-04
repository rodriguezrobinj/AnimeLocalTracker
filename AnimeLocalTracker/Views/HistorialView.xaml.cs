using System.Windows.Controls;

namespace AnimeLocalTracker.Views;

public partial class HistorialView : UserControl
{
    public HistorialView()
    {
        InitializeComponent();
    }

    private void LimpiarBusqueda_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is ViewModels.HistorialViewModel vm)
        {
            vm.TextoBusqueda = string.Empty;
        }
    }
}
