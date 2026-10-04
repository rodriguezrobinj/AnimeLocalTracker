using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>"Borrar todos mis datos": qué se hace y en qué casos. El borrado real de la carpeta se sustituye: nunca toca datos del usuario.</summary>
public class ConfiguracionBorrarDatosTests
{
    private readonly Mock<ISettingsService> _settings = new();
    private readonly Mock<IAuthService> _auth = new();
    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<IDialogService> _dialogo = new();
    private readonly Mock<IStartupService> _arranque = new();
    private int _borradosTotales;

    private ConfiguracionViewModel CrearSut(bool confirmaPrimero, bool confirmaSegundo)
    {
        _settings.Setup(s => s.ObtenerConfiguracion()).Returns(new AppSettings());
        _db.Setup(d => d.VaciarBibliotecaAsync()).Returns(Task.CompletedTask);
        _dialogo.SetupSequence(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(confirmaPrimero)
            .ReturnsAsync(confirmaSegundo)
            .ReturnsAsync(true);

        var vm = new ConfiguracionViewModel(_settings.Object, _auth.Object, _db.Object, _dialogo.Object,
            new CacheMaintenanceService(_db.Object), Mock.Of<IPluginService>(), _arranque.Object);
        vm.CerrarAppYBorrarDatos = () => _borradosTotales++;
        return vm;
    }

    [Fact]
    public async Task ConLasDosConfirmaciones_CierraSesionVaciaLaBibliotecaQuitaElArranqueYBorraLaCarpetaDeDatos()
    {
        var vm = CrearSut(confirmaPrimero: true, confirmaSegundo: true);

        await vm.BorrarTodosMisDatosAsync();

        _auth.Verify(a => a.CerrarSesion(), Times.Once);
        _db.Verify(d => d.VaciarBibliotecaAsync(), Times.Once);
        _arranque.Verify(a => a.Sincronizar(false), Times.Once);
        _borradosTotales.Should().Be(1);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task SinLasDosConfirmaciones_NoSeBorraNada(bool confirmaPrimero, bool confirmaSegundo)
    {
        var vm = CrearSut(confirmaPrimero, confirmaSegundo);

        await vm.BorrarTodosMisDatosAsync();

        _auth.Verify(a => a.CerrarSesion(), Times.Never);
        _db.Verify(d => d.VaciarBibliotecaAsync(), Times.Never);
        _arranque.Verify(a => a.Sincronizar(It.IsAny<bool>()), Times.Never);
        _borradosTotales.Should().Be(0);
    }

    [Fact]
    public async Task SiVaciarLaBibliotecaFalla_NoSeBorraLaCarpetaNiSeCierraLaApp()
    {
        var vm = CrearSut(confirmaPrimero: true, confirmaSegundo: true);
        _db.Setup(d => d.VaciarBibliotecaAsync()).ThrowsAsync(new System.IO.IOException("base de datos en uso"));

        await vm.BorrarTodosMisDatosAsync();

        _borradosTotales.Should().Be(0, "el usuario ve el error y decide: no se cierra la app a medias");
    }
}
