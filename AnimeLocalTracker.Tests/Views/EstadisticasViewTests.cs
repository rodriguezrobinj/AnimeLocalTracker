using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using AnimeLocalTracker.Views;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Views;

/// <summary>
/// Colección no paralelizable que comparte UN anfitrión WPF (<see cref="WpfHostFixture"/>): WPF solo
/// permite una Application por proceso y crearla en un test puede interferir con el test host si
/// otros tests corren en paralelo (cuelga la suite).
/// </summary>
[CollectionDefinition("WpfSmoke", DisableParallelization = true)]
public class WpfSmokeDefinition : ICollectionFixture<WpfHostFixture>
{
}

/// <summary>
/// Test de humo de las vistas XAML: construye las vistas reales con los recursos que
/// referencia por StaticResource (AppText.PageTitle, BoolToVis y los estilos
/// MaterialDesign) para detectar errores de carga en tiempo de ejecución (iconos
/// inexistentes, bindings con modo incompatible, markup inválido, etc.) que el
/// compilador no puede ver.
/// Regresiones detectadas por este test:
/// - Estadísticas: binding TwoWay sobre el indexador de solo lectura de LocalizationService.
/// - Reproductor: bump de MaterialDesignThemes ci1462 eliminó los iconos
///   Rewind10/FastForward10 (XamlParseException al abrir un anime).
/// </summary>
[Collection("WpfSmoke")]
public class EstadisticasViewTests
{
    private readonly WpfHostFixture _host;

    public EstadisticasViewTests(WpfHostFixture host) => _host = host;

    [Fact]
    public void VistasPrincipales_DeberianCargarSinErrores()
    {
        _host.Ejecutar(() =>
        {
            _ = new EstadisticasView();
            _ = new LogrosView();
            _ = new ActualizacionesView();
            _ = new HistorialView();
            _ = new DescargasView();
            _ = new ReproductorView();
        });
    }

    [Fact]
    public async System.Threading.Tasks.Task MapaDeCalor_PintaCadaNivel_YLosDiasFuturosNoTienenTooltip()
    {
        var db = new Mock<IDatabaseService>();
        db.Setup(d => d.ObtenerTodosLosAnimesAsync()).ReturnsAsync(new System.Collections.Generic.List<AnimeItem>());
        db.Setup(d => d.ObtenerTodosLosRegistrosAsync()).ReturnsAsync(new System.Collections.Generic.List<RegistroEpisodio>
        {
            new() { AniListId = 1, NumeroEpisodio = 1, VistoLocal = true, UltimaReproduccion = System.DateTime.UtcNow }
        });
        var vm = new EstadisticasViewModel(db.Object, Mock.Of<IAnimeTrackingService>(), Mock.Of<IAuthService>(), Mock.Of<IDialogService>());
        await vm.CargarEstadisticasAsync();

        _host.Ejecutar(() =>
        {
            var vista = new EstadisticasView { DataContext = vm };
            vista.Measure(new Size(1200, 4000));
            vista.Arrange(new Rect(0, 0, 1200, 4000));
            vista.UpdateLayout();

            var celdas = (ItemsControl)vista.FindName("CeldasMapaCalor");
            Border BordeDe(int i) => (Border)VisualTreeHelper.GetChild(celdas.ItemContainerGenerator.ContainerFromIndex(i), 0);
            var mapa = vm.MapaCalor!;
            int hoy = mapa.Celdas.ToList().FindIndex(c => c.Fecha == System.DateTime.Today);
            int vacio = mapa.Celdas.ToList().FindIndex(c => c.Nivel == 0);
            int futuro = mapa.Celdas.ToList().FindIndex(c => c.Nivel == -1);

            BordeDe(hoy).Opacity.Should().BeApproximately(0.30, 0.001, "1 episodio = nivel 1");
            BordeDe(hoy).ToolTip.Should().Be(mapa.Celdas[hoy].Texto);
            BordeDe(vacio).Background.Should().BeSameAs(Application.Current.Resources["Brush.SurfaceChip"]);
            // Lo que lee un lector de pantalla: el elemento de la fila toma el nombre del dato, no del Border.
            var filas = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(celdas).GetChildren();
            filas[hoy].GetName().Should().Be(mapa.Celdas[hoy].Texto);
            if (futuro >= 0) ToolTipService.GetIsEnabled(BordeDe(futuro)).Should().BeFalse("un día futuro no debe mostrar un tooltip vacío");
        });
    }

    [Fact]
    public void MapaDeCalor_UnMesEnLaUltimaColumna_NoSeSaleDeLaCuadricula()
    {
        _host.Ejecutar(() =>
        {
            // Miércoles 2 de septiembre: el 1 cae en la última semana, que lleva la etiqueta "sept" (más ancha que su columna).
            var mapa = CalculadorMapaCalor.Calcular([], new System.DateTime(2026, 9, 2), System.Globalization.CultureInfo.GetCultureInfo("es-ES"));
            mapa.Meses[^1].Should().NotBeEmpty();
            var vista = new EstadisticasView { DataContext = new { MapaCalor = mapa } };
            vista.Measure(new Size(1200, 4000));
            vista.Arrange(new Rect(0, 0, 1200, 4000));
            vista.UpdateLayout();

            var meses = (ItemsControl)vista.FindName("MesesMapaCalor");
            var cuadricula = (FrameworkElement)vista.FindName("CuadriculaMapaCalor");
            var lienzo = VisualTreeHelper.GetChild(meses.ItemContainerGenerator.ContainerFromIndex(CalculadorMapaCalor.Semanas - 1), 0);
            var texto = (TextBlock)VisualTreeHelper.GetChild(lienzo, 0);
            var borde = texto.TransformToAncestor(cuadricula).TransformBounds(new Rect(texto.RenderSize));

            // El Viewbox escala la cuadrícula con su margen: lo que pase de ahí se pinta encima del relleno de la tarjeta.
            borde.Right.Should().BeLessThanOrEqualTo(cuadricula.ActualWidth + cuadricula.Margin.Right);
        });
    }
}
