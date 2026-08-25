using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;

namespace AnimeLocalTracker.Views
{
    public partial class ReproductorView : UserControl
    {
        private DispatcherTimer _fadeTimer;
        private bool _controlsVisible = true;
        private Point _lastMousePosition;

        public ReproductorView()
        {
            InitializeComponent();

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
            DesactivarDobleClickFullscreenHost();

            // Suscribir al pipeline global de input DESPUÉS de que FlyleafHost
            // haya creado su ventana nativa (ocurre durante el layout pass de Loaded).
            InputManager.Current.PreProcessInput += InputManager_PreProcessInput;
            _fadeTimer.Start();

            // Restaurar foco con prioridad baja para no competir con FlyleafHost
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (IsLoaded && IsVisible)
                    this.Focus();
            });
        }

        /// <summary>
        /// FlyleafHost captura el mouse a nivel nativo: su doble-click fullscreen no pasa por el
        /// routing de WPF y hay que desactivarlo en el propio host. Se asigna por reflexión porque
        /// como atributo XAML el compilador BAML de FlyleafLib 3.11 lo mapea a la propiedad
        /// equivocada ("False is not a valid value for AvailableWindows").
        /// </summary>
        private void DesactivarDobleClickFullscreenHost()
        {
            try
            {
                var campo = typeof(FlyleafLib.Controls.WPF.FlyleafHost).GetField(
                    "ToggleFullScreenOnDoubleClickProperty",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

                if (campo?.GetValue(null) is System.Windows.DependencyProperty dp)
                {
                    HostFlyleaf.SetValue(dp, false);
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
        }

        private void ReproductorView_Unloaded(object sender, RoutedEventArgs e)
        {
            InputManager.Current.PreProcessInput -= InputManager_PreProcessInput;
            _fadeTimer.Stop();
            Mouse.OverrideCursor = null;

            // StaysOpen=True: cerrar manualmente al salir del reproductor
            if (SubtitlesPopup != null)
            {
                SubtitlesPopup.IsOpen = false;
            }
        }

        private void InputManager_PreProcessInput(object sender, PreProcessInputEventArgs e)
        {
            // Micro-optimización: solo procesar si la ventana está activa
            var mainWindow = Application.Current?.MainWindow;
            if (mainWindow == null || !mainWindow.IsActive)
                return;

            // Filtro barato: descartamos cualquier evento que no sea de mouse
            if (e.StagingItem.Input is not MouseEventArgs mouseArgs)
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

            // No ocultar mientras el menú de subtítulos esté abierto...
            if (SubtitlesPopup != null && SubtitlesPopup.IsOpen)
                return;

            // ...ni mientras el usuario esté arrastrando la barra de progreso...
            if (DataContext is ReproductorViewModel vm && vm.IsDraggingSlider)
                return;

            // ...ni mientras el mouse esté físicamente sobre los controles.
            if (OverlayControls != null && OverlayControls.IsMouseOver)
                return;

            OcultarControles();
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
            Mouse.OverrideCursor = Cursors.None;

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
            }
        }

        private void Slider_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (sender is Slider slider && DataContext is ReproductorViewModel vm)
            {
                vm.FinalizarArrastre(slider.Value);
            }
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
            SubtitlesPopup.IsOpen = !SubtitlesPopup.IsOpen;
            e.Handled = true;
            RegistrarActividad();
        }

        private void SubtitleMenuItem_Click(object sender, RoutedEventArgs e)
        {
            // Elegir una opción cierra el menú por completo (el Command se ejecuta igualmente)
            SubtitlesPopup.IsOpen = false;
        }

        private void ReproductorView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Clic fuera del popup y del botón: cerrar el menú
            if (SubtitlesPopup.IsOpen && !SubtitlesPopup.IsMouseOver && !SubtitlesButton.IsMouseOver)
            {
                SubtitlesPopup.IsOpen = false;
            }
        }

        private void ReproductorView_PreviewMouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
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

            switch (e.Key)
            {
                case Key.Space:
                    vm.TogglePlayPauseCommand.Execute(null);
                    e.Handled = true;
                    RegistrarActividad();
                    break;
                case Key.F11:
                    vm.ToggleFullscreenCommand.Execute(null);
                    e.Handled = true;
                    RegistrarActividad();
                    break;
                case Key.M:
                    vm.ToggleMuteCommand.Execute(null);
                    e.Handled = true;
                    RegistrarActividad();
                    break;
                case Key.Up:
                    vm.Volumen = Math.Min(100, vm.Volumen + 5);
                    e.Handled = true;
                    RegistrarActividad();
                    break;
                case Key.Down:
                    vm.Volumen = Math.Max(0, vm.Volumen - 5);
                    e.Handled = true;
                    RegistrarActividad();
                    break;
                case Key.Right:
                    vm.SeekCommand.Execute(vm.CurrentSeconds + 10);
                    e.Handled = true;
                    RegistrarActividad();
                    break;
                case Key.Left:
                    vm.SeekCommand.Execute(vm.CurrentSeconds - 10);
                    e.Handled = true;
                    RegistrarActividad();
                    break;
                case Key.S:
                    if (vm.MostrarSkipButton || vm.MostrarSkipIntro)
                    {
                        vm.SkipIntroOutroCommand.Execute(null);
                        e.Handled = true;
                        RegistrarActividad();
                    }
                    break;
                case Key.N:
                    if (vm.TieneEpisodioSiguiente)
                    {
                        vm.SiguienteEpisodioCommand.Execute(null);
                        e.Handled = true;
                        RegistrarActividad();
                    }
                    break;
                case Key.P:
                    if (vm.TieneEpisodioAnterior)
                    {
                        vm.AnteriorEpisodioCommand.Execute(null);
                        e.Handled = true;
                        RegistrarActividad();
                    }
                    break;
                case Key.Escape:
                    vm.CerrarCommand.Execute(null);
                    e.Handled = true;
                    break;
            }
        }
    }
}