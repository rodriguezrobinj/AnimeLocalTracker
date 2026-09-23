using System.Collections.Generic;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

public class ConfiguracionSeccionesTests
{
    private static ConfiguracionViewModel CrearSut(AppSettings? config = null)
    {
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.ObtenerConfiguracion()).Returns(config ?? new AppSettings());
        var db = Mock.Of<IDatabaseService>();

        return new ConfiguracionViewModel(
            settings.Object,
            Mock.Of<IAuthService>(),
            db,
            Mock.Of<IDialogService>(),
            new CacheMaintenanceService(db),
            Mock.Of<IPluginService>());
    }

    [Fact]
    public void AlAbrir_DeberiaMostrarBibliotecaYSinBarraDeGuardado()
    {
        var sut = CrearSut();

        sut.SeccionActiva.Should().Be(SeccionConfiguracion.Biblioteca);
        sut.EsSeccionBiblioteca.Should().BeTrue();
        sut.EsSeccionReproduccion.Should().BeFalse();
        sut.MostrarBarraGuardar.Should().BeFalse("la carpeta y las copias actúan al instante con sus propios botones");
    }

    [Theory]
    [InlineData(SeccionConfiguracion.Reproduccion, true)]
    [InlineData(SeccionConfiguracion.Atajos, true)]
    [InlineData(SeccionConfiguracion.Descargas, true)]
    [InlineData(SeccionConfiguracion.General, true)]
    [InlineData(SeccionConfiguracion.Biblioteca, false)]
    [InlineData(SeccionConfiguracion.Plugins, false)]
    public void SeleccionarSeccion_DeberiaMostrarLaBarraDeGuardadoSoloEnCategoriasConPreferencias(SeccionConfiguracion seccion, bool esperaBarra)
    {
        var sut = CrearSut();

        sut.SeleccionarSeccionCommand.Execute(seccion);

        sut.SeccionActiva.Should().Be(seccion);
        sut.MostrarBarraGuardar.Should().Be(esperaBarra);
    }

    [Fact]
    public void SeleccionarSeccion_DeberiaActivarUnaSolaCategoriaYNotificarATodas()
    {
        var sut = CrearSut();
        var cambiadas = new List<string?>();
        sut.PropertyChanged += (_, e) => cambiadas.Add(e.PropertyName);

        sut.SeleccionarSeccionCommand.Execute(SeccionConfiguracion.Atajos);

        new[] { sut.EsSeccionBiblioteca, sut.EsSeccionReproduccion, sut.EsSeccionAtajos, sut.EsSeccionDescargas, sut.EsSeccionGeneral, sut.EsSeccionPlugins }
            .Should().Equal(false, false, true, false, false, false);
        // Los botones de la barra lateral se enlazan a estos flags: si no se notifican, el resaltado no se mueve.
        cambiadas.Should().Contain(new[]
        {
            nameof(ConfiguracionViewModel.EsSeccionBiblioteca),
            nameof(ConfiguracionViewModel.EsSeccionAtajos),
            nameof(ConfiguracionViewModel.MostrarBarraGuardar)
        });
    }

    [Fact]
    public void AlAbrir_DeberiaCargarNotificarConBandejaSiempreDesdeLaConfiguracion()
    {
        var sut = CrearSut(new AppSettings { NotificarConBandejaSiempre = true });

        sut.NotificarConBandejaSiempre.Should().BeTrue();
    }

    [Fact]
    public async Task GuardarPreferenciasAsync_DeberiaPersistirNotificarConBandejaSiempre()
    {
        var settings = new Mock<ISettingsService>();
        var config = new AppSettings();
        settings.Setup(s => s.ObtenerConfiguracion()).Returns(config);
        var db = Mock.Of<IDatabaseService>();
        var sut = new ConfiguracionViewModel(
            settings.Object, Mock.Of<IAuthService>(), db, Mock.Of<IDialogService>(),
            new CacheMaintenanceService(db), Mock.Of<IPluginService>())
        {
            NotificarConBandejaSiempre = true
        };

        await sut.GuardarPreferenciasAsync();

        config.NotificarConBandejaSiempre.Should().BeTrue();
        settings.Verify(s => s.GuardarConfiguracionAsync(config), Times.Once);
    }

    [Fact]
    public void AlAbrir_ConSettingsJsonAntiguoSinAccionFinEpisodio_DeberiaCaerEnCuentaAtrasPorDefecto()
    {
        // AppSettings.AccionFinEpisodio ya trae ese default de fábrica, pero esto cubre el caso real:
        // un settings.json existente que un usuario ya tenía antes de que este ajuste existiera.
        var sut = CrearSut(new AppSettings { AccionFinEpisodio = null! });

        sut.AccionFinEpisodio.Should().Be(AccionFinEpisodioValores.AutoPlayCuentaAtras);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(30)]
    [InlineData(60)]
    public void AlAbrir_DeberiaCargarPasosSaltoSegundosDesdeLaConfiguracion(int pasos)
    {
        var sut = CrearSut(new AppSettings { PasosSaltoSegundos = pasos });

        sut.PasosSaltoSegundos.Should().Be(pasos);
    }

    [Fact]
    public void AlAbrir_ConPasosSaltoSegundosInvalido_DeberiaCaerEnDiezPorDefecto()
    {
        // Un settings.json corrupto/editado a mano no debe dejar un valor sin sentido en el ComboBox
        var sut = CrearSut(new AppSettings { PasosSaltoSegundos = 7 });

        sut.PasosSaltoSegundos.Should().Be(10);
    }

    [Fact]
    public async Task GuardarPreferenciasAsync_DeberiaPersistirLosNuevosAjustesDeReproduccionYGeneral()
    {
        var settings = new Mock<ISettingsService>();
        var config = new AppSettings();
        settings.Setup(s => s.ObtenerConfiguracion()).Returns(config);
        var db = Mock.Of<IDatabaseService>();
        var startupService = new Mock<IStartupService>();
        var sut = new ConfiguracionViewModel(
            settings.Object, Mock.Of<IAuthService>(), db, Mock.Of<IDialogService>(),
            new CacheMaintenanceService(db), Mock.Of<IPluginService>(), startupService.Object)
        {
            PasosSaltoSegundos = 30,
            EvitarSuspensionPantalla = false,
            AccionFinEpisodio = AccionFinEpisodioValores.PausarYSalirFicha,
            TeclaPanicoActiva = true,
            TeclaPanico = "Escape",
            IniciarConWindows = true
        };

        await sut.GuardarPreferenciasAsync();

        config.PasosSaltoSegundos.Should().Be(30);
        config.EvitarSuspensionPantalla.Should().BeFalse();
        config.AccionFinEpisodio.Should().Be(AccionFinEpisodioValores.PausarYSalirFicha);
        config.TeclaPanicoActiva.Should().BeTrue();
        config.TeclaPanico.Should().Be("Escape");
        // El registro de Windows es la fuente de verdad, no un campo de AppSettings.
        startupService.Verify(s => s.Sincronizar(true), Times.Once);
    }

    [Fact]
    public void AlAbrir_DeberiaLeerIniciarConWindowsDesdeElStartupServiceNoDesdeAppSettings()
    {
        // El registro de Windows puede haber sido desactivado desde fuera de la app (p. ej. el
        // Administrador de tareas de Windows): la fuente de verdad debe ser el propio registro.
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.ObtenerConfiguracion()).Returns(new AppSettings());
        var db = Mock.Of<IDatabaseService>();
        var startupService = new Mock<IStartupService>();
        startupService.Setup(s => s.EstaHabilitado()).Returns(true);

        var sut = new ConfiguracionViewModel(
            settings.Object, Mock.Of<IAuthService>(), db, Mock.Of<IDialogService>(),
            new CacheMaintenanceService(db), Mock.Of<IPluginService>(), startupService.Object);

        sut.IniciarConWindows.Should().BeTrue();
    }
}
