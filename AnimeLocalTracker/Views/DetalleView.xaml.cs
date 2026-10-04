using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;

namespace AnimeLocalTracker.Views;

public partial class DetalleView : UserControl
{
    private DetalleViewModel? _vmObservado;

    /// <summary>
    /// Último ViewModel que tuvo esta vista. Al salir de pantalla WPF quita el DataContext heredado ANTES de lanzar Unloaded
    /// (por eso <c>_vmObservado</c> ya es null ahí): la limpieza de Unloaded (parar la música y el contador) debe usar este.
    /// </summary>
    private DetalleViewModel? _ultimoVm;

    public DetalleView()
    {
        var reloj = System.Diagnostics.Stopwatch.StartNew();
        InitializeComponent();
        long msCreada = reloj.ElapsedMilliseconds;
        // La vista se construye una vez (se reutiliza de un anime al siguiente): cuánto costó y cuándo quedó colocada.
        RoutedEventHandler? alCargar = null;
        alCargar = (_, _) =>
        {
            Loaded -= alCargar;
            AppLogger.Debug("DetalleView", $"[Perf] Vista de la ficha: creada en {msCreada} ms, colocada a los {reloj.ElapsedMilliseconds} ms.");
        };
        Loaded += alCargar;
        DataContextChanged += (_, _) =>
        {
            DejarDeObservar();
            _vmObservado = DataContext as DetalleViewModel;
            if (_vmObservado != null)
            {
                // Otro anime en la misma vista: nada de lo que el usuario dejó en la ficha anterior debe seguir ahí.
                if (!ReferenceEquals(_ultimoVm, _vmObservado)) ReiniciarEstadoDeLaVista();
                _ultimoVm = _vmObservado;
                _vmObservado.PropertyChanged += Ficha_PropertyChanged;
                ObservarMusica(_vmObservado.Musica);
                _vmObservado.Seguimiento.PropertyChanged += Seguimiento_PropertyChanged;
            }

            // Aplazado: aquí WPF está en mitad de propagar el DataContext de la ficha, y dárselo ahora mismo a las ventanas ya
            // creadas (otra propagación, anidada) puede cortar la primera y dejar controles sin datos.
            Dispatcher.BeginInvoke(new Action(MostrarVentanasSiCorresponde));
        };
        Loaded += (_, _) => (DataContext as DetalleViewModel)?.ReanudarContador();
        Unloaded += (_, _) =>
        {
            AlSalirDeLaFicha();
            DejarDeObservar();
            _vmObservado = null;
        };
        // La vista se conserva y se reutiliza (ver AnfitrionVistas): al ir a otra pestaña no llega Unloaded, solo deja de
        // verse. Si lo que se oculta es la ventana entera (bandeja), la ficha sigue abierta y no se toca nada.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) (DataContext as DetalleViewModel)?.ReanudarContador();
            else if (Window.GetWindow(this) is not { IsVisible: false }) AlSalirDeLaFicha();
        };
    }

    private void AlSalirDeLaFicha()
    {
        // El contador de próximo episodio no debe seguir corriendo con la ficha oculta.
        _ultimoVm?.DetenerContador();
        // La música de la ficha no debe seguir sonando en otras pestañas (salvo que el usuario lo haya pedido: entonces
        // la propia música sabe que se quedó de fondo y no se corta).
        _ultimoVm?.Musica.AlOcultarLaFicha();
        CerrarMenus();
    }

    private void CerrarMenus()
    {
        AvisosPopup.IsOpen = false;
        MarcarPopup.IsOpen = false;
        HerramientasPopup.IsOpen = false;
    }

    /// <summary>
    /// Deja la vista como recién abierta cuando pasa a mostrar otro anime: menús cerrados, caja "Ir al ep." vacía y las dos
    /// columnas arriba del todo.
    /// </summary>
    private void ReiniciarEstadoDeLaVista()
    {
        CerrarMenus();
        CajaIrAEpisodio.Clear();
        ColumnaPortadaScroll.ScrollToTop();
        ListaEpisodios.SelectedItem = null;
        BuscarDescendiente<ScrollViewer>(ListaEpisodios)?.ScrollToTop();
    }

    private static T? BuscarDescendiente<T>(DependencyObject raiz) where T : DependencyObject
    {
        int hijos = VisualTreeHelper.GetChildrenCount(raiz);
        for (int i = 0; i < hijos; i++)
        {
            var hijo = VisualTreeHelper.GetChild(raiz, i);
            if (hijo is T encontrado) return encontrado;
            if (BuscarDescendiente<T>(hijo) is { } descendiente) return descendiente;
        }
        return null;
    }

    private void DejarDeObservar()
    {
        ObservarMusica(null);
        if (_vmObservado == null) return;

        _vmObservado.PropertyChanged -= Ficha_PropertyChanged;
        _vmObservado.Seguimiento.PropertyChanged -= Seguimiento_PropertyChanged;
    }

    private MusicaFichaViewModel? _musicaObservada;

    private void ObservarMusica(MusicaFichaViewModel? musica)
    {
        if (_musicaObservada != null) _musicaObservada.PropertyChanged -= Musica_PropertyChanged;
        _musicaObservada = musica;
        if (musica != null) musica.PropertyChanged += Musica_PropertyChanged;
    }

    /// <summary>La ficha cambió de música (recibió la que venía sonando de fondo): la ventana de música pasa a ser la suya.</summary>
    private void Ficha_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DetalleViewModel.Musica)) return;
        Core.HiloUi.Ejecutar(() =>
        {
            ObservarMusica(_vmObservado?.Musica);
            MostrarVentanasSiCorresponde();
        });
    }

    // === Ventanas de la ficha (música y editor de seguimiento): cada una vive en su propia vista y se crea la primera vez que
    //     se abre. Construirlas en cada visita era trabajo perdido casi siempre: la mayoría de las veces no se abren. Una vez
    //     creadas se quedan (su visibilidad sigue al ViewModel) y cambian de anime con la ficha. ===

    private void Musica_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MusicaFichaViewModel.MostrandoPanelMusica)) Core.HiloUi.Ejecutar(MostrarVentanasSiCorresponde);
    }

    private void Seguimiento_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SeguimientoEditorViewModel.MostrandoEditorSeguimiento)) Core.HiloUi.Ejecutar(MostrarVentanasSiCorresponde);
    }

    private void MostrarVentanasSiCorresponde()
    {
        var musica = _vmObservado?.Musica;
        if (AnfitrionMusica.Content is PanelMusicaView panelMusica) panelMusica.DataContext = musica;
        else if (musica?.MostrandoPanelMusica == true) AnfitrionMusica.Content = new PanelMusicaView { DataContext = musica };

        var seguimiento = _vmObservado?.Seguimiento;
        if (AnfitrionSeguimiento.Content is EditorSeguimientoView editor) editor.DataContext = seguimiento;
        else if (seguimiento?.MostrandoEditorSeguimiento == true) AnfitrionSeguimiento.Content = new EditorSeguimientoView { DataContext = seguimiento };
    }

    // === Lista de episodios: doble clic o Enter reproducen; "Ir al ep." lleva la lista hasta ese episodio ===

    private void ListaEpisodios_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left) return;
        if (FilaDe(e.OriginalSource as DependencyObject, out bool sobreUnBoton) is not { } episodio || sobreUnBoton) return;

        Reproducir(episodio);
        e.Handled = true;
    }

    private void ListaEpisodios_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        // Solo con el foco en la fila: Enter sobre un botón de la fila ya pulsa ese botón.
        if (e.OriginalSource is not ListBoxItem { DataContext: AnimeLocalTracker.Models.EpisodioItem episodio }) return;

        Reproducir(episodio);
        e.Handled = true;
    }

    private void Reproducir(AnimeLocalTracker.Models.EpisodioItem episodio)
    {
        var comando = _vmObservado?.Episodios.ReproducirEpisodioCommand;
        if (episodio.Descargado && comando?.CanExecute(episodio) == true) comando.Execute(episodio);
    }

    /// <summary>El episodio de la fila que contiene ese elemento (null si no está en una fila), y si el elemento es un botón de la fila.</summary>
    private static AnimeLocalTracker.Models.EpisodioItem? FilaDe(DependencyObject? origen, out bool sobreUnBoton)
    {
        sobreUnBoton = false;
        for (DependencyObject? actual = origen; actual != null;
             actual = actual is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(actual) : LogicalTreeHelper.GetParent(actual))
        {
            if (actual is ButtonBase) sobreUnBoton = true;
            if (actual is ListBoxItem fila) return fila.DataContext as AnimeLocalTracker.Models.EpisodioItem;
        }
        return null;
    }

    private void CajaIrAEpisodio_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
    {
        // Solo dígitos y hasta 4 (el límite se pone aquí y no con MaxLength: Material Design le añade un contador "0 / 4").
        if (sender is TextBox caja && caja.Text.Length - caja.SelectionLength + e.Text.Length > 4) { e.Handled = true; return; }
        foreach (char c in e.Text)
        {
            if (!char.IsDigit(c)) { e.Handled = true; return; }
        }
    }

    private void CajaIrAEpisodio_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter || sender is not TextBox caja) return;

        e.Handled = true;
        if (_vmObservado?.Episodios.BuscarParaIr(caja.Text) is not { } episodio) return;

        ListaEpisodios.SelectedItem = episodio;
        ListaEpisodios.ScrollIntoView(episodio);
        caja.Clear();
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
            menu.HorizontalOffset = 0;
            menu.VerticalOffset = 6;
            menu.Closed += MenuEpisodio_CerradoTrasAbrirConBoton;
            menu.IsOpen = true;
            e.Handled = true;
        }
    }

    /// <summary>
    /// El menú es uno solo para todas las filas. Al cerrarse tras abrirlo desde el botón se le quita el anclaje a ese botón y
    /// vuelve a su colocación de clic derecho: si se quedara anclado, el siguiente clic derecho en OTRA fila abriría el menú
    /// con el episodio de esta.
    /// </summary>
    private static void MenuEpisodio_CerradoTrasAbrirConBoton(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;

        menu.Closed -= MenuEpisodio_CerradoTrasAbrirConBoton;
        menu.ClearValue(ContextMenu.PlacementTargetProperty);
        menu.Placement = PlacementMode.RelativePoint;
        menu.HorizontalOffset = 18;
        menu.VerticalOffset = 12;
    }

    /// <summary>Al elegir una opción de un menú de acciones (Marcar / Herramientas) se cierra su popup; el comando se ejecuta después.</summary>
    private void MenuAcciones_ItemClick(object sender, RoutedEventArgs e)
    {
        for (DependencyObject? actual = sender as DependencyObject; actual != null; actual = LogicalTreeHelper.GetParent(actual) ?? (actual is FrameworkElement fe ? fe.Parent : null))
        {
            if (actual is Popup popup)
            {
                popup.IsOpen = false;
                return;
            }
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
