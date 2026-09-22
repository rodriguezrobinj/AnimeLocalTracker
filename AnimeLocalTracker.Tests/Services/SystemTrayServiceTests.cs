using System.Reflection;
using System.Windows;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Tests.Views;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// SystemTrayService crea un NotifyIcon real de WinForms, así que estas pruebas necesitan el hilo STA
/// compartido de <see cref="WpfHostFixture"/> (misma colección que las pruebas de vistas).
/// </summary>
[Collection("WpfSmoke")]
public class SystemTrayServiceTests
{
    private readonly WpfHostFixture _host;

    public SystemTrayServiceTests(WpfHostFixture host) => _host = host;

    private static Mock<ISettingsService> CrearSettingsConIconoActivo() =>
        CrearSettings(new AppSettings { MinimizarABandejaAlCerrar = true });

    private static Mock<ISettingsService> CrearSettings(AppSettings config)
    {
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.ObtenerConfiguracion()).Returns(config);
        return settings;
    }

    /// <summary>El icono de bandeja es privado (WinForms): se accede por reflexión, igual que su
    /// método interno OnBalloonTipClicked (no hay forma pública de simular el clic del usuario en el
    /// globo nativo de Windows).</summary>
    private static System.Windows.Forms.NotifyIcon ObtenerNotifyIcon(SystemTrayService servicio)
    {
        var campo = typeof(SystemTrayService).GetField("_notifyIcon", BindingFlags.NonPublic | BindingFlags.Instance);
        var icono = campo!.GetValue(servicio) as System.Windows.Forms.NotifyIcon;
        icono.Should().NotBeNull("el icono debe existir porque la configuración lo activa");
        return icono!;
    }

    private static void SimularClicEnGlobo(System.Windows.Forms.NotifyIcon icono)
    {
        var metodo = typeof(System.Windows.Forms.NotifyIcon).GetMethod("OnBalloonTipClicked", BindingFlags.NonPublic | BindingFlags.Instance);
        metodo.Should().NotBeNull();
        metodo!.Invoke(icono, null);
    }

    private static Window CrearVentanaDePrueba() => new()
    {
        Width = 1,
        Height = 1,
        Left = -5000,
        Top = -5000,
        ShowInTaskbar = false,
        WindowStyle = WindowStyle.None
    };

    [Fact]
    public void ClicEnElGloboDeLaNotificacion_DeberiaRestaurarLaVentana()
    {
        _host.Ejecutar(() =>
        {
            var servicio = new SystemTrayService(CrearSettingsConIconoActivo().Object);
            var ventana = CrearVentanaDePrueba();
            try
            {
                servicio.Habilitar(ventana);
                ventana.WindowState = WindowState.Minimized;

                SimularClicEnGlobo(ObtenerNotifyIcon(servicio));

                ventana.WindowState.Should().Be(WindowState.Normal, "el clic en el globo debe restaurar la ventana como el doble clic del icono");
                ventana.IsVisible.Should().BeTrue("RestaurarVentana() llama a Show()");
            }
            finally
            {
                servicio.Dispose();
                ventana.Close();
            }
        });
    }

    [Fact]
    public void ClicEnElGlobo_SinIconoDeBandejaActivo_NoDeberiaFallar()
    {
        // Con la opción desactivada no hay NotifyIcon que reciba el clic; esto documenta que
        // simplemente no hay nada que probar (no debe lanzar al intentar acceder a él).
        _host.Ejecutar(() =>
        {
            var servicio = new SystemTrayService(CrearSettings(new AppSettings()).Object);
            var ventana = CrearVentanaDePrueba();
            try
            {
                servicio.Habilitar(ventana);

                var campo = typeof(SystemTrayService).GetField("_notifyIcon", BindingFlags.NonPublic | BindingFlags.Instance);
                campo!.GetValue(servicio).Should().BeNull("MinimizarABandejaAlCerrar y NotificarConBandejaSiempre están desactivados");
            }
            finally
            {
                servicio.Dispose();
                ventana.Close();
            }
        });
    }
}
