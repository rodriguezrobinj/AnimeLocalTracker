using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AnimeLocalTracker.Controls;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using MaterialDesignThemes.Wpf;

namespace AnimeLocalTracker.Views
{
    // Vista del reproductor: aloja la superficie de video del motor (libmpv) y pone los controles encima como WPF normal.
    [System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
    public partial class ReproductorView : UserControl
    {
        private DispatcherTimer _fadeTimer;
        private bool _controlsVisible = true;
        private Point _lastMousePosition;

        // Aviso propio del reproductor (ver ToastReproductor en el XAML)
        private IDialogService? _dialogoToast;
        private bool _toastMostrado;

        public ReproductorView()
        {
            InitializeComponent();

            _menus = new[]
            {
                (SubtitlesPopup, SubtitlesButton, SubtitlesPopupFlecha),
                (AudioPopup, AudioButton, AudioPopupFlecha),
                (MoreOptionsPopup, MoreOptionsButton, MoreOptionsPopupFlecha),
                (EqualizerPopup, EqualizerButton, EqualizerPopupFlecha),
            };

            _fadeTimer = new DispatcherTimer(DispatcherPriority.Background);
            _fadeTimer.Interval = TimeSpan.FromSeconds(3);
            _fadeTimer.Tick += FadeTimer_Tick;

            // PreProcessInput se suscribe en Loaded (no en el constructor): solo interesa mientras la vista está en pantalla.
            this.Loaded += ReproductorView_Loaded;
            this.Unloaded += ReproductorView_Unloaded;
            this.IsVisibleChanged += ReproductorView_IsVisibleChanged;
            this.DataContextChanged += ReproductorView_DataContextChanged;
            this.SizeChanged += (_, _) => ColocarSubtitulos();
        }

        private void ReproductorView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (OverlayControls != null)
            {
                OverlayControls.DataContext = this.DataContext;
            }

            EnlazarToast((e.NewValue as ReproductorViewModel)?.DialogService);
            EnlazarCuentaAtras(e.NewValue as ReproductorViewModel);
            EnlazarSubtitulos(e.NewValue as ReproductorViewModel);
        }

        // === El reproductor al que mira esta vista (el nombre viene de cuando la vista dibujaba los subtítulos; ahora los
        // dibuja libmpv dentro del video y la vista solo aloja la superficie y sigue el resto del estado). ===
        private ReproductorViewModel? _vmSubtitulos;

        private void EnlazarSubtitulos(ReproductorViewModel? vm)
        {
            if (!ReferenceEquals(_vmSubtitulos, vm))
            {
                if (_vmSubtitulos != null) _vmSubtitulos.PropertyChanged -= VmSubtitulos_PropertyChanged;
                _vmSubtitulos = vm;
                if (_vmSubtitulos != null) _vmSubtitulos.PropertyChanged += VmSubtitulos_PropertyChanged;

                ActualizarTapaVideo();
            }
            AlojarSuperficie();
        }

        /// <summary>
        /// Trae a esta vista la superficie de video del motor. Es una sola por motor: al pasar al mini reproductor (otra ventana, otra
        /// instancia de esta vista) se mueve en vez de crearse otra, porque soltar el pintor de libmpv deja el video 1 s en negro.
        /// Solo la pide una vista cargada: la que se está yendo no se la quita a la que llega.
        /// </summary>
        private void AlojarSuperficie()
        {
            if (IsLoaded && _vmSubtitulos?.Motor is MotorMpv { Desechado: false } motor) SuperficieMpv.Para(motor).MoverA(HuecoVideo);
            ColocarSubtitulos();
        }

        /// <summary>Lo que los subtítulos ya dejan libre por debajo, en % del alto (el margen de fábrica de libmpv: 22 de 720).</summary>
        private const double MargenPropioDeLosSubtitulos = 3;

        /// <summary>
        /// Los subtítulos los dibuja libmpv dentro del video, así que los controles quedan encima. Mientras están a la vista, los
        /// diálogos suben justo por encima de la barra de progreso; al ocultarse vuelven a su sitio.
        /// </summary>
        private void ColocarSubtitulos()
        {
            if (!IsLoaded || _vmSubtitulos?.Motor is not MotorMpv { Desechado: false } motor) return;

            double alto = HuecoVideo.ActualHeight, posicion = 100;
            if (_controlsVisible && alto > 0)
            {
                double arriba = alto - 50; // el mini reproductor: su barra inferior mide 50
                try
                {
                    if (ProgressBarArea.IsVisible) arriba = ProgressBarArea.TranslatePoint(new Point(0, 0), HuecoVideo).Y;
                }
                catch (InvalidOperationException)
                {
                    // Sin ancestro común (aún sin mostrar): se queda la barra del mini
                }
                posicion = 100 * arriba / alto + MargenPropioDeLosSubtitulos;
            }
            motor.ColocarSubtitulos(posicion);
        }

        private void ActualizarTapaVideo() =>
            TapaVideo.Visibility = _vmSubtitulos?.OcultarVideoInicio == true ? Visibility.Visible : Visibility.Collapsed;

        // === Un solo menú abierto a la vez (subtítulos, audio, más opciones y el cajón de episodios) ===
        // Antes se podían abrir todos a la vez, uno encima de otro.

        private void AlternarMenu(System.Windows.Controls.Primitives.Popup menu)
        {
            bool abrir = !menu.IsOpen;
            CerrarMenus();
            if (!abrir) return;
            if (DataContext is ReproductorViewModel { CajonEpisodiosAbierto: true } vm) vm.CajonEpisodiosAbierto = false;
            menu.IsOpen = true;
        }

        private void CerrarMenus()
        {
            foreach (var (menu, _, _) in _menus) menu.IsOpen = false;
        }

        /// <summary>Cada menú con el botón que lo abre y la flecha que apunta a ese botón.</summary>
        private readonly (Popup Menu, Button Boton, System.Windows.Shapes.Path Flecha)[] _menus;

        private bool HayMenuAbierto() => System.Linq.Enumerable.Any(_menus, m => m.Menu.IsOpen);

        // === Posición de los menús ===
        // Antes salían desplazados a la derecha del botón, sin nada que dijera de qué botón eran, y tapando la barra de progreso.
        // Ahora cada menú se centra sobre su botón (sin salirse de la ventana), queda por ENCIMA de la barra de progreso, lleva una
        // flecha que apunta a su botón y el botón queda pintado en azul mientras su menú está abierto.

        private void Menu_Opened(object? sender, EventArgs e)
        {
            if (sender is not Popup menu) return;
            ActualizarBotonesActivos();
            ColocarMenu(menu);
            // Segunda pasada ya con el menú medido y colocado en pantalla (la primera evita que se vea un instante fuera de sitio)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ColocarMenu(menu));
        }

        private void Menu_Closed(object? sender, EventArgs e) => ActualizarBotonesActivos();

        private void ColocarMenu(Popup menu)
        {
            if (!menu.IsOpen || menu.Child is not FrameworkElement contenido) return;
            var (_, boton, flecha) = System.Linq.Enumerable.First(_menus, m => ReferenceEquals(m.Menu, menu));

            double ancho = contenido.ActualWidth;
            if (ancho <= 0)
            {
                contenido.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                ancho = contenido.DesiredSize.Width;
            }

            double desplazamiento = (boton.ActualWidth - ancho) / 2;
            if (Window.GetWindow(boton) is Window ventana && ventana.ActualWidth > 0)
            {
                // Sin salirse de la ventana del reproductor (el de Más opciones y el ecualizador están pegados al borde derecho)
                double xBoton = boton.TranslatePoint(new Point(0, 0), ventana).X;
                double minimo = 8 - xBoton;
                double maximo = ventana.ActualWidth - 8 - ancho - xBoton;
                desplazamiento = maximo < minimo ? minimo : Math.Clamp(desplazamiento, minimo, maximo);
            }
            if (Math.Abs(menu.HorizontalOffset - desplazamiento) > 0.5) menu.HorizontalOffset = desplazamiento;

            // En vertical: el borde inferior del menú (con su flecha) justo por encima de la barra de progreso, no encima de ella.
            double subida = -4;
            try
            {
                if (ProgressBarArea.IsVisible) subida = Math.Min(subida, ProgressBarArea.TranslatePoint(new Point(0, 0), boton).Y - 6);
            }
            catch (InvalidOperationException)
            {
                // Sin ancestro común (aún sin mostrar): se queda pegado al botón
            }
            if (Math.Abs(menu.VerticalOffset - subida) > 0.5) menu.VerticalOffset = subida;

            double xFlecha = boton.ActualWidth / 2 - desplazamiento - flecha.Width / 2;
            try
            {
                // Con el menú ya en pantalla se mide de verdad (por si Windows lo empujó para que quepa en el monitor)
                if (PresentationSource.FromVisual(contenido) != null && PresentationSource.FromVisual(boton) != null)
                {
                    var escala = VisualTreeHelper.GetDpi(contenido).DpiScaleX;
                    var centroBoton = boton.PointToScreen(new Point(boton.ActualWidth / 2, 0));
                    var origenMenu = contenido.PointToScreen(new Point(0, 0));
                    xFlecha = (centroBoton.X - origenMenu.X) / escala - flecha.Width / 2;
                }
            }
            catch (InvalidOperationException)
            {
                // Aún sin ventana: se queda la posición calculada
            }
            flecha.Margin = new Thickness(Math.Clamp(xFlecha, 10, Math.Max(10, ancho - flecha.Width - 10)), 0, 0, 0);
        }

        /// <summary>El botón cuyo menú (o cajón de episodios) está abierto se pinta en azul (estilo PlayerMenuToolButton).</summary>
        private void ActualizarBotonesActivos()
        {
            foreach (var (_, boton, _) in _menus)
                boton.Tag = System.Linq.Enumerable.Any(_menus, m => ReferenceEquals(m.Boton, boton) && m.Menu.IsOpen) ? "activo" : null;
            CajonButton.Tag = _vmSubtitulos?.CajonEpisodiosAbierto == true ? "activo" : null;
        }

        private void VmSubtitulos_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ReproductorViewModel.Motor))
            {
                Core.HiloUi.Ejecutar(AlojarSuperficie);
                return;
            }

            if (e.PropertyName == nameof(ReproductorViewModel.CajonEpisodiosAbierto))
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ActualizarBotonesActivos);
                if (_vmSubtitulos?.CajonEpisodiosAbierto == true)
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => CerrarMenus());
                    Dispatcher.BeginInvoke(DispatcherPriority.Loaded, MostrarEpisodioActualEnCajon);
                }
                return;
            }

            if (e.PropertyName == nameof(ReproductorViewModel.OcultarVideoInicio))
            {
                Core.HiloUi.Ejecutar(ActualizarTapaVideo);
                return;
            }
        }

        /// <summary>
        /// Al abrir el cajón de episodios, la lista se coloca en el episodio que se está viendo (antes empezaba siempre arriba:
        /// en One Piece, viendo el 1180, había que bajar más de mil filas para encontrarlo).
        /// </summary>
        private void MostrarEpisodioActualEnCajon()
        {
            var vm = _vmSubtitulos;
            if (vm == null || ListaCajonEpisodios == null) return;

            var actual = System.Linq.Enumerable.FirstOrDefault(vm.EpisodiosDelCajon, ep => ep.NumeroEpisodio == vm.Episodio);
            if (actual == null) return;

            ListaCajonEpisodios.UpdateLayout();
            ListaCajonEpisodios.ScrollIntoView(actual);
        }

        /// <summary>Se suscribe a los cambios del toast de IDialogService (y se desuscribe del anterior).</summary>
        private void EnlazarToast(IDialogService? dialogo)
        {
            if (ReferenceEquals(_dialogoToast, dialogo)) return;

            if (_dialogoToast != null) _dialogoToast.PropertyChanged -= DialogoToast_PropertyChanged;
            _dialogoToast = dialogo;
            if (_dialogoToast != null)
            {
                _dialogoToast.PropertyChanged += DialogoToast_PropertyChanged;
                ActualizarToast();
            }
        }

        private void DialogoToast_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is not (nameof(IDialogService.ToastVisible) or nameof(IDialogService.ToastTitulo)
                or nameof(IDialogService.ToastMensaje) or nameof(IDialogService.ToastIcono) or nameof(IDialogService.ToastColor)
                or nameof(IDialogService.ToastDelReproductor)))
            {
                return;
            }

            Core.HiloUi.Ejecutar(ActualizarToast);
        }

        private void ActualizarToast()
        {
            var dialogo = _dialogoToast;
            if (dialogo == null) return;

            // Solo los avisos del propio reproductor (auto-tracking, reanudar, fin de episodio…). Antes se dibujaba cualquier aviso
            // de la app: una descarga que terminaba aparecía encima del episodio.
            bool visible = dialogo.ToastVisible && dialogo.ToastDelReproductor;
            if (visible)
            {
                ToastRepTitulo.Text = dialogo.ToastTitulo;
                ToastRepMensaje.Text = dialogo.ToastMensaje;
                ToastRepIcono.Kind = Enum.TryParse<PackIconKind>(dialogo.ToastIcono, out var icono) ? icono : PackIconKind.InformationOutline;
                try
                {
                    ToastRepIcono.Foreground = (Brush)new BrushConverter().ConvertFromString(dialogo.ToastColor)!;
                }
                catch (Exception)
                {
                    ToastRepIcono.Foreground = Brushes.White;
                }
            }

            // Solo se anima al cambiar de visible a oculto (o al revés), no en cada cambio de texto.
            if (visible == _toastMostrado) return;
            _toastMostrado = visible;
            ToastReproductor.BeginAnimation(OpacityProperty, new DoubleAnimation(_toastMostrado ? 1 : 0, TimeSpan.FromMilliseconds(300)));
        }

        // === Cuenta atrás de auto-play al siguiente episodio: mismo patrón que EnlazarToast/ActualizarToast
        // (ver el comentario del XAML) ===
        private ReproductorViewModel? _vmCuentaAtras;
        private bool _cuentaAtrasMostrada;

        private void EnlazarCuentaAtras(ReproductorViewModel? vm)
        {
            if (ReferenceEquals(_vmCuentaAtras, vm)) return;

            if (_vmCuentaAtras != null) _vmCuentaAtras.PropertyChanged -= VmCuentaAtras_PropertyChanged;
            _vmCuentaAtras = vm;
            if (_vmCuentaAtras != null)
            {
                _vmCuentaAtras.PropertyChanged += VmCuentaAtras_PropertyChanged;
                ActualizarCuentaAtras();
            }
        }

        private void VmCuentaAtras_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is not (nameof(ReproductorViewModel.MostrarCuentaAtrasSiguiente)
                or nameof(ReproductorViewModel.SegundosCuentaAtrasSiguiente)
                or nameof(ReproductorViewModel.TituloSiguienteEnCuentaAtras)
                or nameof(ReproductorViewModel.AvisoSaltoSiguiente)))
            {
                return;
            }

            Core.HiloUi.Ejecutar(ActualizarCuentaAtras);
        }

        private void ActualizarCuentaAtras()
        {
            var vm = _vmCuentaAtras;
            if (vm == null) return;

            CuentaAtrasTitulo.Text = vm.TituloSiguienteEnCuentaAtras;
            CuentaAtrasAvisoTexto.Text = vm.AvisoSaltoSiguiente;
            CuentaAtrasAviso.Visibility = string.IsNullOrEmpty(vm.AvisoSaltoSiguiente) ? Visibility.Collapsed : Visibility.Visible;
            CuentaAtrasSegundosTexto.Text = $"{vm.SegundosCuentaAtrasSiguiente}s";

            if (vm.MostrarCuentaAtrasSiguiente == _cuentaAtrasMostrada) return;
            _cuentaAtrasMostrada = vm.MostrarCuentaAtrasSiguiente;
            CuentaAtrasSiguiente.IsHitTestVisible = _cuentaAtrasMostrada;
            CuentaAtrasSiguiente.BeginAnimation(OpacityProperty, new DoubleAnimation(_cuentaAtrasMostrada ? 1 : 0, TimeSpan.FromMilliseconds(250)));
        }

        private void BtnCancelarAutoPlay_Click(object sender, RoutedEventArgs e)
        {
            (DataContext as ReproductorViewModel)?.CancelarAutoPlayCommand.Execute(null);
        }

        private void BtnReproducirAhoraCuentaAtras_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not ReproductorViewModel vm) return;
            vm.CancelarAutoPlayCommand.Execute(null);
            vm.SiguienteEpisodioCommand.Execute(null);
        }

        private void ReproductorView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is bool isVisible && !isVisible)
            {
                Mouse.OverrideCursor = null;
            }
        }

        private void ReproductorView_Loaded(object sender, RoutedEventArgs e)
        {
            EnlazarToast((DataContext as ReproductorViewModel)?.DialogService);
            EnlazarSubtitulos(DataContext as ReproductorViewModel);

            // Suscribir al pipeline global de input mientras la vista está cargada (se quita en Unloaded).
            InputManager.Current.PreProcessInput += InputManager_PreProcessInput;
            EscucharTeclasDe(Window.GetWindow(this));
            _fadeTimer.Start();

            // Restaurar el foco con prioridad baja, con la vista ya colocada
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (IsLoaded && IsVisible)
                    this.Focus();
            });
        }

        private void ReproductorView_Unloaded(object sender, RoutedEventArgs e)
        {
            EnlazarToast(null);
            _toastMostrado = false;
            ToastReproductor.BeginAnimation(OpacityProperty, null);
            ToastReproductor.Opacity = 0;

            EnlazarSubtitulos(null);
            EnlazarCuentaAtras(null);
            _cuentaAtrasMostrada = false;
            CuentaAtrasSiguiente.BeginAnimation(OpacityProperty, null);
            CuentaAtrasSiguiente.Opacity = 0;
            InputManager.Current.PreProcessInput -= InputManager_PreProcessInput;
            EscucharTeclasDe(null);
            _fadeTimer.Stop();
            Mouse.OverrideCursor = null;

            // StaysOpen=True: cerrar manualmente al salir del reproductor
            CerrarMenus();
        }

        /// <summary>La app está activa si lo está la ventana principal o la que contiene esta vista.</summary>
        private bool AppActiva() =>
            (Application.Current?.MainWindow?.IsActive ?? false)
            || (Window.GetWindow(this)?.IsActive ?? false);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetCursorPos(out PuntoNativo punto);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(PuntoNativo punto);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr ventana, out uint proceso);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr ventana, uint tipo);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct PuntoNativo { public int X; public int Y; }

        /// <summary>
        /// True si el puntero está sobre el propio reproductor: su ventana (la principal, o la del mini reproductor).
        /// Fuera —barra de tareas, otro programa o, con el mini abierto, el resto de la app— ni se
        /// despiertan sus controles ni se oculta el cursor: antes, navegando por la app con el mini abierto, el cursor desaparecía.
        /// </summary>
        private bool PunteroSobreElReproductor()
        {
            try
            {
                if (!GetCursorPos(out var punto)) return false;
                IntPtr raiz = GetAncestor(WindowFromPoint(punto), 2); // GA_ROOT
                if (raiz == IntPtr.Zero) return false;

                return raiz == Manejador(Window.GetWindow(this));
            }
            catch
            {
                return false;
            }

            static IntPtr Manejador(Window? ventana) =>
                ventana == null ? IntPtr.Zero : new System.Windows.Interop.WindowInteropHelper(ventana).Handle;
        }

        private void InputManager_PreProcessInput(object sender, PreProcessInputEventArgs e)
        {
            // Filtro barato: descartamos cualquier evento que no sea de mouse
            if (e.StagingItem.Input is not MouseEventArgs mouseArgs)
                return;

            // Solo si la app está activa. El mini reproductor no se activa al pasar el ratón
            // por encima mientras se usa otra ventana: ahí basta con que el puntero esté sobre él.
            bool esMini = DataContext is ReproductorViewModel { EsModoMini: true };
            if (!AppActiva() && !esMini)
                return;

            try
            {
                if (mouseArgs.RoutedEvent == Mouse.MouseMoveEvent)
                {
                    if (!IsLoaded || PresentationSource.FromVisual(this) == null)
                        return;

                    Point currentPosition = mouseArgs.GetPosition(this);

                    // Ignorar micro-temblores
                    if (Math.Abs(currentPosition.X - _lastMousePosition.X) < 2 &&
                        Math.Abs(currentPosition.Y - _lastMousePosition.Y) < 2)
                    {
                        return;
                    }

                    _lastMousePosition = currentPosition;

                    // Con el mini abierto, moverse por el resto de la app no debe despertar sus controles.
                    if (!PunteroSobreElReproductor())
                        return;

                    RegistrarActividad();
                }
                else if (mouseArgs.RoutedEvent == Mouse.MouseDownEvent || mouseArgs.RoutedEvent == Mouse.MouseWheelEvent)
                {
                    RegistrarActividad();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("ReproductorView", $"Excepción transitoria en hook de eventos de mouse: {ex.Message}");
            }
        }

        private void RegistrarActividad()
        {
            if (!_controlsVisible)
            {
                MostrarControles();
            }

            _fadeTimer.Stop();
            _fadeTimer.Start();
        }

        private void FadeTimer_Tick(object? sender, EventArgs e)
        {
            if (!_controlsVisible)
                return;

            // No ocultar mientras haya un menú abierto (subtítulos, audio, más opciones o el ecualizador)...
            if (HayMenuAbierto())
                return;

            // ...ni mientras el usuario esté arrastrando la barra de progreso...
            if (DataContext is ReproductorViewModel vm && vm.IsDraggingSlider)
                return;

            // ...ni mientras el mouse esté físicamente sobre los controles. En el mini, solo si está sobre un botón o la barra de progreso:
            // sus franjas oscuras de arriba y abajo ocupan buena parte del recuadro y, con el ratón quieto encima, nunca se ocultaban.
            bool esMini = DataContext is ReproductorViewModel { EsModoMini: true };
            // En grande, sobre la franja de arriba, la barra de abajo o el cajón, pero no sobre el video que queda entre ellas: la capa
            // de controles ocupa toda la vista y es transparente, así que su IsMouseOver es cierto en cualquier punto del reproductor
            // y, con el ratón quieto encima del video, los controles (y el cursor) no se ocultaban nunca.
            if (esMini ? PunteroSobreUnControl() : OverlayControls.IsMouseOver && !ReferenceEquals(Mouse.DirectlyOver, OverlayControls))
                return;

            OcultarControles();
        }

        /// <summary>True si el ratón está sobre un botón o un deslizador de los controles del reproductor.</summary>
        private bool PunteroSobreUnControl() => EsParteDeUnControl(Mouse.DirectlyOver as DependencyObject);

        /// <summary>True si <paramref name="elemento"/> es un botón o un deslizador de los controles del reproductor, o está dentro de uno.</summary>
        private bool EsParteDeUnControl(DependencyObject? elemento)
        {
            for (; elemento != null; elemento = Padre(elemento))
            {
                if (elemento is ButtonBase or Slider) return true;
                if (ReferenceEquals(elemento, OverlayControls)) return false;
            }
            return false;

            static DependencyObject? Padre(DependencyObject hijo) =>
                hijo is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(hijo) : LogicalTreeHelper.GetParent(hijo);
        }

        private void MostrarControles()
        {
            _controlsVisible = true;
            ColocarSubtitulos();
            Mouse.OverrideCursor = null;

            var fadeIn = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            OverlayControls.BeginAnimation(OpacityProperty, fadeIn);
        }

        private void OcultarControles()
        {
            _controlsVisible = false;
            ColocarSubtitulos();
            // Solo se oculta el cursor si está encima del reproductor: fuera de la app, Windows lo dejaba oculto hasta moverlo.
            if (PunteroSobreElReproductor()) Mouse.OverrideCursor = Cursors.None;

            var fadeOut = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(400))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            OverlayControls.BeginAnimation(OpacityProperty, fadeOut);
        }

        private void Slider_DragStarted(object sender, DragStartedEventArgs e)
        {
            if (DataContext is ReproductorViewModel vm)
            {
                vm.IniciarArrastre();
            }
        }

        private void Slider_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (sender is Slider slider && DataContext is ReproductorViewModel vm)
            {
                vm.VistaPreviaArrastre(slider.Value);
                if (vm.TotalSeconds > 0)
                {
                    double halfThumb = ObtenerMitadAnchoThumb();
                    double usable = Math.Max(1d, ProgressBarArea.ActualWidth - 2 * halfThumb);
                    MostrarVistaPreviaTiempo(vm, slider.Value, halfThumb + usable * Math.Clamp(slider.Value / vm.TotalSeconds, 0, 1));
                }
            }
        }

        private void Slider_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (sender is Slider slider && DataContext is ReproductorViewModel vm)
            {
                vm.FinalizarArrastre(slider.Value);
            }
            if (!ProgressBarArea.IsMouseOver) VistaPreviaTiempo.Visibility = Visibility.Collapsed;
        }

        // === Clic EXACTO en la barra de tiempo ===
        // IsMoveToPointEnabled del slider alinea el CENTRO del thumb con el clic y lo recorta
        // dentro de la pista, lo que desplaza hasta ~4% de la duración en los extremos.
        // Aquí calculamos el valor exacto con corrección por el ancho del thumb.
        // Vale para las dos barras, la grande y la del mini (la que recibe el clic llega en sender): sin esto el deslizador solo
        // mueve su bolita, sin saltar en el video, y la posición real la devuelve a su sitio al instante.
        private void ProgressBarArea_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement barra || DataContext is not ReproductorViewModel vm || vm.TotalSeconds <= 0)
                return;

            // No interferir con el arrastre del pulgar (el thumb gestiona su propio drag)
            if (EsDentroDeThumb(e.OriginalSource as DependencyObject))
                return;

            double x = e.GetPosition(barra).X;
            double ancho = Math.Max(1d, barra.ActualWidth);
            double halfThumb = ObtenerMitadAnchoThumb(barra);
            double usable = Math.Max(1d, ancho - 2 * halfThumb);

            double ratio = Math.Clamp((x - halfThumb) / usable, 0d, 1d);
            vm.FinalizarArrastre(vm.TotalSeconds * ratio);

            // Evitar que el slider aplique además su valor impreciso (IsMoveToPointEnabled)
            e.Handled = true;
        }

        // === Vista previa del tiempo al pasar el ratón por la barra ===

        private void ProgressBarArea_MouseMove(object sender, MouseEventArgs e)
        {
            if (DataContext is not ReproductorViewModel vm || vm.TotalSeconds <= 0) return;
            if (vm.IsDraggingSlider) return; // mientras se arrastra, la muestra Slider_DragDelta en la posición de la bolita

            double x = e.GetPosition(ProgressBarArea).X;
            MostrarVistaPreviaTiempo(vm, SegundosEnX(vm, x), x);
        }

        private void ProgressBarArea_MouseLeave(object sender, MouseEventArgs e)
        {
            if (DataContext is ReproductorViewModel { IsDraggingSlider: true }) return;
            VistaPreviaTiempo.Visibility = Visibility.Collapsed;
        }

        /// <summary>Segundo del episodio en la posición <paramref name="x"/> de la barra (misma cuenta que el clic exacto).</summary>
        private double SegundosEnX(ReproductorViewModel vm, double x)
        {
            double ancho = Math.Max(1d, ProgressBarArea.ActualWidth);
            double halfThumb = ObtenerMitadAnchoThumb();
            double usable = Math.Max(1d, ancho - 2 * halfThumb);
            return vm.TotalSeconds * Math.Clamp((x - halfThumb) / usable, 0d, 1d);
        }

        /// <summary>Muestra "12:34" (y "· Opening" si ese punto cae en un tramo marcado) encima de la barra, centrado en <paramref name="x"/>
        /// sin salirse por los bordes.</summary>
        private void MostrarVistaPreviaTiempo(ReproductorViewModel vm, double segundos, double x)
        {
            VistaPreviaTiempoTexto.Text = ReproductorViewModel.FormatearTiempo(segundos, vm.TotalSeconds);

            var tramo = AnimeLocalTracker.Controls.MarcadoresLineaTiempo.TramoEn(vm.SegmentosLineaTiempo, segundos);
            if (tramo != null)
            {
                VistaPreviaTramoTexto.Text = "· " + LocalizationService.T(tramo.Tipo switch
                {
                    Models.TipoSegmentoLineaTiempo.Opening => "Player_TramoOpening",
                    Models.TipoSegmentoLineaTiempo.Ending => "Player_TramoEnding",
                    _ => "Player_TramoResumen"
                });
                VistaPreviaTramoTexto.Foreground = TryFindResource(tramo.Tipo switch
                {
                    Models.TipoSegmentoLineaTiempo.Opening => "Brush.Accent",
                    Models.TipoSegmentoLineaTiempo.Ending => "Brush.Warning",
                    _ => "Brush.TextTertiary"
                }) as Brush ?? Brushes.White;
                VistaPreviaTramoTexto.Visibility = Visibility.Visible;
            }
            else
            {
                VistaPreviaTramoTexto.Visibility = Visibility.Collapsed;
            }

            VistaPreviaTiempo.Visibility = Visibility.Visible;
            VistaPreviaTiempo.UpdateLayout();
            double anchoCaja = VistaPreviaTiempo.ActualWidth;
            double izquierda = Math.Clamp(x - anchoCaja / 2, 0, Math.Max(0, ProgressBarArea.ActualWidth - anchoCaja));
            Canvas.SetLeft(VistaPreviaTiempo, izquierda);
        }

        private static bool EsDentroDeThumb(DependencyObject? source)
        {
            while (source != null)
            {
                if (source is Thumb) return true;
                source = System.Windows.Media.VisualTreeHelper.GetParent(source);
            }
            return false;
        }

        private double ObtenerMitadAnchoThumb(DependencyObject? barra = null)
        {
            var thumb = EncontrarHijo<Thumb>(barra ?? SliderTiempo);
            double ancho = thumb?.ActualWidth ?? 0;
            if (ancho <= 0) ancho = 12; // ancho típico del thumb de MaterialDesign
            return ancho / 2;
        }

        private static T? EncontrarHijo<T>(DependencyObject? parent) where T : DependencyObject
        {
            if (parent == null) return null;
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T encontrado) return encontrado;
                var resultado = EncontrarHijo<T>(child);
                if (resultado != null) return resultado;
            }
            return null;
        }

        // === Menús flotantes (subtítulos, audio, más opciones, ecualizador): cada botón abre o cierra el suyo (ver _menus) ===
        // El toggle por ratón va en PreviewMouseDown y no en Click: el Popup se cerraba al capturar el ratón y el mismo clic lo
        // volvía a abrir. e.Handled = true evita que ese clic llegue además como Click.
        private void BotonMenu_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            AlternarMenuDe(sender);
            e.Handled = true;
        }

        // Activación por teclado (Enter/Espacio con el botón enfocado): con el ratón no se llega aquí.
        private void BotonMenu_Click(object sender, RoutedEventArgs e) => AlternarMenuDe(sender);

        private void AlternarMenuDe(object boton)
        {
            foreach (var (menu, botonDelMenu, _) in _menus)
            {
                if (!ReferenceEquals(botonDelMenu, boton)) continue;
                AlternarMenu(menu);
                break;
            }
            RegistrarActividad();
        }

        /// <summary>Elegir una opción cierra el menú (su Command se ejecuta igualmente; el Modo Noche es un interruptor y también
        /// cierra). Solo hay un menú abierto a la vez, así que basta con cerrarlos todos.</summary>
        private void OpcionDeMenu_Click(object sender, RoutedEventArgs e) => CerrarMenus();

        /// <summary>
        /// El deslizador de volumen se abre con el ratón encima o con el foco de teclado (para quien navega con Tab). Tras usarlo con el
        /// ratón se quedaba con el foco y seguía abierto aunque el ratón se fuera: al salir, si lo último fue el ratón, se suelta el foco
        /// (va a la raíz de los controles, así los atajos de teclado siguen funcionando).
        /// </summary>
        private void VolumePanel_MouseLeave(object sender, MouseEventArgs e)
        {
            if (VolumePanel.IsKeyboardFocusWithin && InputManager.Current.MostRecentInputDevice is MouseDevice)
                OverlayControls.Focus();
        }

        private void ReproductorView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Clic fuera del popup y del botón: cerrar el menú
            foreach (var (menu, boton, _) in _menus)
            {
                if (menu.IsOpen && !menu.IsMouseOver && !boton.IsMouseOver) menu.IsOpen = false;
            }
        }

        private void ReproductorView_PreviewMouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // Solo en el mini, y solo sobre el video: lo devuelve al formato habitual. En cualquier otro caso el doble clic no se da
            // por atendido: Windows cuenta como doble clic el segundo de dos clics seguidos (medio segundo), y al darlo por atendido
            // aquí el botón de debajo no lo recibía (cerrar un menú y volver a pulsar su botón no lo abría; dos «+10 s» seguidos
            // saltaban una sola vez).
            if (DataContext is ReproductorViewModel { EsModoMini: true } vm && !EsParteDeUnControl(e.OriginalSource as DependencyObject))
            {
                vm.RestaurarFormatoHabitualCommand.Execute(null);
                e.Handled = true;
            }
        }

        private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
        {
            while (child != null)
            {
                if (child is T parent)
                    return parent;
                child = System.Windows.Media.VisualTreeHelper.GetParent(child);
            }
            return null;
        }

        private void ReproductorView_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (DataContext is not ReproductorViewModel vm) return;

            var k = e.Key;
            bool ejecutado = true;

            // Ctrl+S siempre captura, además de la tecla configurable "CapturarFrame".
            if (k == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
                vm.CapturarFrameCommand.Execute(null);
            else if (k == Key.L && Keyboard.Modifiers == ModifierKeys.None)
                vm.ToggleCajonEpisodiosCommand.Execute(null);
            else if (k == vm.ObtenerTeclaPara("PlayPausa"))
                vm.TogglePlayPauseCommand.Execute(null);
            else if (k == vm.ObtenerTeclaPara("PantallaCompleta"))
                vm.ToggleFullscreenCommand.Execute(null);
            else if (k == vm.ObtenerTeclaPara("Silenciar"))
                vm.ToggleMuteCommand.Execute(null);
            else if (k == vm.ObtenerTeclaPara("SubirVolumen"))
                vm.Volumen = Math.Min(100, vm.Volumen + 5);
            else if (k == vm.ObtenerTeclaPara("BajarVolumen"))
                vm.Volumen = Math.Max(0, vm.Volumen - 5);
            else if (k == vm.ObtenerTeclaPara("Adelantar10"))
                vm.Forward10Command.Execute(null);
            else if (k == vm.ObtenerTeclaPara("Retroceder10"))
                vm.Rewind10Command.Execute(null);
            else if (k == vm.ObtenerTeclaPara("SaltarIntro"))
            {
                if (vm.MostrarSkipButton || vm.MostrarSkipIntro)
                    vm.SkipIntroOutroCommand.Execute(null);
                else
                    ejecutado = false;
            }
            else if (k == vm.ObtenerTeclaPara("SiguienteEpisodio"))
            {
                if (vm.TieneEpisodioSiguiente)
                    vm.SiguienteEpisodioCommand.Execute(null);
                else
                    ejecutado = false;
            }
            else if (k == vm.ObtenerTeclaPara("AnteriorEpisodio"))
            {
                if (vm.TieneEpisodioAnterior)
                    vm.AnteriorEpisodioCommand.Execute(null);
                else
                    ejecutado = false;
            }
            else if (k == vm.ObtenerTeclaPara("CapturarFrame"))
                vm.CapturarFrameCommand.Execute(null);
            else if (k == vm.ObtenerTeclaPara("MarcarInicioClip"))
                vm.MarcarInicioClipCommand.Execute(null);
            else if (k == vm.ObtenerTeclaPara("MarcarFinClip"))
                vm.MarcarFinClipCommand.Execute(null);
            else if (k == vm.ObtenerTeclaPara("ModoMini"))
                vm.AlternarModoMiniCommand.Execute(null);
            else if (k == vm.ObtenerTeclaPara("Cerrar"))
                vm.TeclaCerrar(); // sale del reproductor sin quitar la pantalla completa
            // Al final: si el usuario puso J o K para retroceder o saltar la intro en Configuración, gana su elección.
            else if (k == vm.ObtenerTeclaPara("RetrasarSubtitulos"))
                vm.RetrasarSubtitulosCommand.Execute(null);
            else if (k == vm.ObtenerTeclaPara("AdelantarSubtitulos"))
                vm.AdelantarSubtitulosCommand.Execute(null);
            else if (k == vm.ObtenerTeclaPara("RetrasarAudio"))
                vm.RetrasarAudioCommand.Execute(null);
            else if (k == vm.ObtenerTeclaPara("AdelantarAudio"))
                vm.AdelantarAudioCommand.Execute(null);
            else
                ejecutado = false;


            if (ejecutado)
            {
                e.Handled = true;
                RegistrarActividad();
            }
        }

        // === Atajos con el foco fuera de la vista ===
        // Las teclas pasan por la vista (ReproductorView_PreviewKeyDown) mientras el foco del teclado está dentro de ella. Cuando el
        // control que lo tenía desaparece (la opción elegida de un menú que se cierra, un botón que se oculta), WPF deja el foco en
        // la propia ventana y las teclas ya no pasaban por aquí: Espacio no pausaba hasta pulsar un botón del reproductor con el
        // ratón. Por eso la vista escucha también a su ventana (la principal o la del mini) mientras está cargada.
        private Window? _ventanaConTeclas;

        internal void EscucharTeclasDe(Window? ventana)
        {
            if (ReferenceEquals(_ventanaConTeclas, ventana)) return;
            if (_ventanaConTeclas != null) _ventanaConTeclas.PreviewKeyDown -= Ventana_PreviewKeyDown;
            _ventanaConTeclas = ventana;
            if (_ventanaConTeclas != null) _ventanaConTeclas.PreviewKeyDown += Ventana_PreviewKeyDown;
        }

        /// <summary>Solo con el foco en la propia ventana: en otro control suyo (el botón de un diálogo) la tecla es de ese control.</summary>
        private void Ventana_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (ReferenceEquals(e.OriginalSource, sender)) ReproductorView_PreviewKeyDown(sender, e);
        }

        /// <summary>
        /// PIP-01: en modo Mini/PiP la barra superior arrastra la VENTANA real de Windows
        /// (Window.DragMove ya se encarga del seguimiento del mouse y del release) — antes
        /// ajustaba un Margin dentro de la ventana porque el recuadro "mini" era un elemento
        /// interno; ahora la ventana misma es el recuadro.
        /// </summary>
        private void BarraSuperior_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is ViewModels.ReproductorViewModel vm && vm.EsModoMini && e.LeftButton == MouseButtonState.Pressed)
            {
                Window.GetWindow(this)?.DragMove();
            }
        }
    }
}