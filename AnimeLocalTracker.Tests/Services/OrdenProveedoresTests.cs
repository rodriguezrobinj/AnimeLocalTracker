using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// El usuario elige en Configuración en qué orden se prueban los sitios (AnimeAV1, JKAnime). Se guarda como lista separada por comas
/// y el orquestador la lee en cada descarga (un cambio se nota al instante, sin reiniciar la app).
/// </summary>
public class OrdenProveedoresTests
{
    // === El ajuste ===

    [Fact]
    public void OrdenPredeterminado_EsAnimeAv1YDespuesJkAnime() =>
        OrdenProveedores.Predeterminado.Should().Equal("AnimeAV1", "JKAnime");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void DesdeAjuste_SinNada_DaElOrdenPredeterminado(string? ajuste) =>
        OrdenProveedores.DesdeAjuste(ajuste).Should().Equal("AnimeAV1", "JKAnime");

    [Fact]
    public void DesdeAjuste_IgnoraDesconocidosRepetidosYMayusculas() =>
        OrdenProveedores.DesdeAjuste("jkanime, Raro,JKAnime").Should().Equal("JKAnime", "AnimeAV1");

    [Fact]
    public void ParaAjuste_ElOrdenPredeterminadoSeGuardaComoNulo() =>
        OrdenProveedores.ParaAjuste(["AnimeAV1", "JKAnime"]).Should().BeNull();

    [Fact]
    public void ParaAjuste_OtroOrdenSeGuardaSeparadoPorComas() =>
        OrdenProveedores.ParaAjuste(["JKAnime", "AnimeAV1"]).Should().Be("JKAnime,AnimeAV1");

    // === El orquestador ===

    private static Mock<IProveedorVideo> Proveedor(string nombre, List<string> llamadas, string? resultado = null)
    {
        var mock = new Mock<IProveedorVideo>();
        mock.SetupGet(p => p.Nombre).Returns(nombre);
        mock.Setup(p => p.BuscarUrlEpisodioAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback(() => llamadas.Add(nombre))
            .ReturnsAsync(resultado);
        return mock;
    }

    private static Task<string?> Buscar(OrquestadorMultiProveedor orquestador) =>
        orquestador.BuscarUrlEpisodioAsync(["Anime"], 1);

    [Fact]
    public async Task SinOrdenDelUsuario_SeProbanEnElOrdenDeRegistro()
    {
        var llamadas = new List<string>();
        var orquestador = new OrquestadorMultiProveedor([Proveedor("AnimeAV1", llamadas).Object, Proveedor("JKAnime", llamadas).Object, Proveedor("MiPlugin", llamadas).Object]);

        await Buscar(orquestador);

        llamadas.Should().Equal("AnimeAV1", "JKAnime", "MiPlugin");
    }

    [Fact]
    public async Task ConOrdenDelUsuario_LosListadosVanPrimeroYElRestoDespuesEnSuOrden()
    {
        var llamadas = new List<string>();
        var orquestador = new OrquestadorMultiProveedor(
            [Proveedor("AnimeAV1", llamadas).Object, Proveedor("JKAnime", llamadas).Object, Proveedor("MiPlugin", llamadas).Object],
            ordenProveedores: () => "JKAnime");

        await Buscar(orquestador);

        llamadas.Should().Equal("JKAnime", "AnimeAV1", "MiPlugin");
    }

    [Fact]
    public async Task ElOrdenSeLeeEnCadaBusqueda_UnCambioSeAplicaSinReiniciar()
    {
        var llamadas = new List<string>();
        string? orden = null;
        var orquestador = new OrquestadorMultiProveedor([Proveedor("AnimeAV1", llamadas).Object, Proveedor("JKAnime", llamadas).Object], ordenProveedores: () => orden);

        await Buscar(orquestador);
        orden = "JKAnime,AnimeAV1";
        await Buscar(orquestador);

        llamadas.Should().Equal("AnimeAV1", "JKAnime", "JKAnime", "AnimeAV1");
    }

    [Fact]
    public async Task SiElPrimeroResuelve_ElSegundoNoSeToca()
    {
        var llamadas = new List<string>();
        var orquestador = new OrquestadorMultiProveedor(
            [Proveedor("AnimeAV1", llamadas).Object, Proveedor("JKAnime", llamadas, resultado: "https://cdn.example.com/video.mp4").Object],
            ordenProveedores: () => "JKAnime");

        (await Buscar(orquestador)).Should().Be("https://cdn.example.com/video.mp4");
        llamadas.Should().Equal("JKAnime");
    }

    [Fact]
    public async Task SiAnimeAv1NoTieneElEpisodio_JkAnimeLoIntenta()
    {
        var llamadas = new List<string>();
        var orquestador = new OrquestadorMultiProveedor([Proveedor("AnimeAV1", llamadas).Object, Proveedor("JKAnime", llamadas, resultado: "https://cdn.example.com/jk.mp4").Object]);

        (await Buscar(orquestador)).Should().Be("https://cdn.example.com/jk.mp4");
        llamadas.Should().Equal("AnimeAV1", "JKAnime");
    }

    [Fact]
    public async Task UnProveedorEnPausaPorFallosSeSaltaAunqueVayaPrimeroEnElOrden()
    {
        var llamadas = new List<string>();
        var roto = new Mock<IProveedorVideo>();
        roto.SetupGet(p => p.Nombre).Returns("JKAnime");
        roto.Setup(p => p.BuscarUrlEpisodioAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback(() => llamadas.Add("JKAnime")).ThrowsAsync(new InvalidOperationException("caído"));
        var orquestador = new OrquestadorMultiProveedor([Proveedor("AnimeAV1", llamadas).Object, roto.Object], maxFallosConsecutivos: 1, ordenProveedores: () => "JKAnime");

        await Buscar(orquestador); // JKAnime falla y entra en pausa
        llamadas.Clear();
        await Buscar(orquestador);

        llamadas.Should().Equal("AnimeAV1");
    }

    // === La pantalla de Configuración ===

    private static (ConfiguracionViewModel Vm, AppSettings Config) Crear(string? ajuste)
    {
        var config = new AppSettings { OrdenProveedoresVideo = ajuste };
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.ObtenerConfiguracion()).Returns(config);
        var db = Mock.Of<IDatabaseService>();
        var vm = new ConfiguracionViewModel(settings.Object, Mock.Of<IAuthService>(), db, Mock.Of<IDialogService>(),
            new CacheMaintenanceService(db), Mock.Of<IPluginService>());
        return (vm, config);
    }

    [Fact]
    public void Configuracion_MuestraElOrdenGuardadoDeLosSitios()
    {
        var (vm, _) = Crear("JKAnime");

        vm.OrdenProveedores.Select(s => s.Nombre).Should().Equal("JKAnime", "AnimeAV1");
    }

    [Fact]
    public void Configuracion_SubirYBajarUnSitio_CambiaElOrdenYLosBotones()
    {
        var (vm, _) = Crear(null);

        vm.OrdenProveedores[1].SubirCommand.Execute(null);

        vm.OrdenProveedores.Select(s => s.Nombre).Should().Equal("JKAnime", "AnimeAV1");
        vm.OrdenProveedores[0].PuedeSubir.Should().BeFalse();
        vm.OrdenProveedores[1].PuedeBajar.Should().BeFalse();
        vm.OrdenProveedores.Select(s => s.Posicion).Should().Equal(1, 2);
    }

    [Fact]
    public async Task Configuracion_GuardaElOrdenDeSitios_YElPredeterminadoComoNulo()
    {
        var (vm, config) = Crear(null);
        vm.OrdenProveedores[0].BajarCommand.Execute(null);
        await vm.GuardarPreferenciasAsync();
        config.OrdenProveedoresVideo.Should().Be("JKAnime,AnimeAV1");

        vm.RestablecerOrdenProveedoresCommand.Execute(null);
        await vm.GuardarPreferenciasAsync();
        config.OrdenProveedoresVideo.Should().BeNull();
    }
}
