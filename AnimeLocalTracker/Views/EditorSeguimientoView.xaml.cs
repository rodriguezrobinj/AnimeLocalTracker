using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;

namespace AnimeLocalTracker.Views;

/// <summary>
/// Editor de seguimiento de AniList de la ficha y su selector de fecha. Lo crea <see cref="DetalleView"/> la primera vez que
/// se abre.
/// </summary>
public partial class EditorSeguimientoView : UserControl
{
    private SeguimientoEditorViewModel? _vm;

    public EditorSeguimientoView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Observar(DataContext as SeguimientoEditorViewModel);
        Loaded += (_, _) =>
        {
            Observar(DataContext as SeguimientoEditorViewModel);

            // La vista se crea cuando el editor YA se ha pedido: ese aviso no vuelve a llegar. Se hace aquí y NO al recibir el
            // DataContext: cambiar el idioma (propiedad heredable) en mitad de la propagación del DataContext la cortaba, y
            // los campos de texto del editor se quedaban sin datos (vacíos).
            if (_vm?.MostrandoEditorSeguimiento == true) AjustarIdiomaDelEditor();
            if (_vm?.MostrandoCalendarioFecha == true) PrepararCalendario();
        };
        Unloaded += (_, _) => Observar(null);
    }

    private void Observar(SeguimientoEditorViewModel? vm)
    {
        if (ReferenceEquals(_vm, vm)) return;

        if (_vm != null) _vm.PropertyChanged -= Vm_PropertyChanged;
        _vm = vm;
        if (_vm != null) _vm.PropertyChanged += Vm_PropertyChanged;
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SeguimientoEditorViewModel.MostrandoEditorSeguimiento) && _vm?.MostrandoEditorSeguimiento == true)
        {
            AjustarIdiomaDelEditor();
        }
        else if (e.PropertyName == nameof(SeguimientoEditorViewModel.MostrandoCalendarioFecha))
        {
            if (_vm?.MostrandoCalendarioFecha == true) PrepararCalendario();
            else if (_vm?.MostrandoEditorSeguimiento == true) DarFoco(EditorSeguimientoCard); // de vuelta al editor: su Escape vuelve a valer
        }
    }

    /// <summary>
    /// El calendario del selector de fechas usa el idioma del elemento (por defecto en-US: "October 2024").
    /// Se ajusta al idioma de la app cada vez que se abre el editor de seguimiento.
    /// </summary>
    private void AjustarIdiomaDelEditor()
    {
        EditorSeguimientoCard.Language = XmlLanguage.GetLanguage(LocalizationService.Cultura.IetfLanguageTag);
        DarFoco(EditorSeguimientoCard);
    }

    /// <summary>El foco dentro de la tarjeta hace que su atajo de Escape la cierre.</summary>
    private void DarFoco(UIElement tarjeta) =>
        Dispatcher.BeginInvoke(new Action(() => tarjeta.Focus()), System.Windows.Threading.DispatcherPriority.Input);

    /// <summary>Un clic en el fondo oscuro (fuera de la tarjeta) cierra el calendario.</summary>
    private void FondoCalendario_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, FondoCalendario)) return;
        if (_vm?.CerrarCalendarioFechaCommand.CanExecute(null) == true) _vm.CerrarCalendarioFechaCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>Cada vez que se abre: idioma de la app, vista de días y el mes de la fecha ya elegida (o de hoy).</summary>
    private void PrepararCalendario()
    {
        if (_vm == null) return;

        CalendarioFechaCard.Language = XmlLanguage.GetLanguage(LocalizationService.Cultura.IetfLanguageTag);
        var fecha = _vm.CalendarioFechaInicial;
        CalendarioFecha.DisplayMode = CalendarMode.Month;
        CalendarioFecha.SelectedDate = _vm.CalendarioTieneFecha ? fecha : null;
        CalendarioFecha.DisplayDate = fecha;
        DarFoco(CalendarioFechaCard);
    }

    /// <summary>
    /// El título del calendario avanza mes → año → década (comportamiento nativo). En la década su botón queda
    /// deshabilitado y no había forma de volver: ahora pulsar el título en esa vista regresa a los días del mes.
    /// </summary>
    private void CalendarioTitulo_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not Calendar calendario || calendario.DisplayMode != CalendarMode.Decade) return;

        // Se comprueba por posición: con el botón deshabilitado, el origen del clic no es fiable (llega la rejilla de fondo).
        var item = calendario.Template?.FindName("PART_CalendarItem", calendario) as Control;
        var cabecera = item?.Template?.FindName("PART_HeaderButton", item) as FrameworkElement;
        if (cabecera == null || !cabecera.IsVisible) return;

        var zona = cabecera.TransformToAncestor(calendario).TransformBounds(new Rect(0, 0, cabecera.ActualWidth, cabecera.ActualHeight));
        if (zona.Contains(e.GetPosition(calendario)))
        {
            calendario.DisplayMode = CalendarMode.Month;
            e.Handled = true;
        }
    }

    /// <summary>
    /// Un clic en un día aplica la fecha y cierra la tarjeta. Se resuelve aquí (y no con SelectedDate) para que
    /// también funcione al pulsar el día que ya estaba seleccionado, que no genera cambio de selección.
    /// </summary>
    private void CalendarioFecha_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        for (DependencyObject? actual = e.OriginalSource as DependencyObject; actual != null && !ReferenceEquals(actual, CalendarioFecha); actual = VisualTreeHelper.GetParent(actual))
        {
            if (actual is System.Windows.Controls.Primitives.CalendarDayButton { DataContext: DateTime dia })
            {
                if (_vm?.ElegirFechaCalendarioCommand.CanExecute(dia) == true)
                    _vm.ElegirFechaCalendarioCommand.Execute(dia);
                e.Handled = true;
                return;
            }
        }
    }
}
