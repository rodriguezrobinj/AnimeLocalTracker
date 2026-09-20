using System.Collections.Generic;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

public class ConfiguracionSeccionesTests
{
    private static ConfiguracionViewModel CrearSut()
    {
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.ObtenerConfiguracion()).Returns(new AppSettings());
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
}
