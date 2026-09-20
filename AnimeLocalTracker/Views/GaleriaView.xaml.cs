using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AnimeLocalTracker.ViewModels;

namespace AnimeLocalTracker.Views;

public partial class GaleriaView : UserControl
{
    // Deben coincidir con la tarjeta de la ruleta en GaleriaView.xaml (Width=100, Margin derecho=10).
    private const double RuletaAnchoTarjeta = 100;
    private const double RuletaSeparacion = 10;
    private const double RuletaSegundosGiro = 4.2;

    private ScrollViewer? _scrollViewer;
    private GaleriaViewModel? _vmQueVer;
    // Identifica el giro vigente: si el panel se cierra o se pide "Otro" a mitad, los Completed
    // de una animación anterior no deben terminar el giro nuevo.
    private int _giroId;

    public GaleriaView()
    {
        InitializeComponent();
        Loaded += GaleriaView_Loaded;
        Unloaded += GaleriaView_Unloaded;
    }

    private void GaleriaView_Loaded(object sender, RoutedEventArgs e)
    {
        _scrollViewer = FindVisualChild<ScrollViewer>(ListaGaleria);
        if (_scrollViewer != null)
        {
            _scrollViewer.ScrollChanged += ScrollViewer_ScrollChanged;
            if (DataContext is GaleriaViewModel vm && vm.UltimoScrollOffset > 0)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    _scrollViewer?.ScrollToVerticalOffset(vm.UltimoScrollOffset);
                }));
            }
        }

        SuscribirQueVerHoy();
    }

    private void GaleriaView_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_scrollViewer != null)
        {
            _scrollViewer.ScrollChanged -= ScrollViewer_ScrollChanged;
        }

        // El ViewModel es singleton y sobrevive a la vista: al salir de la galería (p. ej. al ir al
        // reproductor) el panel "Qué veo hoy" no debe quedar abierto ni a mitad de giro.
        var vm = _vmQueVer;
        DesuscribirQueVerHoy();
        vm?.CerrarQueVerHoyCommand.Execute(null);
    }

    private void ScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (DataContext is GaleriaViewModel vm && _scrollViewer != null)
        {
            vm.UltimoScrollOffset = _scrollViewer.VerticalOffset;
        }
    }

    // ── "Qué veo hoy": animación de la ruleta ──

    private void SuscribirQueVerHoy()
    {
        DesuscribirQueVerHoy();
        if (DataContext is not GaleriaViewModel vm) return;

        _vmQueVer = vm;
        vm.PropertyChanged += Vm_PropertyChanged;
    }

    private void DesuscribirQueVerHoy()
    {
        if (_vmQueVer != null)
        {
            _vmQueVer.PropertyChanged -= Vm_PropertyChanged;
            _vmQueVer = null;
        }
        DetenerGiro();
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(GaleriaViewModel.FaseActualQueVer) || _vmQueVer == null) return;

        if (_vmQueVer.FaseActualQueVer == FaseQueVer.Buscando)
        {
            // Al abrir el panel el foco se lleva a él: si quedara en la galería (p. ej. en una tarjeta),
            // las teclas de flecha / AvPág seguirían desplazándola por detrás.
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => PanelQueVeoHoy.Focus()));
        }

        if (_vmQueVer.FaseActualQueVer == FaseQueVer.Girando)
        {
            IniciarGiro(_vmQueVer);
        }
        else if (_vmQueVer.FaseActualQueVer != FaseQueVer.Resultado)
        {
            DetenerGiro();
        }
    }

    /// <summary>Con el panel abierto la rueda del ratón no debe llegar a nada de lo que hay debajo.</summary>
    private void Overlay_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e) => e.Handled = true;

    private TranslateTransform TransformRuleta => (TranslateTransform)RuletaItems.RenderTransform;

    private void DetenerGiro()
    {
        _giroId++;
        var transform = TransformRuleta;
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        transform.X = 0;
    }

    private void IniciarGiro(GaleriaViewModel vm)
    {
        int id = ++_giroId;

        // Se coloca la tira al inicio en el mismo instante en que cambia la fase, para que no se vea
        // ni un fotograma de la tira nueva en la posición donde se detuvo la anterior ("Otro").
        var transform = TransformRuleta;
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        transform.X = 0;

        // Con las animaciones del sistema desactivadas (Windows: "mostrar animaciones") no hay giro:
        // se pasa directo al resultado.
        if (!SystemParameters.ClientAreaAnimation)
        {
            vm.TerminarGiroQueVerCommand.Execute(null);
            return;
        }

        // La tira aparece con esta misma fase: se espera al diseño para conocer el ancho real.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (id != _giroId || vm.FaseActualQueVer != FaseQueVer.Girando) return;

            double viewport = RuletaCanvas.ActualWidth;
            if (viewport <= 0)
            {
                vm.TerminarGiroQueVerCommand.Execute(null);
                return;
            }

            double paso = RuletaAnchoTarjeta + RuletaSeparacion;
            double centroGanador = vm.RuletaIndiceGanadorQueVer * paso + RuletaAnchoTarjeta / 2;
            double centrado = -(centroGanador - viewport / 2);
            // Como en una ruleta real, se pasa o se queda corta unos píxeles y luego se acomoda.
            double pasada = centrado + (Random.Shared.NextDouble() - 0.5) * RuletaAnchoTarjeta * 0.7;

            var giro = new DoubleAnimation(0, pasada, TimeSpan.FromSeconds(RuletaSegundosGiro))
            {
                EasingFunction = new PowerEase { Power = 4, EasingMode = EasingMode.EaseOut }
            };
            giro.Completed += (_, _) =>
            {
                if (id != _giroId) return;

                var ajuste = new DoubleAnimation(pasada, centrado, TimeSpan.FromMilliseconds(420))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
                };
                ajuste.Completed += (_, _) =>
                {
                    if (id != _giroId) return;

                    // La posición final se fija a mano para que se conserve al liberar la animación.
                    transform.BeginAnimation(TranslateTransform.XProperty, null);
                    transform.X = centrado;
                    vm.TerminarGiroQueVerCommand.Execute(null);
                };
                transform.BeginAnimation(TranslateTransform.XProperty, ajuste);
            };
            transform.BeginAnimation(TranslateTransform.XProperty, giro);
        }));
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent == null) return null;
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild)
                return typedChild;

            var descendant = FindVisualChild<T>(child);
            if (descendant != null)
                return descendant;
        }
        return null;
    }
}
