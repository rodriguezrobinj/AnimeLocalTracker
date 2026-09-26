using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using MaterialDesignThemes.Wpf;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>SEC-01: pestaña Plugins — interruptor general, confianza por archivo con confirmación, y textos traducidos.</summary>
public class ConfiguracionPluginsTests
{
    private readonly AppSettings _config = new();
    private readonly Mock<ISettingsService> _settings = new();
    private readonly Mock<IPluginService> _plugins = new();
    private readonly Mock<IDialogService> _dialogo = new();
    private readonly List<PluginInfo> _detalles = new();

    private ConfiguracionViewModel CrearSut()
    {
        _settings.Setup(s => s.ObtenerConfiguracion()).Returns(_config);
        _settings.Setup(s => s.GuardarConfiguracionAsync(It.IsAny<AppSettings>())).Returns(Task.CompletedTask);
        _plugins.Setup(p => p.ObtenerDetallesPlugins()).Returns(() => _detalles.ToList());
        _plugins.Setup(p => p.ObtenerPluginsInstalados()).Returns(() => _detalles.Select(d => d.Nombre).ToList());
        _plugins.Setup(p => p.EstablecerConfianzaAsync(It.IsAny<string>(), It.IsAny<bool>())).ReturnsAsync(true);
        _dialogo.Setup(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);

        var db = Mock.Of<IDatabaseService>();
        return new ConfiguracionViewModel(_settings.Object, Mock.Of<IAuthService>(), db, _dialogo.Object,
            new CacheMaintenanceService(db), _plugins.Object);
    }

    private static PluginInfo Info(string nombre, EstadoConfianzaPlugin estado, string hash = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")
        => new(nombre, nombre.EndsWith(".py") ? ".py" : ".dll", hash, estado);

    [Fact]
    public void PorDefecto_LosPluginsVienenApagados()
    {
        CrearSut().PluginsHabilitados.Should().BeFalse();
    }

    [Fact]
    public void AlAbrir_ConLosPluginsActivados_DeberiaReflejarloSinGuardar()
    {
        _config.PluginsHabilitados = true;

        var vm = CrearSut();

        vm.PluginsHabilitados.Should().BeTrue();
        _settings.Verify(s => s.GuardarConfiguracionAsync(It.IsAny<AppSettings>()), Times.Never, "rellenar la pantalla no debe guardar");
        vm.PluginsRequiereReinicio.Should().BeFalse();
    }

    [Fact]
    public void ActivarElInterruptor_DeberiaGuardarAlInstanteYAvisarDelReinicio()
    {
        var vm = CrearSut();

        vm.PluginsHabilitados = true;

        _config.PluginsHabilitados.Should().BeTrue();
        _settings.Verify(s => s.GuardarConfiguracionAsync(It.IsAny<AppSettings>()), Times.Once);
        vm.PluginsRequiereReinicio.Should().BeTrue("los .dll solo se cargan al iniciar");
    }

    [Fact]
    public void PluginsDetalle_DeberiaMostrarCadaArchivoConSuEstado()
    {
        _detalles.Add(Info("a.py", EstadoConfianzaPlugin.Confiable));
        _detalles.Add(Info("b.dll", EstadoConfianzaPlugin.Modificado));
        _detalles.Add(Info("c.dll", EstadoConfianzaPlugin.SinConfiar));

        var vm = CrearSut();

        vm.PluginsDetalle.Select(p => p.Nombre).Should().Equal("a.py", "b.dll", "c.dll");
        vm.PluginsDetalle.Select(p => p.EsConfiable).Should().Equal(true, false, false);
        vm.PluginsDetalle[0].Tipo.Should().Be("Python");
        vm.PluginsDetalle[1].Tipo.Should().Be("C#");
        vm.PluginsDetalle[0].HashCorto.Should().Be("0123456789AB…");
        vm.PluginsDetalle[0].HashCompleto.Should().HaveLength(64);
    }

    [Fact]
    public void PluginsDetalle_SiElServicioDevuelveNull_NoDeberiaFallar()
    {
        var vm = CrearSut();
        _plugins.Setup(p => p.ObtenerDetallesPlugins()).Returns((IReadOnlyList<PluginInfo>)null!);
        _plugins.Setup(p => p.ObtenerPluginsInstalados()).Returns((List<string>)null!);

        Action recargar = () => vm.CargarPlugins();

        recargar.Should().NotThrow();
        vm.PluginsDetalle.Should().BeEmpty();
    }

    [Fact]
    public async Task Confiar_ConConfirmacion_DeberiaPedirlaConLaHuellaYFijarLaConfianza()
    {
        _detalles.Add(Info("c.dll", EstadoConfianzaPlugin.SinConfiar));
        var vm = CrearSut();

        await vm.AlternarConfianzaPluginCommand.ExecuteAsync(vm.PluginsDetalle[0]);

        _dialogo.Verify(d => d.MostrarDialogoAsync(It.IsAny<string>(),
            It.Is<string>(m => m.Contains("c.dll") && m.Contains(vm.PluginsDetalle[0].HashCompleto)),
            true, ConfiguracionViewModel.IconoConfiarPlugin, "#F59E0B"), Times.Once);
        _plugins.Verify(p => p.EstablecerConfianzaAsync("c.dll", true), Times.Once);
        vm.PluginsRequiereReinicio.Should().BeTrue();
    }

    [Fact]
    public async Task Confiar_SiElUsuarioCancela_NoDeberiaCambiarNada()
    {
        _detalles.Add(Info("c.dll", EstadoConfianzaPlugin.SinConfiar));
        var vm = CrearSut();
        _dialogo.Setup(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(false);

        await vm.AlternarConfianzaPluginCommand.ExecuteAsync(vm.PluginsDetalle[0]);

        _plugins.Verify(p => p.EstablecerConfianzaAsync(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        vm.PluginsRequiereReinicio.Should().BeFalse();
    }

    [Fact]
    public async Task ConfiarEnUnPluginModificado_DeberiaPedirConfirmacionOtraVez()
    {
        _detalles.Add(Info("b.dll", EstadoConfianzaPlugin.Modificado));
        var vm = CrearSut();

        await vm.AlternarConfianzaPluginCommand.ExecuteAsync(vm.PluginsDetalle[0]);

        _dialogo.Verify(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        _plugins.Verify(p => p.EstablecerConfianzaAsync("b.dll", true), Times.Once);
    }

    [Fact]
    public async Task QuitarConfianza_NoDeberiaPedirConfirmacion()
    {
        _detalles.Add(Info("a.py", EstadoConfianzaPlugin.Confiable));
        var vm = CrearSut();

        await vm.AlternarConfianzaPluginCommand.ExecuteAsync(vm.PluginsDetalle[0]);

        _dialogo.Verify(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _plugins.Verify(p => p.EstablecerConfianzaAsync("a.py", false), Times.Once);
    }

    [Fact]
    public void TextosDeLasFilas_DeberianEstarTraducidosYCambiarConLaAccion()
    {
        var sinConfiar = new PluginItemViewModel(Info("a.py", EstadoConfianzaPlugin.SinConfiar));
        var confiable = new PluginItemViewModel(Info("b.py", EstadoConfianzaPlugin.Confiable));

        foreach (var fila in new[] { sinConfiar, confiable })
        {
            fila.TextoEstado.Should().NotStartWith("Cfg_");
            fila.TextoAccion.Should().NotStartWith("Cfg_");
        }
        sinConfiar.TextoAccion.Should().NotBe(confiable.TextoAccion);
        sinConfiar.TextoEstado.Should().NotBe(confiable.TextoEstado);
    }

    [Theory]
    [InlineData("Cfg_PluginsPermitir")]
    [InlineData("Cfg_PluginsPermitirSub")]
    [InlineData("Cfg_PluginsAviso")]
    [InlineData("Cfg_PluginConfiar")]
    [InlineData("Cfg_PluginQuitarConfianza")]
    [InlineData("Cfg_PluginEstado_Confiable")]
    [InlineData("Cfg_PluginEstado_SinConfiar")]
    [InlineData("Cfg_PluginEstado_Modificado")]
    [InlineData("Cfg_PluginConfiarTitulo")]
    [InlineData("Cfg_PluginConfiarMsj")]
    [InlineData("Cfg_PluginsReiniciar")]
    public void ClavesDeLocalizacion_DeberianEstarTraducidas(string clave)
    {
        LocalizationService.T(clave).Should().NotBe(clave, $"falta la traducción de {clave}");
    }

    [Fact]
    public void MensajeDeConfirmacion_DeberiaAdmitirNombreYHuella()
    {
        string texto = string.Format(LocalizationService.T("Cfg_PluginConfiarMsj"), "x.dll", "ABC123");

        texto.Should().Contain("x.dll").And.Contain("ABC123");
    }

    [Fact]
    public void IconoDelAviso_DeberiaExistirEnMaterialDesign()
    {
        Enum.TryParse<PackIconKind>(ConfiguracionViewModel.IconoConfiarPlugin, out _).Should().BeTrue();
    }
}
