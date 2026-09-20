using AnimeLocalTracker.Views;
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
            _ = new ReproductorView();
        });
    }
}
