using System;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Mal;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>Configuración → Descargas y cuenta → MyAnimeList: conectar, desconectar y sincronizar ahora.</summary>
public class ConfiguracionMalTests
{
    private readonly Mock<IDialogService> _dialogos = new();
    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<IMalAuthService> _auth = new();
    private readonly Mock<IMalApiClient> _api = new();
    private readonly Mock<IMalSyncService> _sync = new();

    private ConfiguracionViewModel CrearSut(bool configurado = true, bool conectado = false)
    {
        _auth.SetupGet(a => a.Configurado).Returns(configurado);
        _auth.SetupGet(a => a.Conectado).Returns(conectado);
        _api.Setup(a => a.UsuarioAsync(It.IsAny<CancellationToken>())).ReturnsAsync("robin_anime");
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.ObtenerConfiguracion()).Returns(new AppSettings());
        return new ConfiguracionViewModel(settings.Object, Mock.Of<IAuthService>(), _db.Object, _dialogos.Object,
            new CacheMaintenanceService(_db.Object), Mock.Of<IPluginService>(), malAuth: _auth.Object, malApi: _api.Object, malSync: _sync.Object);
    }

    [Fact]
    public void SinClientId_ElGrupoNoAparece()
        => CrearSut(configurado: false).MalDisponible.Should().BeFalse();

    [Fact]
    public async Task Conectado_MuestraElUsuario()
    {
        var sut = CrearSut(conectado: true);
        await sut.CargaMal;

        sut.MalConectado.Should().BeTrue();
        sut.MalEstadoTexto.Should().Be(string.Format(LocalizationService.T("Cfg_MalConectadoComoFormato"), "robin_anime"));
    }

    [Fact]
    public void SinConectar_LoDice()
    {
        var sut = CrearSut(conectado: false);

        sut.MalConectado.Should().BeFalse();
        sut.MalEstadoTexto.Should().Be(LocalizationService.T("Cfg_MalSinConectar"));
    }

    [Fact]
    public async Task Conectar_Bien_QuedaConectadoYEmpiezaASincronizar()
    {
        var sut = CrearSut();
        _auth.Setup(a => a.ConectarAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true)
            .Callback(() => _auth.SetupGet(a => a.Conectado).Returns(true));

        await sut.ConectarMalCommand.ExecuteAsync(null);

        sut.MalConectado.Should().BeTrue();
        _sync.Verify(s => s.SincronizarAhoraAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Conectar_Falla_AvisaYSigueSinConectar()
    {
        var sut = CrearSut();
        _auth.Setup(a => a.ConectarAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await sut.ConectarMalCommand.ExecuteAsync(null);

        sut.MalConectado.Should().BeFalse();
        _dialogos.Verify(d => d.MostrarDialogoAsync(LocalizationService.T("Cfg_MalErrorConectarTitulo"), It.IsAny<string>(), false, It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task Desconectar_BorraLosTokensYLoEnviado()
    {
        var sut = CrearSut(conectado: true);
        _auth.Setup(a => a.Desconectar()).Callback(() => _auth.SetupGet(a => a.Conectado).Returns(false));

        await sut.DesconectarMalCommand.ExecuteAsync(null);

        _auth.Verify(a => a.Desconectar(), Times.Once);
        _db.Verify(d => d.BorrarSincronizacionesMalAsync(), Times.Once);
        sut.MalConectado.Should().BeFalse();
    }

    [Fact]
    public async Task SincronizarAhora_MuestraElResultado()
    {
        var fin = new DateTime(2026, 10, 18, 15, 30, 0, DateTimeKind.Utc);
        _sync.Setup(s => s.SincronizarAhoraAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new ResumenSincronizacionMal(fin, 5, 2, true));
        var sut = CrearSut(conectado: true);

        await sut.SincronizarMalCommand.ExecuteAsync(null);

        sut.MalUltimaPasadaTexto.Should().Be(string.Format(LocalizationService.T("Cfg_MalUltimaFormato"),
            fin.ToLocalTime().ToString("t", LocalizationService.Cultura), 2));
    }
}
