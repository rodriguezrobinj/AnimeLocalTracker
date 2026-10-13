using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using AnimeLocalTracker.Views;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Views;

/// <summary>
/// Teclas y clics del reproductor que se perdían: los atajos dejaban de responder cuando el foco caía en la ventana (tras elegir
/// una opción de un menú) y el segundo de dos clics seguidos se descartaba por contar como doble clic.
/// </summary>
[Collection("WpfSmoke")]
public class ReproductorTeclasYClicsTests
{
    private readonly WpfHostFixture _host;

    public ReproductorTeclasYClicsTests(WpfHostFixture host) => _host = host;

    private static ReproductorViewModel CrearVm() =>
        new(Mock.Of<IDatabaseService>(), Mock.Of<IAnimeTrackingService>(), Mock.Of<IAuthService>());

    /// <summary>Un doble clic tal como le llega a la vista: se lanza sobre ella con el elemento pulsado como origen.</summary>
    private static MouseButtonEventArgs DobleClicSobre(ReproductorView vista, object origen)
    {
        var clic = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = Control.PreviewMouseDoubleClickEvent,
            Source = origen
        };
        vista.RaiseEvent(clic);
        return clic;
    }

    [Fact]
    public void ElSegundoClicSeguidoEnUnBoton_NoSeDescarta()
    {
        _host.Ejecutar(() =>
        {
            var vista = new ReproductorView { DataContext = CrearVm() };

            var clic = DobleClicSobre(vista, vista.FindName("MoreOptionsButton"));

            clic.Handled.Should().BeFalse("cerrar un menú y volver a pulsar su botón enseguida tiene que abrirlo: si la vista da el doble clic por atendido, el botón no recibe el segundo clic");
        });
    }

    [Fact]
    public void EnElMini_ElDobleClicEnElVideoVuelveAlFormatoHabitual_PeroNoSobreUnBoton()
    {
        _host.Ejecutar(() =>
        {
            var vm = CrearVm();
            var vista = new ReproductorView { DataContext = vm };

            vm.EsModoMini = true;
            var enBoton = DobleClicSobre(vista, vista.FindName("MoreOptionsButton"));
            enBoton.Handled.Should().BeFalse();
            vm.EsModoMini.Should().BeTrue("dos clics seguidos en un botón del mini son dos pulsaciones, no la orden de agrandarlo");

            var enVideo = DobleClicSobre(vista, vista);
            enVideo.Handled.Should().BeTrue();
            vm.EsModoMini.Should().BeFalse();
        });
    }

    [Fact]
    public void UnClicEnLaBarraDelMini_SaltaAEsePunto_ComoEnLaBarraGrande()
    {
        _host.Ejecutar(() =>
        {
            var vm = CrearVm();
            vm.TotalSeconds = 1420;
            var vista = new ReproductorView { DataContext = vm };

            foreach (string barra in (string[])["ProgressBarArea", "BarraProgresoMini"])
            {
                var clic = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent };
                (vista.FindName(barra) as UIElement)?.RaiseEvent(clic);

                clic.Handled.Should().BeTrue($"el clic en {barra} lo atiende la vista (salta a ese punto): si se lo deja al deslizador, solo se mueve la bolita y el video sigue donde estaba");
            }
        });
    }

    [Fact]
    public void ConElFocoEnLaVentana_LosAtajosDelReproductorSiguenRespondiendo()
    {
        _host.Ejecutar(() =>
        {
            var vm = CrearVm();
            var vista = new ReproductorView { DataContext = vm };
            var otroBoton = new Button();
            var ventana = new Window { Content = new Grid { Children = { vista, otroBoton } } };
            using var fuente = new HwndSource(new HwndSourceParameters("prueba") { ParentWindow = new IntPtr(-3) }); // solo de mensajes: no se ve
            KeyEventArgs Pulsar(UIElement dondeEstaElFoco)
            {
                var tecla = new KeyEventArgs(Keyboard.PrimaryDevice, fuente, 0, Key.L) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                dondeEstaElFoco.RaiseEvent(tecla);
                return tecla;
            }

            vista.EscucharTeclasDe(ventana);

            // Tras elegir una opción de un menú, WPF deja el foco en la propia ventana: la tecla ya no pasa por la vista.
            Pulsar(ventana).Handled.Should().BeTrue();
            vm.CajonEpisodiosAbierto.Should().BeTrue("L abre el cajón de episodios aunque el foco no esté dentro de la vista");

            Pulsar(otroBoton).Handled.Should().BeFalse("con el foco en otro control de la ventana (el botón de un diálogo) la tecla es suya");
            vm.CajonEpisodiosAbierto.Should().BeTrue();

            vista.EscucharTeclasDe(null);
            Pulsar(ventana).Handled.Should().BeFalse("al salir de la ventana la vista deja de escucharla");
        });
    }
}
