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

    // Se guarda al cargar: en Unloaded el DataContext ya puede ser null (la vista se está quitando).
    private VisorRegistrosViewModel? _vm;

    public VisorRegistrosView()
    {
        InitializeComponent();
    }

    private void Vista_Loaded(object sender, RoutedEventArgs e)
    {
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
