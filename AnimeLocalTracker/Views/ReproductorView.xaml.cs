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
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using MaterialDesignThemes.Wpf;

namespace AnimeLocalTracker.Views
{
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

            // Suscribimos PreProcessInput en Loaded (no en el constructor)
            // para que FlyleafHost ya haya terminado de inicializar su ventana nativa.
            this.Loaded += ReproductorView_Loaded;
            this.Unloaded += ReproductorView_Unloaded;
            this.IsVisibleChanged += ReproductorView_IsVisibleChanged;
            this.DataContextChanged += ReproductorView_DataContextChanged;
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

        // === Texto de los subtítulos: mismo patrón que el toast/cuenta atrás (ver comentario en el XAML).
        // Escucha Player.Subtitles.SubsText; si el Player se recrea, se vuelve a enganchar al nuevo. ===
        private ReproductorViewModel? _vmSubtitulos;
        private INotifyPropertyChanged? _subtitulosPlayer;

        private void EnlazarSubtitulos(ReproductorViewModel? vm)
        {
            if (ReferenceEquals(_vmSubtitulos, vm))
            {
                ActualizarCapaAss(); // la vista aparece con el modo ASS ya activo (mini reproductor): se lee el estado actual
                return;
            }

            if (_vmSubtitulos != null) _vmSubtitulos.PropertyChanged -= VmSubtitulos_PropertyChanged;
            _vmSubtitulos = vm;
            if (_vmSubtitulos != null) _vmSubtitulos.PropertyChanged += VmSubtitulos_PropertyChanged;

            EnlazarSubtitulosDelPlayer(vm?.Player?.Subtitles);
            ActualizarTapaVideo();
            ActualizarCapaAss();
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
            if (e.PropertyName is nameof(ReproductorViewModel.SubtitulosAssActivo) or nameof(ReproductorViewModel.SubtitulosHabilitados))
            {
                Core.HiloUi.Ejecutar(ActualizarCapaAss);
                return;
            }

            if (e.PropertyName is nameof(ReproductorViewModel.EstiloSubtitulos)
                or nameof(ReproductorViewModel.SubtitulosDobleLineaActivo)
                or nameof(ReproductorViewModel.SubtituloLineaAbajo)
                or nameof(ReproductorViewModel.SubtituloLineaArriba))
            {
                Core.HiloUi.Ejecutar(ActualizarTextoSubtitulos);
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

            if (e.PropertyName != nameof(ReproductorViewModel.Player)) return;

            if (Dispatcher.CheckAccess()) EnlazarSubtitulosDelPlayer(_vmSubtitulos?.Player?.Subtitles);
            else Dispatcher.InvokeAsync(() => EnlazarSubtitulosDelPlayer(_vmSubtitulos?.Player?.Subtitles));
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

        private void EnlazarSubtitulosDelPlayer(INotifyPropertyChanged? subtitulos)
        {
            if (ReferenceEquals(_subtitulosPlayer, subtitulos)) return;

            if (_subtitulosPlayer != null) _subtitulosPlayer.PropertyChanged -= Subtitulos_PropertyChanged;
            _subtitulosPlayer = subtitulos;
            if (_subtitulosPlayer != null) _subtitulosPlayer.PropertyChanged += Subtitulos_PropertyChanged;

            ActualizarTextoSubtitulos();
        }

        private void Subtitulos_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(FlyleafLib.MediaPlayer.Subtitles.SubsText)) return;

            Core.HiloUi.Ejecutar(ActualizarTextoSubtitulos);
        }

        private void ActualizarTextoSubtitulos()
        {
            if (_assCapaActiva)
            {
                // La capa ASS ya dibuja estas líneas con su estilo: el texto plano se oculta para no verlas dos veces.
                SubtitulosVista.Texto = string.Empty;
                SubtitulosVistaArriba.Texto = string.Empty;
                return;
            }

            // Con la pista completa ya extraída (ver ReproductorViewModel.CargarCuesSubtitulosSiCorresponde) se
            // confía en las dos líneas resueltas por la app, que sí distinguen cuándo hay dos hablando a la vez.
            // Si todavía no terminó de extraerse (o falló), se sigue mostrando el texto único de Flyleaf abajo,
            // igual que siempre: los subtítulos nunca desaparecen por culpa de esta mejora.
            if (_vmSubtitulos?.SubtitulosDobleLineaActivo == true)
            {
                SubtitulosVista.Texto = _vmSubtitulos.SubtituloLineaAbajo;
                SubtitulosVistaArriba.Texto = _vmSubtitulos.SubtituloLineaArriba;
            }
            else
            {
                SubtitulosVista.Texto = (_subtitulosPlayer as FlyleafLib.MediaPlayer.Subtitles)?.SubsText ?? string.Empty;
                SubtitulosVistaArriba.Texto = string.Empty;
            }

            SubtitulosVista.Estilo = _vmSubtitulos?.EstiloSubtitulos;
            SubtitulosVistaArriba.Estilo = _vmSubtitulos?.EstiloSubtitulos;
        }

        // === Capa de subtítulos ASS (ver SubtitulosAssImagen en el XAML y docs/investigacion-subtitulos-ass.md) ===
        // El ViewModel es el dueño del dibujante; esta vista solo le pide el fotograma del instante actual y lo pinta.
        // Cada instancia de la vista (ventana principal y mini reproductor) tiene su propio bitmap y solo dibuja mientras se ve.
        private WriteableBitmap? _assBitmap;
        private byte[]? _assBufer;
        private bool _assCapaActiva;
        private bool _assDibujando;
        private long _assUltimoTick = -1;

        private void ActualizarCapaAss()
        {
            var vm = _vmSubtitulos;
            bool activa = vm is { SubtitulosAssActivo: true, SubtitulosHabilitados: true } && IsLoaded && IsVisible
                          && vm.DibujanteAss.Ancho > 0 && vm.DibujanteAss.Alto > 0;

            if (_assCapaActiva)
            {
                CompositionTarget.Rendering -= CapaAss_Rendering;
                SubtitulosAssImagen.Visibility = Visibility.Collapsed;
                SubtitulosAssImagen.Source = null;
                _assBitmap = null;
                _assBufer = null;
            }

            _assCapaActiva = activa;
            if (activa)
            {
                var dibujante = vm!.DibujanteAss;
                _assBitmap = new WriteableBitmap(dibujante.Ancho, dibujante.Alto, 96, 96, PixelFormats.Pbgra32, null);
                _assBufer = new byte[dibujante.Ancho * dibujante.Alto * 4];
                _assUltimoTick = -1;
                SubtitulosAssImagen.Source = _assBitmap;
                SubtitulosAssImagen.Visibility = Visibility.Visible;
                CompositionTarget.Rendering += CapaAss_Rendering;
            }

            ActualizarTextoSubtitulos();
        }

        /// <summary>
        /// En cada fotograma de la interfaz: si el video avanzó (o saltó), se pide el dibujo de ese instante a un hilo de fondo y
        /// al volver se copia al bitmap la franja que cambió. Mientras hay un dibujo en curso no se pide otro: el siguiente toma
        /// el instante más reciente, así un cartel pesado salta fotogramas del subtítulo en vez de acumular retraso. En pausa el
        /// tiempo no cambia y no se dibuja nada.
        /// </summary>
        private async void CapaAss_Rendering(object? sender, EventArgs e)
        {
            var vm = _vmSubtitulos;
            var player = vm?.Player;
            var bitmap = _assBitmap;
            var bufer = _assBufer;
            if (_assDibujando || vm == null || player == null || bitmap == null || bufer == null) return;

            long tick = player.CurTime;
            if (tick == _assUltimoTick) return;
            _assUltimoTick = tick;

            _assDibujando = true;
            try
            {
                var dibujante = vm.DibujanteAss;
                var instante = TimeSpan.FromTicks(tick);
                var (ok, filaInicial, filas) = await Task.Run(() =>
                {
                    bool dibujado = dibujante.Renderizar(instante, bufer, out int desde, out int cuantas);
                    return (dibujado, desde, cuantas);
                });

                if (!ReferenceEquals(bitmap, _assBitmap)) return; // la capa se apagó o cambió de episodio mientras se dibujaba
                if (!ok)
                {
                    vm.NotificarFalloDibujoAss();
                    return;
                }
                if (filas == 0) return; // nada cambió en pantalla: no se toca el bitmap

                // Solo se copia la franja de filas que cambió (un diálogo son unas decenas de filas, no el fotograma entero).
                int paso = bitmap.PixelWidth * 4;
                bitmap.WritePixels(new Int32Rect(0, filaInicial, bitmap.PixelWidth, filas), bufer, paso, filaInicial * paso);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("ReproductorView", $"No se pudo pintar la capa de subtítulos ASS: {ex.Message}");
            }
            finally
            {
                _assDibujando = false;
            }
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
        // (ver comentario en el XAML sobre por qué no se usa Binding en la raíz del FlyleafHost) ===
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

            ActualizarCapaAss(); // oculta: se deja de dibujar; visible otra vez: se retoma
        }

        private void ReproductorView_Loaded(object sender, RoutedEventArgs e)
        {
            EnlazarToast((DataContext as ReproductorViewModel)?.DialogService);
            EnlazarSubtitulos(DataContext as ReproductorViewModel);
            DesactivarInteraccionNativaDelHost();

            // Suscribir al pipeline global de input DESPUÉS de que FlyleafHost
            // haya creado su ventana nativa (ocurre durante el layout pass de Loaded).
            InputManager.Current.PreProcessInput += InputManager_PreProcessInput;
            EngancharTecladoEnVentanasDeFlyleaf();
            HostFlyleaf.OverlayCreated += HostFlyleaf_VentanaCreada;
            HostFlyleaf.SurfaceCreated += HostFlyleaf_VentanaCreada;
            _fadeTimer.Start();

            // Restaurar foco con prioridad baja para no competir con FlyleafHost
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (IsLoaded && IsVisible)
                    this.Focus();
            });
        }

        /// <summary>
        /// Desactiva la interacción nativa del mouse del FlyleafHost:
        /// - El doble-click fullscreen no pasa por el routing de WPF y hay que
        ///   desactivarlo en el propio host (por reflexión: como atributo XAML el
        ///   compilador BAML lo mapea a la propiedad equivocada).
        /// - Los bindings nativos del ratón (MouseBindings en Surface) capturan el
        ///   mouse tras un click en el video y los controles WPF dejan de recibir
        ///   eventos hasta usar el teclado. Con None, el host no consume clicks.
        /// </summary>
        private void DesactivarInteraccionNativaDelHost()
        {
            try
            {
                var campo = typeof(FlyleafLib.Controls.WPF.FlyleafHost).GetField(
                    "ToggleFullScreenOnDoubleClickProperty",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

                if (campo?.GetValue(null) is System.Windows.DependencyProperty dp)
                {
                    if (dp.PropertyType == typeof(bool))
                    {
                        HostFlyleaf.SetValue(dp, false);
                    }
                    else if (dp.PropertyType.IsEnum)
                    {
                        // En FlyleafLib 3.x el tipo es AvailableWindows (None = 0)
                        object noneVal = Enum.ToObject(dp.PropertyType, 0);
                        HostFlyleaf.SetValue(dp, noneVal);
                    }
                }
                else
                {
                    AppLogger.Debug("ReproductorView", "FlyleafHost no expone ToggleFullScreenOnDoubleClickProperty; se mantiene solo el bloqueo a nivel WPF.");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug("ReproductorView", $"No se pudo desactivar el doble-click del host: {ex.Message}");
            }

            try
            {
                // Bug reproductor: click en medio del video dejaba los controles sin
                // responder (captura nativa del mouse del host) hasta usar el teclado.
                var mbField = typeof(FlyleafLib.Controls.WPF.FlyleafHost).GetField(
                    "MouseBindingsProperty",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

                if (mbField?.GetValue(null) is System.Windows.DependencyProperty mbDp)
                {
                    HostFlyleaf.SetValue(mbDp, FlyleafLib.Controls.WPF.AvailableWindows.None);
                    AppLogger.Debug("ReproductorView", "MouseBindings del host desactivados (None): los clicks ya no capturan el ratón.");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug("ReproductorView", $"No se pudieron desactivar los MouseBindings del host: {ex.Message}");
            }
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
            HostFlyleaf.OverlayCreated -= HostFlyleaf_VentanaCreada;
            HostFlyleaf.SurfaceCreated -= HostFlyleaf_VentanaCreada;
            DesengancharTecladoDeVentanasDeFlyleaf();
            _fadeTimer.Stop();
            Mouse.OverrideCursor = null;

            // StaysOpen=True: cerrar manualmente al salir del reproductor
            CerrarMenus();

            // Esta vista no vuelve a usarse (se cerró el episodio, o pasó al mini reproductor, que tiene su propia vista en otra ventana):
            // se suelta el reproductor y se cierran sus ventanas nativas de video y controles. Si no, quedaban ocultas para siempre, dos
            // más cada vez que se pasaba al mini y se volvía. El Player no se destruye: la otra vista lo recoge y el video sigue.
            try
            {
                HostFlyleaf.Dispose();
            }
            catch (Exception ex)
            {
                AppLogger.Debug("ReproductorView", $"No se pudo liberar el FlyleafHost de la vista descartada: {ex.Message}");
            }
        }

        // === Teclado en las ventanas de Flyleaf ===
        // Los controles viven en una ventana propia de Flyleaf (Overlay) encima del video. Al hacer clic en un botón, el foco pasa a esa
        // ventana y las teclas ya no llegaban a este control: las flechas, espacio, etc. dejaban de funcionar hasta sacar y volver a poner
        // el foco en la app. Ahora esas ventanas pasan sus teclas por el mismo manejador.
        private readonly System.Collections.Generic.List<Window> _ventanasConTeclado = new();

        private void HostFlyleaf_VentanaCreada(object? sender, EventArgs e) => EngancharTecladoEnVentanasDeFlyleaf();

        private void EngancharTecladoEnVentanasDeFlyleaf()
        {
            DesactivarTeclasPropiasDeFlyleaf();
            foreach (var ventana in new[] { HostFlyleaf.Overlay, HostFlyleaf.Surface })
            {
                if (ventana == null || _ventanasConTeclado.Contains(ventana)) continue;
                ventana.PreviewKeyDown += ReproductorView_PreviewKeyDown;
                _ventanasConTeclado.Add(ventana);
                FuenteNativa(ventana)?.AddHook(BordeMiniHook);
            }
        }

        private void DesengancharTecladoDeVentanasDeFlyleaf()
        {
            foreach (var ventana in _ventanasConTeclado)
            {
                ventana.PreviewKeyDown -= ReproductorView_PreviewKeyDown;
                FuenteNativa(ventana)?.RemoveHook(BordeMiniHook);
            }
            _ventanasConTeclado.Clear();
        }

        /// <summary>La ventana de video de Flyleaf no tiene contenido WPF y PresentationSource.FromVisual devuelve null: se busca por su HWND.</summary>
        private static System.Windows.Interop.HwndSource? FuenteNativa(Window ventana)
        {
            var manejador = new System.Windows.Interop.WindowInteropHelper(ventana).Handle;
            return manejador == IntPtr.Zero ? null : System.Windows.Interop.HwndSource.FromHwnd(manejador);
        }

        /// <summary>
        /// En el mini reproductor, el video y los controles de Flyleaf (ventanas nativas) cubren la ventana entera, así que sus bordes
        /// no se podían agarrar para cambiar el tamaño: por eso llevaba un marco negro alrededor. Ahora, cerca de los bordes, estas
        /// ventanas se declaran "transparentes" al ratón y el punto le llega a la ventana del mini, que hace el cambio de tamaño.
        /// </summary>
        private IntPtr BordeMiniHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_NCHITTEST = 0x0084;
            const int HTTRANSPARENT = -1;
            if (msg != WM_NCHITTEST || Window.GetWindow(this) is not MiniReproductorWindow mini) return IntPtr.Zero;

            long punto = lParam.ToInt64();
            int x = (short)(punto & 0xFFFF), y = (short)((punto >> 16) & 0xFFFF);
            if (!mini.EnBordeDeRedimension(x, y)) return IntPtr.Zero;

            handled = true;
            return new IntPtr(HTTRANSPARENT);
        }

        /// <summary>Flyleaf trae sus propios atajos; con los de la app enganchados, dos manejadores para la misma tecla harían la acción
        /// dos veces (p. ej. pausar y reanudar con un solo espacio).</summary>
        private void DesactivarTeclasPropiasDeFlyleaf()
        {
            try { HostFlyleaf.KeyBindings = FlyleafLib.Controls.WPF.AvailableWindows.None; }
            catch (Exception ex) { AppLogger.Debug("ReproductorView", $"No se pudieron desactivar los atajos propios de Flyleaf: {ex.Message}"); }
        }

        /// <summary>La app está activa si lo está cualquiera de sus ventanas: la principal o las de Flyleaf (tras un clic en un control,
        /// la activa es la de los controles y antes se ignoraba el movimiento del ratón).</summary>
        private bool AppActiva() =>
            (Application.Current?.MainWindow?.IsActive ?? false)
            || (HostFlyleaf.Overlay?.IsActive ?? false)
            || (HostFlyleaf.Surface?.IsActive ?? false);

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
        /// True si el puntero está sobre el propio reproductor: su ventana (la principal, o la del mini reproductor) o las ventanas de
        /// video y controles de Flyleaf. Fuera —barra de tareas, otro programa o, con el mini abierto, el resto de la app— ni se
        /// despiertan sus controles ni se oculta el cursor: antes, navegando por la app con el mini abierto, el cursor desaparecía.
        /// </summary>
        private bool PunteroSobreElReproductor()
        {
            try
            {
                if (!GetCursorPos(out var punto)) return false;
                IntPtr raiz = GetAncestor(WindowFromPoint(punto), 2); // GA_ROOT
                if (raiz == IntPtr.Zero) return false;

                return raiz == Manejador(Window.GetWindow(this))
                    || raiz == Manejador(HostFlyleaf.Overlay)
                    || raiz == Manejador(HostFlyleaf.Surface);
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

            // Solo si la app está activa (la ventana principal o las de Flyleaf). El mini reproductor no se activa al pasar el ratón
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
            if (esMini ? PunteroSobreUnControl() : OverlayControls != null && OverlayControls.IsMouseOver)
                return;

            OcultarControles();
        }

        /// <summary>True si el ratón está sobre un botón o un deslizador de los controles del reproductor.</summary>
        private bool PunteroSobreUnControl()
        {
            for (var elemento = Mouse.DirectlyOver as DependencyObject; elemento != null; elemento = Padre(elemento))
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
        private void ProgressBarArea_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is not ReproductorViewModel vm || vm.TotalSeconds <= 0)
                return;

            // No interferir con el arrastre del pulgar (el thumb gestiona su propio drag)
            if (EsDentroDeThumb(e.OriginalSource as DependencyObject))
                return;

            double x = e.GetPosition(ProgressBarArea).X;
            double ancho = Math.Max(1d, ProgressBarArea.ActualWidth);
            double halfThumb = ObtenerMitadAnchoThumb();
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

        private double ObtenerMitadAnchoThumb()
        {
            var thumb = EncontrarHijo<Thumb>(SliderTiempo);
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

        // === Menú de subtítulos (toggle real) ===
        // El toggle se maneja en PreviewMouseDown (no en Click): con StaysOpen="False" el popup
        // se cerraba por captura del mouse y el mismo clic lo volvía a abrir.
        private void SubtitlesButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            AlternarMenu(SubtitlesPopup);
            e.Handled = true;
            RegistrarActividad();
        }

        // Activación por teclado (Enter/Espacio con el botón enfocado): el toggle por mouse va por
        // PreviewMouseLeftButtonDown de arriba, que marca e.Handled=true y evita que este Click
        // también dispare por clic (solo llega aquí vía teclado).
        private void SubtitlesButton_Click(object sender, RoutedEventArgs e)
        {
            AlternarMenu(SubtitlesPopup);
            RegistrarActividad();
        }

        private void SubtitleMenuItem_Click(object sender, RoutedEventArgs e)
        {
            // Elegir una opción cierra el menú por completo (el Command se ejecuta igualmente)
            SubtitlesPopup.IsOpen = false;
        }

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

        // === Menú de pistas de audio — mismo patrón de toggle que Subtítulos ===
        private void AudioButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            AlternarMenu(AudioPopup);
            e.Handled = true;
            RegistrarActividad();
        }

        private void AudioButton_Click(object sender, RoutedEventArgs e)
        {
            AlternarMenu(AudioPopup);
            RegistrarActividad();
        }

        private void AudioMenuItem_Click(object sender, RoutedEventArgs e) => AudioPopup.IsOpen = false;

        // === Menú "Más opciones" (Captura + Modo Noche) — mismo patrón de toggle que Subtítulos ===
        private void MoreOptionsButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            AlternarMenu(MoreOptionsPopup);
            e.Handled = true;
            RegistrarActividad();
        }

        private void MoreOptionsButton_Click(object sender, RoutedEventArgs e)
        {
            AlternarMenu(MoreOptionsPopup);
            RegistrarActividad();
        }

        private void MoreOptionsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            // Modo Noche se queda encendido/apagado (es un toggle); cerramos el menú igual en ambos casos.
            MoreOptionsPopup.IsOpen = false;
        }

        // === Ecualizador — mismo patrón de toggle que Subtítulos ===
        private void EqualizerButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            AlternarMenu(EqualizerPopup);
            e.Handled = true;
            RegistrarActividad();
        }

        private void EqualizerButton_Click(object sender, RoutedEventArgs e)
        {
            AlternarMenu(EqualizerPopup);
            RegistrarActividad();
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
            if (DataContext is ReproductorViewModel vm && vm.EsModoMini)
            {
                vm.RestaurarFormatoHabitualCommand.Execute(null);
                e.Handled = true;
                return;
            }

            // Segunda línea de defensa: el fix principal es ToggleFullScreenOnDoubleClick="False"
            // en FlyleafHost (su captura del mouse es nativa y no pasa por el routing de WPF).
            e.Handled = true;
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
            else if (k == vm.ObtenerTeclaPara("ModoMini"))
                vm.AlternarModoMiniCommand.Execute(null);
            else if (k == vm.ObtenerTeclaPara("Cerrar"))
                vm.TeclaCerrar(); // sale del reproductor sin quitar la pantalla completa
            else
                ejecutado = false;


            if (ejecutado)
            {
                e.Handled = true;
                RegistrarActividad();
            }
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