using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;

namespace AnimeLocalTracker.Views;

public partial class VisorRegistrosView : UserControl
{
    /// <summary>Cada cuánto se añade a la vista lo nuevo de la sesión actual mientras está abierta.</summary>
    private static readonly TimeSpan IntervaloEnVivo = TimeSpan.FromSeconds(3);

    private DispatcherTimer? _temporizador;

    private VisorRegistrosViewModel? _vm;

    public VisorRegistrosView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// La vista se conserva al salir (ver AnfitrionVistas): la actualización en vivo solo corre mientras se ve, también si
    /// lo que se oculta es la ventana entera.
    /// </summary>
    private void Vista_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true)
        {
            _temporizador?.Stop();
            return;
        }

        _vm = DataContext as VisorRegistrosViewModel;
        if (_vm == null) return;
        _temporizador ??= new DispatcherTimer(IntervaloEnVivo, DispatcherPriority.Background, Temporizador_Tick, Dispatcher);
        _temporizador.Start();
    }

    private void Vista_Unloaded(object sender, RoutedEventArgs e)
    {
        _temporizador?.Stop();
    }

    private async void Temporizador_Tick(object? sender, EventArgs e)
    {
        try
        {
            if (_vm != null) await _vm.RefrescarEnVivoAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Debug("VisorRegistrosView", $"Error actualizando el visor en vivo: {ex.Message}");
        }
    }
}
