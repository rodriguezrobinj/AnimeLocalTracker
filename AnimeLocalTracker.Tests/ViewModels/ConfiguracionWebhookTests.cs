using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Webhook;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>Configuración → General → "Avisos en un canal de Discord": URL del webhook (cifrada), Probar, Quitar y los dos avisos.</summary>
public class ConfiguracionWebhookTests
{
    private const string Url = "https://discord.com/api/webhooks/123/secreto";
    private readonly Mock<IDialogService> _dialogos = new();
    private readonly Mock<IWebhookDiscordService> _webhook = new();
    private readonly Mock<ISettingsService> _settings = new();
    private readonly AppSettings _config = new() { WebhookAvisarEmision = true, WebhookAvisarSerieTerminada = false };

    private ConfiguracionViewModel CrearSut(string? urlGuardada = null)
    {
        _webhook.SetupGet(w => w.UrlGuardada).Returns(urlGuardada);
        _settings.Setup(s => s.ObtenerConfiguracion()).Returns(_config);
        _settings.Setup(s => s.GuardarConfiguracionAsync(It.IsAny<AppSettings>())).Returns(Task.CompletedTask);
        var db = Mock.Of<IDatabaseService>();
        return new ConfiguracionViewModel(_settings.Object, Mock.Of<IAuthService>(), db, _dialogos.Object,
            new CacheMaintenanceService(db), Mock.Of<IPluginService>(), webhook: _webhook.Object);
    }

    [Fact]
    public void AlAbrir_MuestraLosAvisosYSiHayUrlGuardadaSinEnseñarla()
    {
        var sut = CrearSut(urlGuardada: Url);

        sut.WebhookAvisarEmision.Should().BeTrue();
        sut.WebhookAvisarSerieTerminada.Should().BeFalse();
        sut.HayWebhookGuardado.Should().BeTrue();
        sut.UrlWebhook.Should().BeEmpty("la URL es secreta: no se vuelve a mostrar");
    }

    [Fact]
    public async Task Guardar_ConUrlValida_LaGuardaCifradaYLimpiaElCampo()
    {
        var sut = CrearSut();
        sut.UrlWebhook = "  " + Url + " ";
        sut.WebhookAvisarSerieTerminada = true;

        await sut.GuardarPreferenciasAsync();

        _webhook.Verify(w => w.GuardarUrl(Url), Times.Once);
        sut.UrlWebhook.Should().BeEmpty();
        sut.HayWebhookGuardado.Should().BeTrue();
        _config.WebhookAvisarSerieTerminada.Should().BeTrue();
    }

    [Fact]
    public async Task Guardar_ConUrlInvalida_AvisaYNoGuardaNada()
    {
        var sut = CrearSut();
        sut.UrlWebhook = "https://evil.net/api/webhooks/1/x";

        await sut.GuardarPreferenciasAsync();

        _webhook.Verify(w => w.GuardarUrl(It.IsAny<string?>()), Times.Never);
        _settings.Verify(s => s.GuardarConfiguracionAsync(It.IsAny<AppSettings>()), Times.Never);
        _dialogos.Verify(d => d.MostrarDialogoAsync(LocalizationService.T("Cfg_WebhookInvalidoTitulo"), It.IsAny<string>(), false, It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task Guardar_SinUrlEscrita_ConservaLaGuardada()
    {
        var sut = CrearSut(urlGuardada: Url);

        await sut.GuardarPreferenciasAsync();

        _webhook.Verify(w => w.GuardarUrl(It.IsAny<string?>()), Times.Never);
        sut.HayWebhookGuardado.Should().BeTrue();
    }

    [Fact]
    public async Task Probar_SinUrlEscrita_UsaLaGuardada()
    {
        _webhook.Setup(w => w.ProbarAsync(Url)).ReturnsAsync(true);
        var sut = CrearSut(urlGuardada: Url);

        await sut.ProbarWebhookCommand.ExecuteAsync(null);

        _webhook.Verify(w => w.ProbarAsync(Url), Times.Once);
        _dialogos.Verify(d => d.MostrarDialogoAsync(LocalizationService.T("Cfg_WebhookPruebaOkTitulo"), It.IsAny<string>(), false, It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task Probar_ConUrlEscritaQueFalla_AvisaDelError()
    {
        const string otra = "https://discord.com/api/webhooks/999/otra";
        _webhook.Setup(w => w.ProbarAsync(otra)).ReturnsAsync(false);
        var sut = CrearSut(urlGuardada: Url);
        sut.UrlWebhook = otra;

        await sut.ProbarWebhookCommand.ExecuteAsync(null);

        _webhook.Verify(w => w.ProbarAsync(otra), Times.Once);
        _dialogos.Verify(d => d.MostrarDialogoAsync(LocalizationService.T("Cfg_WebhookPruebaErrorTitulo"), It.IsAny<string>(), false, It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public void Quitar_BorraLaUrlGuardada()
    {
        var sut = CrearSut(urlGuardada: Url);

        sut.QuitarWebhookCommand.Execute(null);

        _webhook.Verify(w => w.GuardarUrl(null), Times.Once);
        sut.HayWebhookGuardado.Should().BeFalse();
    }
}
