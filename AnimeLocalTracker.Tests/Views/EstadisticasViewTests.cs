using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using AnimeLocalTracker.Views;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Views;

/// <summary>
/// Colección no paralelizable: crear una Application WPF en un test puede interferir
/// con el test host si otros tests corren en paralelo (cuelga la suite).
/// </summary>
[CollectionDefinition("WpfSmoke", DisableParallelization = true)]
public class WpfSmokeDefinition
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
    [Fact]
    public void VistasPrincipales_DeberianCargarSinErrores()
    {
        // WPF exige un hilo STA para los elementos visuales; xUnit corre en MTA por defecto.
        // Una sola Application por test: la segunda Application en el mismo AppDomain lanza.
        Exception? capturada = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new Application();
                // Recursos de la app que las vistas referencian por StaticResource
                app.Resources["AppText.PageTitle"] = new Style(typeof(TextBlock));
                app.Resources["BoolToVis"] = new BooleanToVisibilityConverter();
                // Estilos MaterialDesign (p. ej. MaterialDesignToolButton del reproductor)
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/MaterialDesignThemes.Wpf;component/Themes/MaterialDesign3.Defaults.xaml")
                });

                _ = new EstadisticasView();
                _ = new ReproductorView();

                // Limpieza: una Application viva (Application.Current + dispatcher) impide
                // que el testhost salga al terminar la suite → resetearla antes de morir.
                try { app.Dispatcher.InvokeShutdown(); } catch { }
                try
                {
                    var campo = typeof(Application).GetField("_appInstance",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                    campo?.SetValue(null, null);
                }
                catch { }
            }
            catch (Exception ex)
            {
                capturada = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30)).Should().BeTrue("la carga de las vistas debe completar sin colgarse");

        capturada.Should().BeNull("las vistas deben cargar sin excepciones XAML");
    }
}
