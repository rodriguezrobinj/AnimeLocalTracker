using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;

namespace AnimeLocalTracker.Views;

public partial class DetalleView : UserControl
{
    private DetalleViewModel? _vmObservado;

    public DetalleView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vmObservado != null) _vmObservado.PropertyChanged -= Vm_PropertyChanged;
            _vmObservado = DataContext as DetalleViewModel;
            if (_vmObservado != null) _vmObservado.PropertyChanged += Vm_PropertyChanged;
        };
        Unloaded += (_, _) =>
        {
            if (_vmObservado != null) _vmObservado.PropertyChanged -= Vm_PropertyChanged;
            _vmObservado = null;
        };
    }

    /// <summary>
    /// El calendario del selector de fechas usa el idioma del elemento (por defecto en-US: "October 2024").
    /// Se ajusta al idioma de la app cada vez que se abre el editor de seguimiento.
    /// </summary>
    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DetalleViewModel.MostrandoEditorSeguimiento) && _vmObservado?.MostrandoEditorSeguimiento == true)
        {
            EditorSeguimientoCard.Language = XmlLanguage.GetLanguage(LocalizationService.Cultura.IetfLanguageTag);
        }
    }

    /// <summary>
    /// Clic izquierdo sobre el icono de episodio descargado (check): abre el menú
    /// de acciones de la fila, anclado bajo el propio icono (no en el punto del clic).
    /// </summary>
    private void BotonEpisodioDescargado_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button boton && BuscarFila(boton) is { } fila && fila.ContextMenu is { } menu)
        {
            boton.Tag = fila.Tag;
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
