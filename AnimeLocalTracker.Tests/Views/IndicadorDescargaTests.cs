using AnimeLocalTracker.Controls;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Views;

/// <summary>El aro de descarga único (ficha, Actualizaciones, música): cuándo gira en espera y cuándo muestra el porcentaje.</summary>
public class IndicadorDescargaTests
{
    [Theory]
    [InlineData(0, true)]      // en cola o buscando el enlace
    [InlineData(0.5, false)]
    [InlineData(99.9, false)]
    [InlineData(100, true)]    // bajado pero sin terminar: descifrando (Mega), convirtiendo (música) o moviendo el archivo
    public void EnEspera_SoloSinAvanceOYaAlCien(double progreso, bool esperado) =>
        IndicadorDescarga.EnEspera(progreso).Should().Be(esperado);
}

[Collection("WpfSmoke")]
public class IndicadorDescargaVistaTests
{
    private readonly WpfHostFixture _host;

    public IndicadorDescargaVistaTests(WpfHostFixture host) => _host = host;

    [Fact]
    public void SoloSeConstruyeAlVerse_YMuestraElAvanceQueLlegoAntes()
    {
        _host.Ejecutar(() =>
        {
            // Como en una fila sin descarga: oculto. El avance puede llegar antes de que se vea.
            var indicador = new IndicadorDescarga { Progreso = 40 };
            var fila = new System.Windows.Controls.Grid { Visibility = System.Windows.Visibility.Collapsed, Children = { indicador } };
            using var fuente = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("prueba") { ParentWindow = new System.IntPtr(-3) })
            {
                RootVisual = fila
            };

            indicador.Content.Should().BeNull("mientras no se ve no se construye nada");

            fila.Visibility = System.Windows.Visibility.Visible;
            fila.UpdateLayout();

            indicador.Content.Should().NotBeNull();
            ((System.Windows.UIElement)indicador.FindName("Avance")).Visibility.Should().Be(System.Windows.Visibility.Visible, "40 % es avance, no espera");
            ((System.Windows.UIElement)indicador.FindName("Espera")).Visibility.Should().Be(System.Windows.Visibility.Collapsed);
        });
    }

    [Fact]
    public void EnUnaFilaQueNaceDescargando_SeConstruyeSinRomperLaPlantilla()
    {
        // Regresión real (2026-10-17): abrir Actualizaciones con descargas en marcha. El aro se hacía visible mientras WPF
        // aún montaba la plantilla de la fila, construirlo entonces lanzaba "BeginInit anidado" en cada pasada de diseño y
        // el diálogo de error anidado agotó la pila.
        _host.Ejecutar(() =>
        {
            var plantilla = (System.Windows.DataTemplate)System.Windows.Markup.XamlReader.Parse("""
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                              xmlns:ctl="clr-namespace:AnimeLocalTracker.Controls;assembly=AnimeLocalTracker">
                    <Grid><ctl:IndicadorDescarga Width="26" Height="26" Progreso="{Binding}"/></Grid>
                </DataTemplate>
                """);
            var fila = new System.Windows.Controls.ContentControl { Content = 40.0, ContentTemplate = plantilla };
            using var fuente = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("prueba") { ParentWindow = new System.IntPtr(-3) })
            {
                RootVisual = fila
            };

            fila.UpdateLayout();

            var indicador = BuscarIndicador(fila);
            indicador.Content.Should().NotBeNull("visible desde el principio: se construye en cuanto termina de inicializarse");
            ((System.Windows.UIElement)indicador.FindName("Avance")).Visibility.Should().Be(System.Windows.Visibility.Visible);
        });
    }

    private static IndicadorDescarga BuscarIndicador(System.Windows.DependencyObject raiz)
    {
        if (raiz is IndicadorDescarga indicador) return indicador;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(raiz); i++)
        {
            try { return BuscarIndicador(System.Windows.Media.VisualTreeHelper.GetChild(raiz, i)); }
            catch (System.InvalidOperationException) { }
        }
        throw new System.InvalidOperationException("No hay IndicadorDescarga en el árbol.");
    }
}
