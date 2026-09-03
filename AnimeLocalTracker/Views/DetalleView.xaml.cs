using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace AnimeLocalTracker.Views;

public partial class DetalleView : UserControl
{
    public DetalleView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Clic izquierdo sobre el icono de episodio descargado (check): abre el menú
    /// de acciones de la fila, anclado bajo el propio icono (no en el punto del clic).
    /// </summary>
    private void BotonEpisodioDescargado_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button boton && BuscarFila(boton)?.ContextMenu is { } menu)
        {
            menu.PlacementTarget = boton;
            menu.Placement = PlacementMode.Bottom;
            menu.VerticalOffset = 6;
            menu.IsOpen = true;
            e.Handled = true;
        }
    }

    private static Border? BuscarFila(DependencyObject origen)
    {
        for (DependencyObject? actual = origen; actual != null; actual = VisualTreeHelper.GetParent(actual))
        {
            if (actual is Border fila)
                return fila;
        }
        return null;
    }
}
