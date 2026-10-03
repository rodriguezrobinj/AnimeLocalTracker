using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FlyleafLib;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// "Rendimiento de video": el modo automático elige saltos y escalado según el equipo (decodificador real, hilos, tarjeta) y los
/// ajustes manuales lo fuerzan. Caso real medido: Intel HD 620 + i5-7300U, AV1 por procesador → saltos equilibrados, sin escalado.
/// </summary>
public class RendimientoVideoTests
{
    // === Saltos ===

    [Theory]
    [InlineData(ModosRendimientoVideo.Automatico, false, 4, EstrategiaSaltos.Equilibrados)] // el equipo del usuario con AV1
    [InlineData(ModosRendimientoVideo.Automatico, true, 4, EstrategiaSaltos.Exactos)]       // H.264 por la tarjeta
    [InlineData(ModosRendimientoVideo.Automatico, false, 16, EstrategiaSaltos.Exactos)]     // procesador potente
    [InlineData(ModosRendimientoVideo.Exactos, false, 4, EstrategiaSaltos.Exactos)]
    [InlineData(ModosRendimientoVideo.Rapidos, true, 32, EstrategiaSaltos.Rapidos)]
    [InlineData(null, false, 4, EstrategiaSaltos.Equilibrados)]
    [InlineData("valor-raro", true, 4, EstrategiaSaltos.Exactos)]
    public void Saltos_SegunModoYEquipo(string? modo, bool porHardware, int hilos, EstrategiaSaltos esperado) =>
        MotorVideo.ElegirEstrategiaSaltos(modo, porHardware, hilos).Should().Be(esperado);

    // === Respaldo por procesador ===

    [Theory]
    [InlineData(true, 0u, false, true)]   // Tokyo Revengers en la HD 620: GPU elegida, 0 fotogramas tras la espera
    [InlineData(true, 0u, true, false)]   // ya se reintentó por procesador: no se repite en bucle
    [InlineData(true, 5u, false, false)]  // la GPU sí mostró imagen
    [InlineData(false, 0u, false, false)] // ya va por procesador: reabrir no cambiaría nada
    public void RespaldoPorProcesador_SoloSiLaTarjetaNoMostroImagen(bool porHardware, uint fotogramas, bool yaReintentado, bool esperado) =>
        MotorVideo.DebeReintentarPorProcesador(porHardware, fotogramas, yaReintentado).Should().Be(esperado);

    // === Escalado inteligente ===

    [Theory]
    [InlineData(ModosRendimientoVideo.Automatico, GPUVendor.Intel, 128, false)]   // integrada (Intel HD 620)
    [InlineData(ModosRendimientoVideo.Automatico, GPUVendor.Nvidia, 8192, true)]  // RTX dedicada
    [InlineData(ModosRendimientoVideo.Automatico, GPUVendor.Intel, 8192, true)]   // Intel Arc
    [InlineData(ModosRendimientoVideo.Automatico, GPUVendor.ATI, 8192, false)]    // Flyleaf no lo tiene para AMD
    [InlineData(ModosRendimientoVideo.Activado, GPUVendor.Intel, 128, true)]
    [InlineData(ModosRendimientoVideo.Desactivado, GPUVendor.Nvidia, 8192, false)]
    public void Escalado_SegunModoYTarjeta(string modo, GPUVendor fabricante, long memoriaMb, bool esperado) =>
        MotorVideo.UsarEscaladoInteligente(modo, fabricante, memoriaMb).Should().Be(esperado);

    // === Tarjeta elegida ===

    [Fact]
    public void ElPatronDeLaTarjeta_CoincideConSuNombreAunqueLleveParentesis()
    {
        // Flyleaf compara Config.Video.GPUAdapter como expresión regular: "Intel(R)" sin escapar no coincidiría consigo mismo.
        string patron = MotorVideo.PatronTarjeta("Intel(R) HD Graphics 620")!;

        Regex.IsMatch("Intel(R) HD Graphics 620", patron, RegexOptions.IgnoreCase).Should().BeTrue();
        Regex.IsMatch("Intel(R) HD Graphics 6200", patron, RegexOptions.IgnoreCase).Should().BeFalse("no debe elegir otra tarjeta por parecido");
        Regex.IsMatch("IntelR HD Graphics 620", "Intel(R) HD Graphics 620").Should().BeTrue("así fallaba sin escapar: los paréntesis son un grupo");
    }

    [Fact]
    public void SinTarjetaElegida_NoSeFuerzaNinguna()
    {
        MotorVideo.PatronTarjeta(null).Should().BeNull();
        MotorVideo.PatronTarjeta("  ").Should().BeNull();
    }

    // === Salto "rápido": siempre a un fotograma clave ===

    private static readonly List<double> Claves = [588.58, 599.67, 610.08, 615.04, 625.46];

    [Fact]
    public void Rapido_SiempreCaeEnUnFotogramaClave_RespetandoLaDireccion()
    {
        // Desde 600, +10 → 610: 610,08.
        var (adelante, preciso1) = FotogramasClaveService.ElegirDestinoRapido(Claves, 610, TipoSalto.Adelante, desde: 600);
        adelante.Should().BeApproximately(610.13, 0.001);
        preciso1.Should().BeFalse();
        // Desde 612, -10 → 602: 599,67 (610,08 solo retrocedería 2 s).
        var (atras, preciso2) = FotogramasClaveService.ElegirDestinoRapido(Claves, 602, TipoSalto.Atras, desde: 612);
        atras.Should().BeApproximately(599.72, 0.001);
        preciso2.Should().BeFalse();
    }

    [Fact]
    public void Rapido_AunqueElFotogramaClaveQuedeLejos_NoHaceSaltoPreciso()
    {
        // 604,5 queda a más de 4 s de todos: el modo equilibrado haría salto preciso, el rápido va al más cercano.
        FotogramasClaveService.ElegirDestino(Claves, 604.5, TipoSalto.Libre).Preciso.Should().BeTrue();
        FotogramasClaveService.ElegirDestinoRapido(Claves, 604.5, TipoSalto.Libre).Preciso.Should().BeFalse();
    }

    [Fact]
    public void Rapido_SinListaDeFotogramasClave_SaltaRapidoAlSegundoPedido()
    {
        FotogramasClaveService.ElegirDestinoRapido(null, 300, TipoSalto.Adelante, desde: 290).Should().Be((300, false));
    }

    // === Configuración ===

    private static (ConfiguracionViewModel Vm, AppSettings Config) CrearConfiguracion(AppSettings config)
    {
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.ObtenerConfiguracion()).Returns(config);
        var db = Mock.Of<IDatabaseService>();
        var vm = new ConfiguracionViewModel(settings.Object, Mock.Of<IAuthService>(), db, Mock.Of<IDialogService>(),
            new CacheMaintenanceService(db), Mock.Of<IPluginService>());
        return (vm, config);
    }

    [Fact]
    public void Configuracion_MuestraLosAjustesGuardados()
    {
        var (vm, _) = CrearConfiguracion(new AppSettings
        {
            ModoSaltosVideo = ModosRendimientoVideo.Rapidos, AceleracionHardwareVideo = false,
            TarjetaGraficaVideo = "NVIDIA GeForce RTX 4060 Laptop GPU", EscaladoInteligenteVideo = ModosRendimientoVideo.Activado
        });

        (vm.ModoSaltosVideo, vm.AceleracionHardwareVideo, vm.TarjetaGraficaVideo, vm.EscaladoInteligenteVideo)
            .Should().Be((ModosRendimientoVideo.Rapidos, false, "NVIDIA GeForce RTX 4060 Laptop GPU", ModosRendimientoVideo.Activado));
    }

    [Fact]
    public void Configuracion_ConValoresRarosEnElArchivo_VuelveAAutomatico()
    {
        var (vm, _) = CrearConfiguracion(new AppSettings { ModoSaltosVideo = "turbo", EscaladoInteligenteVideo = "" });

        (vm.ModoSaltosVideo, vm.EscaladoInteligenteVideo).Should().Be((ModosRendimientoVideo.Automatico, ModosRendimientoVideo.Automatico));
    }

    [Fact]
    public async Task Configuracion_GuardaLosAjustes_YLaTarjetaAutomaticaComoNula()
    {
        var (vm, config) = CrearConfiguracion(new AppSettings { TarjetaGraficaVideo = "Intel(R) HD Graphics 620" });
        vm.ModoSaltosVideo = ModosRendimientoVideo.Exactos;
        vm.AceleracionHardwareVideo = false;
        vm.TarjetaGraficaVideo = string.Empty;
        vm.EscaladoInteligenteVideo = ModosRendimientoVideo.Desactivado;

        await vm.GuardarPreferenciasAsync();

        (config.ModoSaltosVideo, config.AceleracionHardwareVideo, config.TarjetaGraficaVideo, config.EscaladoInteligenteVideo)
            .Should().Be((ModosRendimientoVideo.Exactos, false, (string?)null, ModosRendimientoVideo.Desactivado));
    }

    [Fact]
    public void ElDiagnostico_ExplicaComoSeReprodujoElUltimoVideo()
    {
        ConfiguracionViewModel.DescribirUltimoVideo(null).Should().Be(LocalizationService.T("Cfg_DiagSinVideo"));

        string texto = ConfiguracionViewModel.DescribirUltimoVideo(
            new InformeVideo("av1", false, "Intel(R) HD Graphics 620", EstrategiaSaltos.Equilibrados, false, DateTime.UtcNow));

        texto.Should().Contain("AV1").And.Contain(LocalizationService.T("Cfg_DiagPorProcesador")).And.Contain(LocalizationService.T("Cfg_DiagSaltosEquilibrados"));
    }

    [Fact]
    public void EnPruebas_NoSeArrancaElMotorDeVideo()
    {
        // Flyleaf puede colgarse sin ventana: en las pruebas no se listan tarjetas (lista vacía) en vez de arrancarlo.
        MotorVideo.ListarTarjetas().Should().BeEmpty();
    }
}
