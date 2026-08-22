using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
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

        private void ReproductorView_Unloaded(object sender, RoutedEventArgs e)
        {
            InputManager.Current.PreProcessInput -= InputManager_PreProcessInput;
            _fadeTimer.Stop();
            Mouse.OverrideCursor = null;
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
            catch (Exception)
            {
                // Flyleaf usa ventanas nativas que pueden generar excepciones
                // transitorias durante inicialización/destrucción. Las ignoramos
                // silenciosamente para evitar crashes.
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
                vm.IsDraggingSlider = true;
            }
        }

        private void Slider_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (sender is Slider slider && DataContext is ReproductorViewModel vm)
            {
                vm.IsDraggingSlider = false;
                vm.SeekCommand.Execute(slider.Value);
            }
        }

        private void Slider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            // Solo buscar si NO estábamos arrastrando el pulgar (clic directo en la pista)
            if (DataContext is ReproductorViewModel vm && !vm.IsDraggingSlider)
            {
                if (sender is Slider slider)
                {
                    vm.SeekCommand.Execute(slider.Value);
                }
            }
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