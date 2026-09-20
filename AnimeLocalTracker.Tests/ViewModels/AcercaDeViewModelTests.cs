using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Velopack;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

public class AcercaDeViewModelTests
{
    private readonly Mock<IUpdateService> _updateMock = new();
    private readonly Mock<IDialogService> _dialogMock = new();

    private AcercaDeViewModel CrearSut()
    {
        _updateMock.Setup(u => u.ObtenerVersionActual()).Returns("1.0.5");
        _updateMock
            .Setup(u => u.ObtenerInfoUltimaVersionAsync(It.IsAny<bool>()))
            .ReturnsAsync(new ReleaseInfo { Titulo = "AnimeLocalTracker", NotasVersion = "notas" });
        return new AcercaDeViewModel(_updateMock.Object, _dialogMock.Object);
    }

    [Fact]
    public async Task BuscarActualizaciones_SinNuevaVersion_NoDeberiaMostrarUnSegundoAvisoEncimaDelDelServicio()
    {
        // Arrange: el servicio ya avisa por su cuenta (modo desarrollo / al día / error de red) y devuelve null.
        // Antes, la vista mostraba además "Aplicación actualizada", pisando el aviso de modo desarrollo.
        var sut = CrearSut();
        _updateMock.Setup(u => u.ComprobarActualizacionesAsync(true)).ReturnsAsync((UpdateInfo?)null);

        // Act
        await sut.BuscarActualizacionesAsync();

        // Assert
        _dialogMock.Verify(d => d.MostrarDialogoAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData("1.0.5", "v1.0.5")]   // instalación Velopack: sin prefijo
    [InlineData("v1.0.5", "v1.0.5")]  // modo desarrollo: ya con prefijo (antes se veía "vv1.0.5")
    public void VersionAppTexto_DeberiaTenerSiempreUnaSolaV(string devuelta, string esperada)
    {
        var sut = CrearSut();
        _updateMock.Setup(u => u.ObtenerVersionActual()).Returns(devuelta);

        sut.VersionAppTexto.Should().Be(esperada);
    }
}
