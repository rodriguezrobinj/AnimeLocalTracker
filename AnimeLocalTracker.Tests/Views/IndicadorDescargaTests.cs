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
}
