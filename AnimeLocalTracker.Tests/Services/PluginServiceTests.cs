using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Core;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Python;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>SEC-01: PluginService lista con huellas, gestiona la confianza y solo ejecuta plugins aprobados. Nunca toca AppDataPaths.</summary>
public class PluginServiceTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), "alt_pluginsvc_" + Guid.NewGuid().ToString("N"));
    private readonly AppSettings _config = new();
    private readonly Mock<ISettingsService> _settings = new();
    private readonly Mock<IPythonBridgeService> _bridge = new();
    private readonly PluginService _sut;

    public PluginServiceTests()
    {
        Directory.CreateDirectory(_carpeta);
        _settings.Setup(s => s.ObtenerConfiguracion()).Returns(_config);
        _settings.Setup(s => s.GuardarConfiguracionAsync(It.IsAny<AppSettings>())).Returns(Task.CompletedTask);
        _bridge.Setup(b => b.IsAvailableAsync()).ReturnsAsync(true);
        _bridge.Setup(b => b.ExecuteCommandAsync<object, PluginDaemonResponse<string>>("run-plugin", It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PluginDaemonResponse<string> { Success = true, Result = "ok" });
        _sut = new PluginService(_bridge.Object, _settings.Object, _carpeta);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(_carpeta, true); } catch { /* ignore */ }
    }

    private string Crear(string nombre, string contenido = "x")
    {
        string ruta = Path.Combine(_carpeta, nombre);
        File.WriteAllText(ruta, contenido);
        return ruta;
    }

    [Fact]
    public void ObtenerDetallesPlugins_DeberiaListarSoloPyYDllConSuHuellaYEstado()
    {
        string ruta = Crear("a.py", "uno");
        Crear("b.dll", "dos");
        Crear("notas.txt", "ignorado");
        _config.PluginsConfiables["a.py"] = ConfianzaPlugins.CalcularSha256(ruta);

        var detalles = _sut.ObtenerDetallesPlugins();

        detalles.Select(d => d.Nombre).Should().Equal("a.py", "b.dll");
        detalles[0].Estado.Should().Be(EstadoConfianzaPlugin.Confiable);
        detalles[0].Sha256.Should().Be(ConfianzaPlugins.CalcularSha256(ruta)).And.HaveLength(64);
        detalles[0].Tipo.Should().Be(".py");
        detalles[1].Estado.Should().Be(EstadoConfianzaPlugin.SinConfiar);
    }

    [Fact]
    public void ObtenerDetallesPlugins_SiElArchivoCambioDespuesDeConfiar_DeberiaMarcarloModificado()
    {
        string ruta = Crear("a.py", "original");
        _config.PluginsConfiables["a.py"] = ConfianzaPlugins.CalcularSha256(ruta);
        File.WriteAllText(ruta, "reemplazado");

        _sut.ObtenerDetallesPlugins().Single().Estado.Should().Be(EstadoConfianzaPlugin.Modificado);
    }

    [Fact]
    public void ObtenerDetallesPlugins_SinCarpeta_DeberiaDevolverListaVacia()
    {
        Directory.Delete(_carpeta, true);

        _sut.ObtenerDetallesPlugins().Should().BeEmpty();
        _sut.ObtenerPluginsInstalados().Should().BeEmpty();
    }

    [Fact]
    public async Task EstablecerConfianzaAsync_Confiar_DeberiaFijarLaHuellaActualYGuardar()
    {
        string ruta = Crear("a.py", "contenido");

        bool ok = await _sut.EstablecerConfianzaAsync("a.py", true);

        ok.Should().BeTrue();
        _config.PluginsConfiables["a.py"].Should().Be(ConfianzaPlugins.CalcularSha256(ruta));
        _settings.Verify(s => s.GuardarConfiguracionAsync(It.IsAny<AppSettings>()), Times.Once);
    }

    [Fact]
    public async Task EstablecerConfianzaAsync_Retirar_DeberiaQuitarLaEntrada()
    {
        Crear("a.py");
        await _sut.EstablecerConfianzaAsync("a.py", true);

        bool ok = await _sut.EstablecerConfianzaAsync("a.py", false);

        ok.Should().BeTrue();
        _config.PluginsConfiables.Should().NotContainKey("a.py");
    }

    [Theory]
    [InlineData("..\\fuera.py")]
    [InlineData("../fuera.py")]
    [InlineData("sub\\a.py")]
    [InlineData("a.exe")]
    [InlineData("")]
    public async Task EstablecerConfianzaAsync_ConNombreInvalido_DeberiaRechazarSinGuardar(string nombre)
    {
        bool ok = await _sut.EstablecerConfianzaAsync(nombre, true);

        ok.Should().BeFalse();
        _settings.Verify(s => s.GuardarConfiguracionAsync(It.IsAny<AppSettings>()), Times.Never);
    }

    [Fact]
    public async Task EstablecerConfianzaAsync_ConArchivoInexistente_DeberiaDevolverFalse()
    {
        (await _sut.EstablecerConfianzaAsync("fantasma.py", true)).Should().BeFalse();
    }

    [Fact]
    public async Task EjecutarPluginAsync_PorDefecto_NoDeberiaLlamarAlDaemon()
    {
        Crear("a.py");

        var resultado = await _sut.EjecutarPluginAsync<object, string>("a.py", "f", new object());

        resultado.Should().BeNull();
        _bridge.Verify(b => b.ExecuteCommandAsync<object, PluginDaemonResponse<string>>(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EjecutarPluginAsync_ActivadoYConfiado_DeberiaEjecutarloConLaRutaDeLaCarpetaDePlugins()
    {
        string ruta = Crear("a.py");
        _config.PluginsHabilitados = true;
        await _sut.EstablecerConfianzaAsync("a.py", true);

        var resultado = await _sut.EjecutarPluginAsync<object, string>("a.py", "f", new object());

        resultado.Should().Be("ok");
        _bridge.Verify(b => b.ExecuteCommandAsync<object, PluginDaemonResponse<string>>("run-plugin",
            It.Is<object>(p => p.ToString()!.Contains(ruta)), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EjecutarPluginAsync_SiElArchivoCambioTrasConfiar_NoDeberiaEjecutarlo()
    {
        string ruta = Crear("a.py", "bueno");
        _config.PluginsHabilitados = true;
        await _sut.EstablecerConfianzaAsync("a.py", true);
        File.WriteAllText(ruta, "malo");

        var resultado = await _sut.EjecutarPluginAsync<object, string>("a.py", "f", new object());

        resultado.Should().BeNull();
        _bridge.Verify(b => b.ExecuteCommandAsync<object, PluginDaemonResponse<string>>(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("..\\..\\Windows\\x.py")]
    [InlineData("../x.py")]
    [InlineData("C:\\otro\\x.py")]
    public async Task EjecutarPluginAsync_ConNombreConRuta_DeberiaRechazarSinLlamarAlDaemon(string nombre)
    {
        _config.PluginsHabilitados = true;

        var resultado = await _sut.EjecutarPluginAsync<object, string>(nombre, "f", new object());

        resultado.Should().BeNull();
        _bridge.Verify(b => b.IsAvailableAsync(), Times.Never);
    }
}
