using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AnimeLocalTracker.ViewModels;

namespace AnimeLocalTracker.Views;

/// <summary>Panel "Qué veo hoy" de la Galería: aquí vive la animación de la ruleta de portadas.</summary>
public partial class QueVeoHoyPanel : UserControl
{
    // Deben coincidir con la tarjeta de la ruleta en QueVeoHoyPanel.xaml (Width=100, Margin derecho=10).
    private const double RuletaAnchoTarjeta = 100;
    private const double RuletaSeparacion = 10;
    private const double RuletaSegundosGiro = 4.2;

    private QueVeoHoyViewModel? _viewModel;
    // Identifica el giro vigente: si el panel se cierra o se pide "Otro" a mitad, los Completed
    // de una animación anterior no deben terminar el giro nuevo.
    private int _giroId;

    public QueVeoHoyPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Suscribir();
        Unloaded += (_, _) => Desuscribir();
        Loaded += (_, _) => Suscribir();
        // La Galería se conserva al cambiar de pestaña (no llega Unloaded): al dejar de verse no debe quedar un giro a medias.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is false) DetenerGiro();
        };
    }

    private void Suscribir()
    {
        Desuscribir();
        if (DataContext is not QueVeoHoyViewModel vm) return;

        _viewModel = vm;
        vm.PropertyChanged += Vm_PropertyChanged;
    }

    private void Desuscribir()
    {
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged -= Vm_PropertyChanged;
            _viewModel = null;
        }
        DetenerGiro();
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(QueVeoHoyViewModel.FaseActualQueVer) || _viewModel == null) return;

        if (_viewModel.FaseActualQueVer == FaseQueVer.Buscando)
        {
            // Al abrir el panel el foco se lleva a él: si quedara en la galería (p. ej. en una tarjeta),
            // las teclas de flecha / AvPág seguirían desplazándola por detrás.
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => PanelQueVeoHoy.Focus()));
        }

        if (_viewModel.FaseActualQueVer == FaseQueVer.Girando)
        {
            IniciarGiro(_viewModel);
        }
        else if (_viewModel.FaseActualQueVer != FaseQueVer.Resultado)
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

    private void IniciarGiro(QueVeoHoyViewModel vm)
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
}
