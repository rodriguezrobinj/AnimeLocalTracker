using System;
using System.IO;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

public class SettingsServiceTests : IDisposable
{
    private readonly string _tempSettingsDir;
    private readonly string _tempSettingsFile;

    public SettingsServiceTests()
    {
        _tempSettingsDir = Path.Combine(Path.GetTempPath(), "AnimeLocalTracker_TestSettings_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempSettingsDir);
        _tempSettingsFile = Path.Combine(_tempSettingsDir, "settings.json");
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            if (Directory.Exists(_tempSettingsDir))
            {
                Directory.Delete(_tempSettingsDir, true);
            }
        }
        catch { }
    }

    [Fact]
    public void SettingsService_Inicializacion_SinArchivoPrevio_DeberiaCrearValoresPorDefecto()
    {
        // Act
        var sut = new SettingsService(_tempSettingsFile);
        var config = sut.ObtenerConfiguracion();

        // Assert
        config.Should().NotBeNull();
        config.RutaBaseAnimes.Should().NotBeNullOrWhiteSpace();
        config.AutoPlaySiguiente.Should().BeTrue();
        config.SubtitulosPorDefecto.Should().BeTrue();
        config.DescargasSimultaneas.Should().Be(3);
    }

    [Fact]
    public async Task SettingsService_EstablecerRutaBaseAnimes_DeberiaActualizarYPersistirEnDisco()
    {
        // Arrange
        var sut = new SettingsService(_tempSettingsFile);
        string nuevaRuta = Path.Combine(_tempSettingsDir, "MisAnimesCustom");

        // Act
        await sut.EstablecerRutaBaseAnimesAsync(nuevaRuta);

        // Assert
        sut.ObtenerRutaBaseAnimes().Should().Be(nuevaRuta);
        Directory.Exists(nuevaRuta).Should().BeTrue();

        // Reinstanciar para validar persistencia en JSON
        var sutRecargado = new SettingsService(_tempSettingsFile);
        sutRecargado.ObtenerRutaBaseAnimes().Should().Be(nuevaRuta);
    }

    [Fact]
    public async Task SettingsService_GuardarConfiguracion_DeberiaDispararEventoConfiguracionModificada()
    {
        // Arrange
        var sut = new SettingsService(_tempSettingsFile);
        bool eventoDisparado = false;
        sut.ConfiguracionModificada += _ => eventoDisparado = true;

        var nuevosAjustes = new AppSettings
        {
            RutaBaseAnimes = _tempSettingsDir,
            AutoPlaySiguiente = false,
            DescargasSimultaneas = 5
        };

        // Act
        await sut.GuardarConfiguracionAsync(nuevosAjustes);

        // Assert
        eventoDisparado.Should().BeTrue();
        sut.ObtenerConfiguracion().AutoPlaySiguiente.Should().BeFalse();
        sut.ObtenerConfiguracion().DescargasSimultaneas.Should().Be(5);
    }

    [Fact]
    public void SettingsService_AtajosInvalidosOSistema_DeberiaSanitizarConDefaults()
    {
        // Arrange (ATA-01): settings.json editado a mano con tecla de sistema, inválida y acción desconocida
        File.WriteAllText(_tempSettingsFile, """
        {
          "RutaBaseAnimes": "C:\\Anime",
          "Atajos": {
            "PlayPausa": "LWin",
            "Silenciar": "9999",
            "AccionInexistente": "Space",
            "Adelantar10": "Left"
          }
        }
        """);

        // Act
        var sut = new SettingsService(_tempSettingsFile);
        var atajos = sut.ObtenerConfiguracion().Atajos;

        // Assert: inválidos/de sistema vuelven a su default; la acción desconocida se ignora
        atajos["PlayPausa"].Should().Be("Space", "LWin es una tecla de sistema y debe degradarse al default");
        atajos["Silenciar"].Should().Be("M", "9999 no es una tecla real");
        atajos.Should().NotContainKey("AccionInexistente");
        atajos["Adelantar10"].Should().Be("Left", "una tecla válida y sin conflicto se conserva");
        atajos["Cerrar"].Should().Be("Escape", "las acciones ausentes se completan con su default");
    }

    [Fact]
    public void SettingsService_AtajosDuplicadosEntreAcciones_DeberiaConservarElPrimero()
    {
        // Arrange (ATA-01): dos acciones con la misma tecla → la primera gana y la segunda degrada
        File.WriteAllText(_tempSettingsFile, """
        {
          "RutaBaseAnimes": "C:\\Anime",
          "Atajos": {
            "PlayPausa": "Space",
            "Silenciar": "Space"
          }
        }
        """);

        // Act
        var sut = new SettingsService(_tempSettingsFile);
        var atajos = sut.ObtenerConfiguracion().Atajos;

        // Assert
        atajos["PlayPausa"].Should().Be("Space", "la primera ocurrencia gana");
        atajos["Silenciar"].Should().Be("M", "la segunda acción vuelve a su default");
    }

    [Fact]
    public void SettingsService_AtajosNulos_DeberiaRestaurarLosDefaults()
    {
        // Arrange: "Atajos": null en el JSON
        File.WriteAllText(_tempSettingsFile, """
        {
          "RutaBaseAnimes": "C:\\Anime",
          "Atajos": null
        }
        """);

        // Act
        var sut = new SettingsService(_tempSettingsFile);
        var atajos = sut.ObtenerConfiguracion().Atajos;

        // Assert
        atajos.Should().NotBeNull();
        atajos["PlayPausa"].Should().Be("Space");
        atajos["CapturarFrame"].Should().Be("C");
    }

    [Fact]
    public void ConfiguracionViewModel_CargarDatos_DeberiaReflejarAjustesDeSettingsService()
    {
        // Arrange
        var settingsMock = new Mock<ISettingsService>();
        var authMock = new Mock<IAuthService>();
        var dbMock = new Mock<IDatabaseService>();
        var dialogMock = new Mock<IDialogService>();

        settingsMock.Setup(s => s.ObtenerConfiguracion()).Returns(new AppSettings
        {
            RutaBaseAnimes = @"D:\AnimesTest",
            AutoPlaySiguiente = true,
            AutoSkipIntroOutro = true,
            SubtitulosPorDefecto = false,
            DescargasSimultaneas = 2
        });

        authMock.Setup(a => a.EstaAutenticado()).Returns(true);

        // Act
        var vm = new ConfiguracionViewModel(
            settingsMock.Object, 
            authMock.Object, 
            dbMock.Object, 
            dialogMock.Object,
            new CacheMaintenanceService(dbMock.Object));

        // Assert
        vm.RutaBaseAnimes.Should().Be(@"D:\AnimesTest");
        vm.AutoPlaySiguiente.Should().BeTrue();
        vm.AutoSkipIntroOutro.Should().BeTrue();
        vm.SubtitulosPorDefecto.Should().BeFalse();
        vm.DescargasSimultaneas.Should().Be(2);
        vm.EstaAutenticadoAniList.Should().BeTrue();
    }
}
