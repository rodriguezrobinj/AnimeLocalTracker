using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Threading;
using AnimeLocalTracker.ViewModels;

namespace AnimeLocalTracker.Views;

/// <summary>
/// Los atajos de teclado de un minijuego solo funcionan con el foco dentro de su vista, y el botón "Jugar" (o "Siguiente")
/// desaparece al cambiar de estado llevándose el foco: este ayudante se lo devuelve a la vista en cada cambio de estado o
/// de ronda, y sube el desplazamiento al inicio. También al responder: tras un clic el foco se queda en la opción pulsada, y
/// un botón con foco se queda con la tecla Enter (no llegaba a "Siguiente"). Se engancha en Loaded y se suelta en Unloaded.
/// </summary>
internal sealed class MinijuegoFocoHelper
{
    private readonly UserControl _vista;
    private readonly ScrollViewer? _scroll;
    private MinijuegoViewModelBase? _viewModel;

    public MinijuegoFocoHelper(UserControl vista, ScrollViewer? scroll = null)
    {
        _vista = vista;
        _scroll = scroll;
        vista.Loaded += (_, _) =>
        {
            Enlazar();
            RecuperarFoco();
        };
        vista.Unloaded += (_, _) => Desenlazar();
        // La Galería (que contiene los minijuegos) se conserva al cambiar de pestaña: al volver no llega Loaded.
        vista.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true && vista.IsLoaded) RecuperarFoco();
        };
    }

    private void Enlazar()
    {
        Desenlazar();
        _viewModel = _vista.DataContext as MinijuegoViewModelBase;
        if (_viewModel != null) _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void Desenlazar()
    {
        if (_viewModel != null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MinijuegoViewModelBase.Estado) or nameof(MinijuegoViewModelBase.RondaNumero))
        {
            // Cada ronda nueva empieza arriba: si la anterior se desplazó para ver todas las pistas, no dejarla a medias.
            _scroll?.ScrollToTop();
            RecuperarFoco();
        }
        else if (e.PropertyName == nameof(MinijuegoViewModelBase.HaRespondido) && _viewModel?.HaRespondido == true)
        {
            RecuperarFoco(aunqueEsteDentro: true);
        }
    }

    private void RecuperarFoco(bool aunqueEsteDentro = false)
    {
        _vista.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (_vista.IsLoaded && _vista.IsVisible && (aunqueEsteDentro || !_vista.IsKeyboardFocusWithin)) _vista.Focus();
        });
    }
}
