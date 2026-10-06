using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>Configuración → Descargas → "Eliminar el video tras verlo": el modo, el número de episodios a conservar y el aviso.</summary>
public class ConfiguracionEliminarTrasVerTests
{
    private readonly Mock<IDialogService> _dialogos = new();

    private ConfiguracionViewModel CrearSut(AppSettings config)
    {
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.ObtenerConfiguracion()).Returns(config);
        settings.Setup(s => s.GuardarConfiguracionAsync(It.IsAny<AppSettings>())).Returns(Task.CompletedTask);
        var db = Mock.Of<IDatabaseService>();
        return new ConfiguracionViewModel(settings.Object, Mock.Of<IAuthService>(), db, _dialogos.Object,
            new CacheMaintenanceService(db), Mock.Of<IPluginService>());
    }

    private void ResponderAviso(bool acepta) =>
        _dialogos.Setup(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(acepta);

    private void VerificarAvisos(Times veces) =>
        _dialogos.Verify(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<string>(), It.IsAny<string>()), veces);

    [Fact]
    public void AlAbrir_MuestraLoGuardado()
    {
        var sut = CrearSut(new AppSettings { ModoEliminarTrasVer = ModoEliminarTrasVerValores.ConsumoLigero, EpisodiosAConservar = 5 });

        sut.ModoEliminarTrasVer.Should().Be(ModoEliminarTrasVerValores.ConsumoLigero);
        sut.EpisodiosAConservar.Should().Be(5);
        sut.EsConsumoLigero.Should().BeTrue();
        VerificarAvisos(Times.Never()); // cargar lo guardado no pregunta nada
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("otra cosa")]
    public void AlAbrir_ConUnValorRaro_QuedaApagado(string? valor)
    {
        var sut = CrearSut(new AppSettings { ModoEliminarTrasVer = valor! });

        sut.ModoEliminarTrasVer.Should().Be(ModoEliminarTrasVerValores.Apagado);
        sut.EsConsumoLigero.Should().BeFalse();
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(99, 10)]
    public void AlAbrir_ElNumeroAConservarSeAcotaDe1a10(int guardado, int esperado)
    {
        CrearSut(new AppSettings { EpisodiosAConservar = guardado }).EpisodiosAConservar.Should().Be(esperado);
    }

    [Fact]
    public async Task ElegirUnModoQueBorra_Aceptando_LoDejaElegido()
    {
        ResponderAviso(true);
        var sut = CrearSut(new AppSettings());

        sut.ModoEliminarTrasVer = ModoEliminarTrasVerValores.Automatico;
        await Task.Delay(50); // el aviso se resuelve fuera del setter

        sut.ModoEliminarTrasVer.Should().Be(ModoEliminarTrasVerValores.Automatico);
        VerificarAvisos(Times.Once());
    }

    [Fact]
    public async Task ElegirUnModoQueBorra_Rechazando_VuelveAlAnterior()
    {
        ResponderAviso(false);
        var sut = CrearSut(new AppSettings());

        sut.ModoEliminarTrasVer = ModoEliminarTrasVerValores.Automatico;
        await Task.Delay(50);

        sut.ModoEliminarTrasVer.Should().Be(ModoEliminarTrasVerValores.Apagado);
        VerificarAvisos(Times.Once());
    }

    [Fact]
    public async Task ApagarElModo_NoPregunta()
    {
        ResponderAviso(true);
        var sut = CrearSut(new AppSettings { ModoEliminarTrasVer = ModoEliminarTrasVerValores.Automatico });

        sut.ModoEliminarTrasVer = ModoEliminarTrasVerValores.Apagado;
        await Task.Delay(50);

        sut.ModoEliminarTrasVer.Should().Be(ModoEliminarTrasVerValores.Apagado);
        VerificarAvisos(Times.Never());
    }

    [Fact]
    public async Task Guardar_EscribeElModoYElNumero()
    {
        ResponderAviso(true);
        var config = new AppSettings();
        var sut = CrearSut(config);

        sut.ModoEliminarTrasVer = ModoEliminarTrasVerValores.ConsumoLigero;
        await Task.Delay(50);
        sut.EpisodiosAConservar = 4;
        await sut.GuardarPreferenciasAsync();

        config.ModoEliminarTrasVer.Should().Be(ModoEliminarTrasVerValores.ConsumoLigero);
        config.EpisodiosAConservar.Should().Be(4);
    }
}
