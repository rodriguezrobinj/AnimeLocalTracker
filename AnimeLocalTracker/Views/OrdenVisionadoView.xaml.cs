using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;

namespace AnimeLocalTracker.Views;

/// <summary>Ventana "Orden de visionado" de la ficha. La crea <see cref="DetalleView"/> la primera vez que se abre.</summary>
public partial class OrdenVisionadoView : UserControl
{
    private OrdenVisionadoFichaViewModel? _observado;

    public OrdenVisionadoView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_observado != null) _observado.FilaDestacadaSolicitada -= Destacar;
            _observado = DataContext as OrdenVisionadoFichaViewModel;
            if (_observado != null) _observado.FilaDestacadaSolicitada += Destacar;
        };
        // El foco dentro de la tarjeta hace que Escape la cierre.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) Dispatcher.BeginInvoke(new Action(() => OrdenCard.Focus()), System.Windows.Threading.DispatcherPriority.Input);
        };
    }

    private void Destacar(FilaOrdenVisionado fila) =>
        Dispatcher.BeginInvoke(new Action(() => ListaOrden.ScrollIntoView(fila)), System.Windows.Threading.DispatcherPriority.Loaded);

    private void Fila_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (e.OriginalSource is DependencyObject origen && EsDentroDeUnBoton(origen)) return; // "Añadir" tiene su comando
            if (sender is FrameworkElement { DataContext: FilaOrdenVisionado fila } && _observado?.AbrirFilaCommand.CanExecute(fila) == true)
                _observado.AbrirFilaCommand.Execute(fila);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("OrdenVisionadoView", $"No se pudo abrir la parte: {ex.Message}");
        }
    }

    private static bool EsDentroDeUnBoton(DependencyObject origen)
    {
        for (var actual = origen; actual != null;
             actual = actual is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(actual) : LogicalTreeHelper.GetParent(actual))
        {
            if (actual is Button) return true;
        }
        return false;
    }

    /// <summary>Un clic en el fondo oscuro (fuera de la tarjeta) cierra la ventana.</summary>
    private void FondoOrden_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, FondoOrden)) return;
        _observado?.CerrarCommand.Execute(null);
        e.Handled = true;
    }
}
