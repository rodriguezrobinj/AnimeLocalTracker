using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;
using AnimeLocalTracker.ViewModels;

namespace AnimeLocalTracker.Views;

public partial class AdivinaOpEdView : UserControl
{
    private readonly MinijuegoFocoHelper _foco;
    private AdivinaOpEdViewModel? _viewModel;

    public AdivinaOpEdView()
    {
        InitializeComponent();
        _foco = new MinijuegoFocoHelper(this, ScrollPartida);

        Loaded += (_, _) => Enlazar();
        // Al salir de la vista (otra pestaña, volver al menú) el clip deja de sonar.
        Unloaded += (_, _) =>
        {
            (DataContext as MinijuegoViewModelBase)?.Detener();
            Desenlazar();
        };
        // La Galería se conserva al cambiar de pestaña (no llega Unloaded): el clip tampoco debe seguir sonando ahí.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is false) (DataContext as MinijuegoViewModelBase)?.Detener();
        };
    }

    private void Enlazar()
    {
        Desenlazar();
        _viewModel = DataContext as AdivinaOpEdViewModel;
        if (_viewModel != null) _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void Desenlazar()
    {
        if (_viewModel != null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = null;
    }

    /// <summary>La barra de avance recorre el clip mientras suena y vuelve a cero cuando se calla.</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AdivinaOpEdViewModel.EstaSonando) || _viewModel == null) return;

        AvanceClip.BeginAnimation(RangeBase.ValueProperty, null);
        AvanceClip.Value = 0;
        if (_viewModel.EstaSonando && _viewModel.DuracionSonando > System.TimeSpan.Zero)
            AvanceClip.BeginAnimation(RangeBase.ValueProperty, new DoubleAnimation(0, 100, _viewModel.DuracionSonando) { FillBehavior = FillBehavior.Stop });
    }
}
