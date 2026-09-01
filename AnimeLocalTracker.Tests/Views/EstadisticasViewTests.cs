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
/// Test de humo de las vistas XAML: construye la vista real con los recursos que
/// referencia por StaticResource (los temas MaterialDesign se resuelven por
/// DynamicResource y no rompen la carga) para detectar errores de carga en tiempo
/// de ejecución (bindings con modo incompatible, markup inválido, etc.) que el
/// compilador no puede ver.
/// Regresión: la pestaña de Estadísticas dejó de abrir por un binding TwoWay sobre
/// el indexador de solo lectura de LocalizationService (Run.Text es TwoWay por
/// defecto). Este test la habría detectado al instante.
/// </summary>
[Collection("WpfSmoke")]
public class EstadisticasViewTests
{
    [Fact]
    public void EstadisticasView_DeberiaCargarSinErrores()
    {
        // WPF exige un hilo STA para los elementos visuales; xUnit corre en MTA por defecto
        Exception? capturada = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new Application();
                // Recursos de la app que la vista referencia por StaticResource
                app.Resources["AppText.PageTitle"] = new Style(typeof(TextBlock));
                app.Resources["BoolToVis"] = new BooleanToVisibilityConverter();
                _ = new EstadisticasView();

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
        thread.Join(TimeSpan.FromSeconds(30)).Should().BeTrue("la carga de la vista debe completar sin colgarse");

        capturada.Should().BeNull("la vista de estadísticas debe cargar sin excepciones XAML");
    }
}
