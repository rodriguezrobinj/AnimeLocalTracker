using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AnimeLocalTracker.ViewModels;

namespace AnimeLocalTracker.Views;

/// <summary>Ventana flotante de openings y endings de la ficha. La crea <see cref="DetalleView"/> la primera vez que se abre.</summary>
public partial class PanelMusicaView : UserControl
{
    public PanelMusicaView()
    {
        InitializeComponent();
        DataContextChanged += AlCambiarElContexto;

        // El foco dentro de la ventana hace que Escape (KeyBinding de la tarjeta) la cierre.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) Dispatcher.BeginInvoke(new Action(() => PanelMusicaCard.Focus()), System.Windows.Threading.DispatcherPriority.Input);
        };
    }

    private MusicaFichaViewModel? Musica => DataContext as MusicaFichaViewModel;

    private MusicaFichaViewModel? _musicaObservada;

    private void AlCambiarElContexto(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_musicaObservada != null) _musicaObservada.TemaActualSolicitado -= MostrarTema;
        _musicaObservada = Musica;
        if (_musicaObservada != null) _musicaObservada.TemaActualSolicitado += MostrarTema;
    }

    /// <summary>Lleva la lista hasta el tema que suena (se pulsó su título en la barra de "sonando ahora").</summary>
    private void MostrarTema(TemaAnimeItem tema) => ListaTemas.ScrollIntoView(tema);

    // === Barra de progreso de la música: ver TemaAnimeItem.Sincronizando/Arrastrando ===

    /// <summary>
    /// Cualquier cambio de la barra que no venga de la sincronización con el reproductor es del usuario (arrastre, clic en
    /// la barra o teclado): salta a ese punto ahora mismo, así que arrastrar "escucha" el audio mientras se mueve.
    /// </summary>
    private void BarraTema_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is not Slider barra || barra.DataContext is not TemaAnimeItem tema || tema.Sincronizando) return;
        Musica?.BuscarTema(tema, e.NewValue);
    }

    private void BarraTema_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (sender is Slider { DataContext: TemaAnimeItem tema }) tema.Arrastrando = true;
    }

    private void BarraTema_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (sender is Slider { DataContext: TemaAnimeItem tema }) tema.Arrastrando = false;
    }

    /// <summary>Un clic en el fondo oscuro (fuera de la tarjeta) cierra la ventana de música.</summary>
    private void FondoMusica_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, FondoMusica)) return;
        if (Musica?.ToggleMusicaCommand.CanExecute(null) == true) Musica.ToggleMusicaCommand.Execute(null);
        e.Handled = true;
    }
}
