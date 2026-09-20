using System;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FluentAssertions;

namespace AnimeLocalTracker.Tests.Views;

/// <summary>
/// WPF solo permite UNA <see cref="Application"/> por proceso (la segunda lanza), y sus vistas exigen un
/// hilo STA. Este anfitrión la crea una única vez, con los recursos que las vistas referencian por
/// StaticResource, y ejecuta cada prueba dentro de su hilo. Se comparte entre todas las pruebas de vistas
/// (colección "WpfSmoke", sin paralelizar).
/// </summary>
public sealed class WpfHostFixture : IDisposable
{
    private static readonly TimeSpan TiempoMaximo = TimeSpan.FromSeconds(30);

    private readonly Thread _hilo;
    private readonly ManualResetEventSlim _listo = new();
    private Dispatcher? _dispatcher;
    private Exception? _errorDeArranque;

    public WpfHostFixture()
    {
        _hilo = new Thread(() =>
        {
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

                // Recursos de la app que las vistas referencian por StaticResource
                app.Resources["AppText.PageTitle"] = new Style(typeof(TextBlock));
                app.Resources["AppEmptyTitle"] = new Style(typeof(TextBlock));
                app.Resources["AppEmptyText"] = new Style(typeof(TextBlock));
                app.Resources["AppPrimaryButton"] = new Style(typeof(Button));
                app.Resources["ShimmerEffectStyle"] = new Style(typeof(Border));
                app.Resources["AppWatchProgressBar"] = new Style(typeof(ProgressBar));
                app.Resources["AppWatchProgressBar.Overlay"] = new Style(typeof(ProgressBar));
                app.Resources["BoolToVis"] = new BooleanToVisibilityConverter();
                app.Resources["InverseBoolToVis"] = new MaterialDesignThemes.Wpf.Converters.BooleanToVisibilityConverter
                {
                    TrueValue = Visibility.Collapsed,
                    FalseValue = Visibility.Visible
                };
                app.Resources["InverseBool"] = new AnimeLocalTracker.Converters.InverseBoolConverter();
                app.Resources["ZeroToVis"] = new AnimeLocalTracker.Converters.ZeroToVisibilityConverter();
                // Estilos MaterialDesign (p. ej. MaterialDesignToolButton del reproductor)
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/MaterialDesignThemes.Wpf;component/Themes/MaterialDesign3.Defaults.xaml")
                });

                _dispatcher = Dispatcher.CurrentDispatcher;
                _listo.Set();
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                _errorDeArranque = ex;
                _listo.Set();
            }
        })
        {
            IsBackground = true,
            Name = "WpfTestHost"
        };
        _hilo.SetApartmentState(ApartmentState.STA);
        _hilo.Start();

        _listo.Wait(TiempoMaximo).Should().BeTrue("el anfitrión WPF debe arrancar");
        _errorDeArranque.Should().BeNull("el anfitrión WPF debe crear su Application sin errores");
    }

    /// <summary>Ejecuta <paramref name="prueba"/> en el hilo STA del anfitrión; relanza su excepción si falla.</summary>
    public void Ejecutar(Action prueba)
    {
        Exception? error = null;
        bool terminada = false;

        _dispatcher!.Invoke(() =>
        {
            try { prueba(); }
            catch (Exception ex) { error = ex; }
            finally { terminada = true; }
        }, DispatcherPriority.Normal, CancellationToken.None, TiempoMaximo);

        terminada.Should().BeTrue("la prueba de la vista debe completar sin colgarse");
        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    public void Dispose()
    {
        // Una Application viva (Application.Current + dispatcher) impide que el testhost salga.
        try { _dispatcher?.InvokeShutdown(); } catch { /* ignore */ }
        _hilo.Join(TimeSpan.FromSeconds(5));

        try
        {
            typeof(Application).GetField("_appInstance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, null);
        }
        catch { /* ignore */ }

        _listo.Dispose();
    }
}
