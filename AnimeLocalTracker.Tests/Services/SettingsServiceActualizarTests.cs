using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

public class SettingsServiceActualizarTests
{
    [Fact]
    public async Task ActualizarAsync_AplicaElCambioYGuardaLosAjustes()
    {
        var config = new AppSettings { VolumenMusica = 1 };
        var ajustes = new Mock<ISettingsService>();
        ajustes.Setup(s => s.ObtenerConfiguracion()).Returns(config);

        await ajustes.Object.ActualizarAsync(c => c.VolumenMusica = 0.3);

        config.VolumenMusica.Should().Be(0.3);
        ajustes.Verify(s => s.GuardarConfiguracionAsync(config), Times.Once);
    }

    [Fact]
    public async Task ActualizarAsync_SinAjustesCargados_NoGuardaNada()
    {
        var ajustes = new Mock<ISettingsService>();
        ajustes.Setup(s => s.ObtenerConfiguracion()).Returns((AppSettings)null!);

        await ajustes.Object.ActualizarAsync(c => c.VolumenMusica = 0.3);

        ajustes.Verify(s => s.GuardarConfiguracionAsync(It.IsAny<AppSettings>()), Times.Never);
    }
}
