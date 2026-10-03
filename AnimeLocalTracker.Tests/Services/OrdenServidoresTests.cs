using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// El usuario elige en Configuración el orden en que se prueban los servidores al descargar. Se guarda en el mismo ajuste de
/// siempre (<see cref="AppSettings.ServidorPreferidoAnimeAv1"/>) como lista separada por comas: un valor antiguo como "Voe"
/// sigue significando "Voe primero y luego el resto".
/// </summary>
public class OrdenServidoresTests
{
    private static readonly string[] Predeterminado = ["MP4Upload", "TransferIt", "Mega", "Mediafire", "Vidhide", "Voe", "Streamwish"];

    // === El ajuste ===

    [Fact]
    public void OrdenPredeterminado_PoneLosDe1080pAntesQueVoe() =>
        OrdenServidores.Predeterminado.Should().Equal(Predeterminado);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DesdeAjuste_SinNada_DaElOrdenPredeterminado(string? ajuste) =>
        OrdenServidores.DesdeAjuste(ajuste).Should().Equal(Predeterminado);

    [Fact]
    public void DesdeAjuste_ConUnValorAntiguo_PoneEseServidorPrimeroYElRestoEnSuOrden() =>
        OrdenServidores.DesdeAjuste("Voe").Should().Equal("Voe", "MP4Upload", "TransferIt", "Mega", "Mediafire", "Vidhide", "Streamwish");

    [Fact]
    public void DesdeAjuste_ConUnOrdenParcial_CompletaConElRestoEnElOrdenPredeterminado() =>
        OrdenServidores.DesdeAjuste("Mega,TransferIt").Should().Equal("Mega", "TransferIt", "MP4Upload", "Mediafire", "Vidhide", "Voe", "Streamwish");

    [Fact]
    public void DesdeAjuste_IgnoraDuplicadosNombresDesconocidosYMayusculas() =>
        OrdenServidores.DesdeAjuste("Voe, Voe,Raro, mega ").Should().Equal("Voe", "Mega", "MP4Upload", "TransferIt", "Mediafire", "Vidhide", "Streamwish");

    [Fact]
    public void ParaAjuste_ConElOrdenPredeterminado_DevuelveNulo_ParaQueUnCambioFuturoDelPredeterminadoSeAplique() =>
        OrdenServidores.ParaAjuste(Predeterminado).Should().BeNull();

    [Fact]
    public void ParaAjuste_ConOtroOrden_LoGuardaSeparadoPorComas() =>
        OrdenServidores.ParaAjuste(["Voe", "MP4Upload", "TransferIt", "Mega"]).Should().Be("Voe,MP4Upload,TransferIt,Mega");

    // === Cómo lo usa el resolver ===

    private static AnimeAv1HtmlParser.EmbedServidor E(string servidor) => new(servidor, $"https://{servidor.ToLowerInvariant()}.example/x", "SUB");

    [Fact]
    public void OrdenarEmbeds_ConUnaListaDeServidores_RespetaEseOrdenYDespuesElResto()
    {
        var embeds = new[] { E("MP4Upload"), E("TransferIt"), E("Mega"), E("Voe"), E("HLS") };

        AnimeAv1HtmlParser.OrdenarEmbedsPorPreferencia(embeds, servidorPreferido: "Voe, Mega").Select(e => e.Server)
            .Should().Equal("Voe", "Mega", "MP4Upload", "HLS", "TransferIt");
    }

    [Fact]
    public void OrdenarEmbeds_ConElOrdenCompletoDelUsuario_ComoLoGuardaLaConfiguracion()
    {
        var embeds = new[] { E("MP4Upload"), E("TransferIt"), E("Mega"), E("Voe") };
        string? ajuste = OrdenServidores.ParaAjuste(["TransferIt", "Mega", "MP4Upload", "Voe"]);

        AnimeAv1HtmlParser.OrdenarEmbedsPorPreferencia(embeds, servidorPreferido: ajuste).Select(e => e.Server)
            .Should().Equal("TransferIt", "Mega", "MP4Upload", "Voe");
    }

    [Fact]
    public void OrdenarEmbeds_ConNombresQueElSitioNoPublica_LosIgnora()
    {
        var embeds = new[] { E("MP4Upload"), E("Voe") };

        AnimeAv1HtmlParser.OrdenarEmbedsPorPreferencia(embeds, servidorPreferido: "Mega,Voe").Select(e => e.Server)
            .Should().Equal("Voe", "MP4Upload");
    }

    // === La pantalla de Configuración ===

    private static (ConfiguracionViewModel Vm, AppSettings Config) Crear(string? ajuste)
    {
        var config = new AppSettings { ServidorPreferidoAnimeAv1 = ajuste };
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.ObtenerConfiguracion()).Returns(config);
        var db = Mock.Of<IDatabaseService>();
        var vm = new ConfiguracionViewModel(settings.Object, Mock.Of<IAuthService>(), db, Mock.Of<IDialogService>(),
            new CacheMaintenanceService(db), Mock.Of<IPluginService>());
        return (vm, config);
    }

    private static string[] Nombres(ConfiguracionViewModel vm) => vm.OrdenServidores.Select(s => s.Nombre).ToArray();

    [Fact]
    public void Configuracion_MuestraElOrdenGuardado()
    {
        var (vm, _) = Crear("Voe");

        Nombres(vm).Should().Equal("Voe", "MP4Upload", "TransferIt", "Mega", "Mediafire", "Vidhide", "Streamwish");
    }

    [Fact]
    public void Configuracion_SinAjusteGuardado_MuestraElOrdenPredeterminado()
    {
        var (vm, _) = Crear(null);

        Nombres(vm).Should().Equal(Predeterminado);
        vm.OrdenServidores.Select(s => s.Posicion).Should().Equal(1, 2, 3, 4, 5, 6, 7);
    }

    [Fact]
    public void SubirUnServidor_LoAdelantaUnPuestoYActualizaLasPosiciones()
    {
        var (vm, _) = Crear(null);
        var mega = vm.OrdenServidores.Single(s => s.Nombre == "Mega");

        mega.SubirCommand.Execute(null);

        Nombres(vm).Should().Equal("MP4Upload", "Mega", "TransferIt", "Mediafire", "Vidhide", "Voe", "Streamwish");
        vm.OrdenServidores.Select(s => s.Posicion).Should().Equal(1, 2, 3, 4, 5, 6, 7);
    }

    [Fact]
    public void BajarUnServidor_LoRetrasaUnPuesto()
    {
        var (vm, _) = Crear(null);

        vm.OrdenServidores[0].BajarCommand.Execute(null);

        Nombres(vm).Should().Equal("TransferIt", "MP4Upload", "Mega", "Mediafire", "Vidhide", "Voe", "Streamwish");
    }

    [Fact]
    public void ElPrimeroNoPuedeSubirNiElUltimoBajar()
    {
        var (vm, _) = Crear(null);

        vm.OrdenServidores[0].PuedeSubir.Should().BeFalse();
        vm.OrdenServidores[0].PuedeBajar.Should().BeTrue();
        vm.OrdenServidores[^1].PuedeBajar.Should().BeFalse();
        vm.OrdenServidores[^1].PuedeSubir.Should().BeTrue();

        vm.OrdenServidores[0].SubirCommand.Execute(null);
        vm.OrdenServidores[^1].BajarCommand.Execute(null);
        Nombres(vm).Should().Equal(Predeterminado, "mover más allá de los extremos no hace nada");
    }

    [Fact]
    public void LosBotonesSeActualizanTrasMover()
    {
        var (vm, _) = Crear(null);

        vm.OrdenServidores[1].SubirCommand.Execute(null); // TransferIt pasa a ser el primero

        vm.OrdenServidores[0].Nombre.Should().Be("TransferIt");
        vm.OrdenServidores[0].PuedeSubir.Should().BeFalse();
        vm.OrdenServidores[1].PuedeSubir.Should().BeTrue();
    }

    [Fact]
    public async Task Guardar_ConUnOrdenNuevo_LoEscribeEnElAjusteSeparadoPorComas()
    {
        var (vm, config) = Crear(null);
        for (int i = 0; i < 5; i++) vm.OrdenServidores.Single(s => s.Nombre == "Voe").SubirCommand.Execute(null); // de la 6.ª fila a la 1.ª

        await vm.GuardarPreferenciasAsync();

        config.ServidorPreferidoAnimeAv1.Should().Be("Voe,MP4Upload,TransferIt,Mega,Mediafire,Vidhide,Streamwish");
    }

    [Fact]
    public async Task Guardar_ConElOrdenPredeterminado_DejaElAjusteNulo()
    {
        var (vm, config) = Crear("Voe");
        vm.RestablecerOrdenServidoresCommand.Execute(null);

        await vm.GuardarPreferenciasAsync();

        config.ServidorPreferidoAnimeAv1.Should().BeNull();
    }

    [Fact]
    public void Restablecer_VuelveAlOrdenPredeterminado()
    {
        var (vm, _) = Crear("Voe,Mega");

        vm.RestablecerOrdenServidoresCommand.Execute(null);

        Nombres(vm).Should().Equal(Predeterminado);
        vm.OrdenServidores.Select(s => s.Posicion).Should().Equal(1, 2, 3, 4, 5, 6, 7);
    }
}
