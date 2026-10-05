using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>Configuración → Reproductor → a qué se aplican los ajustes de sonido (todo / cada anime / cada capítulo).</summary>
public class ConfiguracionAmbitoAudioTests
{
    private static ConfiguracionViewModel CrearSut(AppSettings config)
    {
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.ObtenerConfiguracion()).Returns(config);
        settings.Setup(s => s.GuardarConfiguracionAsync(It.IsAny<AppSettings>())).Returns(Task.CompletedTask);
        var db = Mock.Of<IDatabaseService>();
        return new ConfiguracionViewModel(settings.Object, Mock.Of<IAuthService>(), db, Mock.Of<IDialogService>(),
            new CacheMaintenanceService(db), Mock.Of<IPluginService>());
    }

    [Fact]
    public void AlAbrir_MuestraLoGuardado_YSiEsUnValorRaroElGlobal()
    {
        CrearSut(new AppSettings { AmbitoAjustesAudio = AmbitoAudio.PorCapitulo }).AmbitoAjustesAudio.Should().Be(AmbitoAudio.PorCapitulo);
        CrearSut(new AppSettings { AmbitoAjustesAudio = "otra cosa" }).AmbitoAjustesAudio.Should().Be(AmbitoAudio.Global);
    }

    [Fact]
    public async Task Guardar_EscribeLaOpcionElegida()
    {
        var config = new AppSettings();
        var vm = CrearSut(config);

        vm.AmbitoAjustesAudio = AmbitoAudio.PorAnime;
        await vm.GuardarPreferenciasAsync();

        config.AmbitoAjustesAudio.Should().Be(AmbitoAudio.PorAnime);
    }
}
