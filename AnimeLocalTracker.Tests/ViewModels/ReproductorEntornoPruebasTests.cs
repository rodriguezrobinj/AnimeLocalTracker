using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>
/// Regresión: la app creía estar "bajo pruebas" (y no creaba el reproductor de video) cuando cualquier
/// ensamblado cargado contenía "test" en el nombre — p. ej. un plugin C# llamado "plugin_test".
/// </summary>
public class ReproductorEntornoPruebasTests
{
    private static readonly string[] EnsambladosNormales =
        { "System.Private.CoreLib", "AnimeLocalTracker", "FlyleafLib", "PresentationFramework" };

    [Fact]
    public void AppNormal_NoEsEntornoDePruebas()
    {
        ReproductorViewModel.EsEntornoPruebas("AnimeLocalTracker", @"C:\Program Files\AnimeLocalTracker\AnimeLocalTracker.exe", EnsambladosNormales)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("plugin_test")]
    [InlineData("PluginProveedorPrueba_Test")]
    [InlineData("Contest.Helpers")]
    public void PluginConTestEnElNombre_NoSeConfundeConEntornoDePruebas(string ensamblado)
    {
        var ensamblados = new[] { "AnimeLocalTracker", ensamblado };

        ReproductorViewModel.EsEntornoPruebas("AnimeLocalTracker", @"C:\Apps\AnimeLocalTracker.exe", ensamblados)
            .Should().BeFalse();
    }

    [Fact]
    public void RutaDeInstalacionConTest_NoSeConfundeConEntornoDePruebas()
    {
        ReproductorViewModel.EsEntornoPruebas("AnimeLocalTracker", @"C:\Users\test\Apps\AnimeLocalTracker.exe", EnsambladosNormales)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("testhost", "testhost.exe")]
    [InlineData("dotnet", "dotnet vstest.console.dll AnimeLocalTracker.Tests.dll")]
    public void EjecutorDePruebas_SiEsEntornoDePruebas(string proceso, string lineaComandos)
    {
        ReproductorViewModel.EsEntornoPruebas(proceso, lineaComandos, EnsambladosNormales).Should().BeTrue();
    }

    [Theory]
    [InlineData("xunit.core")]
    [InlineData("testhost")]
    [InlineData("Microsoft.TestPlatform.CoreUtilities")]
    public void MarcadoresDelEjecutorEnEnsamblados_SiEsEntornoDePruebas(string ensamblado)
    {
        ReproductorViewModel.EsEntornoPruebas("AnimeLocalTracker", "AnimeLocalTracker.exe", new[] { "AnimeLocalTracker", ensamblado })
            .Should().BeTrue();
    }
}
