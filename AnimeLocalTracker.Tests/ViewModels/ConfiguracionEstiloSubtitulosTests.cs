using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

public class ConfiguracionEstiloSubtitulosTests
{
    private static (ConfiguracionViewModel Vm, Mock<ISettingsService> Settings, AppSettings Config) CrearSut(AppSettings? config = null)
    {
        config ??= new AppSettings();
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.ObtenerConfiguracion()).Returns(config);
        var db = Mock.Of<IDatabaseService>();

        var vm = new ConfiguracionViewModel(
            settings.Object,
            Mock.Of<IAuthService>(),
            db,
            Mock.Of<IDialogService>(),
            new CacheMaintenanceService(db),
            Mock.Of<IPluginService>());
        return (vm, settings, config);
    }

    [Fact]
    public void AlAbrir_DeberiaMostrarElEstiloGuardado()
    {
        var (vm, _, _) = CrearSut(new AppSettings
        {
            EstiloSubtitulos = new EstiloSubtitulos { Fuente = "Verdana", Tamano = 60, Cursiva = true, ColorTexto = "#FFE14D", OpacidadFondo = 40 }
        });

        vm.EstiloFuente.Should().Be("Verdana");
        vm.EstiloTamano.Should().Be(60);
        vm.EstiloCursiva.Should().BeTrue();
        vm.EstiloColorTexto.Should().Be("#FFE14D");
        vm.EstiloOpacidadFondo.Should().Be(40);
        vm.EstiloVistaPrevia.Fuente.Should().Be("Verdana");
    }

    [Fact]
    public void CambiarUnaOpcion_DeberiaActualizarLaVistaPreviaAlInstante()
    {
        var (vm, _, _) = CrearSut();
        var antes = vm.EstiloVistaPrevia;

        vm.EstiloSubrayado = true;
        vm.EstiloTamano = 70;
        vm.EstiloColorContorno = "#4DE1FF";

        vm.EstiloVistaPrevia.Should().NotBeSameAs(antes);
        vm.EstiloVistaPrevia.Subrayado.Should().BeTrue();
        vm.EstiloVistaPrevia.Tamano.Should().Be(70);
        vm.EstiloVistaPrevia.ColorContorno.Should().Be("#4DE1FF");
    }

    [Fact]
    public void VistaPrevia_ConValoresFueraDeRango_DeberiaMostrarValoresCorregidos()
    {
        var (vm, _, _) = CrearSut();

        vm.EstiloTamano = 999;
        vm.EstiloOpacidadFondo = 250;

        vm.EstiloVistaPrevia.Tamano.Should().Be(EstiloSubtitulos.TamanoMaximo);
        vm.EstiloVistaPrevia.OpacidadFondo.Should().Be(100);
    }

    [Fact]
    public void Restablecer_DeberiaVolverAlAspectoOriginalSinGuardar()
    {
        var (vm, settings, _) = CrearSut(new AppSettings
        {
            EstiloSubtitulos = new EstiloSubtitulos { Fuente = "Impact", Tamano = 70, Negrita = false, Subrayado = true, GrosorBorde = 3 }
        });

        vm.RestablecerEstiloSubtitulosCommand.Execute(null);

        vm.EstiloFuente.Should().Be("Trebuchet MS");
        vm.EstiloTamano.Should().Be(44);
        vm.EstiloNegrita.Should().BeTrue();
        vm.EstiloSubrayado.Should().BeFalse();
        vm.EstiloGrosorBorde.Should().Be(0);
        vm.EstiloVistaPrevia.Should().BeEquivalentTo(new EstiloSubtitulos());
        settings.Verify(s => s.GuardarConfiguracionAsync(It.IsAny<AppSettings>()), Times.Never,
            "restablecer solo cambia la pantalla; se guarda con «Guardar preferencias»");
    }

    [Fact]
    public async Task Guardar_DeberiaPersistirElEstiloElegido()
    {
        var (vm, settings, config) = CrearSut();
        AppSettings? guardada = null;
        settings.Setup(s => s.GuardarConfiguracionAsync(It.IsAny<AppSettings>()))
            .Callback<AppSettings>(c => guardada = c)
            .Returns(Task.CompletedTask);

        vm.EstiloFuente = "Georgia";
        vm.EstiloTamano = 56;
        vm.EstiloNegrita = false;
        vm.EstiloCursiva = true;
        vm.EstiloColorTexto = "#7CFC7C";
        vm.EstiloGrosorContorno = 5;
        vm.EstiloOpacidadFondo = 60;
        vm.EstiloGrosorBorde = 2;
        vm.EstiloColorBorde = "#FF8FD0";
        await vm.GuardarPreferenciasAsync();

        guardada.Should().BeSameAs(config);
        guardada!.EstiloSubtitulos.Should().BeEquivalentTo(new EstiloSubtitulos
        {
            Fuente = "Georgia", Tamano = 56, Negrita = false, Cursiva = true, Subrayado = false,
            ColorTexto = "#7CFC7C", ColorContorno = "#000000", GrosorContorno = 5,
            OpacidadFondo = 60, GrosorBorde = 2, ColorBorde = "#FF8FD0"
        });
    }

    [Fact]
    public void Paleta_DeberiaOfrecerTodosLosColoresConNombreTraducido()
    {
        var (vm, _, _) = CrearSut();

        vm.ColoresSubtitulos.Select(c => c.Hex).Should().BeEquivalentTo(EstiloSubtitulos.Paleta.Select(c => c.Hex));
        vm.ColoresSubtitulos.Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c.Nombre) && c.Nombre != c.Clave);
        vm.FuentesSubtitulos.Should().Contain(EstiloSubtitulos.FuentePorDefecto);
    }
}
