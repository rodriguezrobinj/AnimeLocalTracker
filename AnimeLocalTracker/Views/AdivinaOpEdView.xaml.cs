using System.Windows.Controls;
using AnimeLocalTracker.ViewModels;

namespace AnimeLocalTracker.Views;

public partial class AdivinaOpEdView : UserControl
{
    private readonly MinijuegoFocoHelper _foco;

    public AdivinaOpEdView()
    {
        InitializeComponent();
        _foco = new MinijuegoFocoHelper(this, ScrollPartida);

        // Al salir de la vista (otra pestaña, volver al menú) el clip deja de sonar.
        Unloaded += (_, _) => (DataContext as MinijuegoViewModelBase)?.Detener();
        // La Galería se conserva al cambiar de pestaña (no llega Unloaded): el clip tampoco debe seguir sonando ahí.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is false) (DataContext as MinijuegoViewModelBase)?.Detener();
        };
    }
}
