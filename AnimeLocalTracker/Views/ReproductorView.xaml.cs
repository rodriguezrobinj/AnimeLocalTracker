using System;
using System.Windows;
using System.Windows.Controls;
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

        public ReproductorView()
        {
            InitializeComponent();

            _fadeTimer = new DispatcherTimer();
            _fadeTimer.Interval = TimeSpan.FromSeconds(3);
            _fadeTimer.Tick += FadeTimer_Tick;
            _fadeTimer.Start();

            InputManager.Current.PreProcessInput += InputManager_PreProcessInput;
            this.Unloaded += ReproductorView_Unloaded;
            this.DataContextChanged += ReproductorView_DataContextChanged;
        }

        private void ReproductorView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (OverlayControls != null)
            {
                OverlayControls.DataContext = this.DataContext;
            }
        }

        private void ReproductorView_Unloaded(object sender, RoutedEventArgs e)
        {
            InputManager.Current.PreProcessInput -= InputManager_PreProcessInput;
            _fadeTimer.Stop();
        }

        private void InputManager_PreProcessInput(object sender, PreProcessInputEventArgs e)
        {
            if (e.StagingItem.Input is MouseEventArgs)
            {
                if (!_controlsVisible)
                {
                    MostrarControles();
                }

                _fadeTimer.Stop();
                _fadeTimer.Start();
            }
        }

        private void FadeTimer_Tick(object? sender, EventArgs e)
        {
            if (_controlsVisible)
            {
                OcultarControles();
            }
        }

        private void MostrarControles()
        {
            _controlsVisible = true;
            this.Cursor = Cursors.Arrow;
            
            var fadeIn = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            OverlayControls.BeginAnimation(OpacityProperty, fadeIn);
        }

        private void OcultarControles()
        {
            _controlsVisible = false;
            this.Cursor = Cursors.None;
            
            var fadeOut = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(400))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            OverlayControls.BeginAnimation(OpacityProperty, fadeOut);
        }

        private void Slider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is ReproductorViewModel vm)
            {
                vm.IsDraggingSlider = true;
            }
        }

        private void Slider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is Slider slider && DataContext is ReproductorViewModel vm)
            {
                vm.IsDraggingSlider = false;
                vm.SeekCommand.Execute(slider.Value);
            }
        }
    }
}
