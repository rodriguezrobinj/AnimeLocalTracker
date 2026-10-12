using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
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

    // === Escalado inteligente (Anime4K): funciona en cualquier tarjeta y el reproductor lo vigila ===

    [Theory]
    [InlineData(ModosRendimientoVideo.Automatico, true)]
    [InlineData(ModosRendimientoVideo.Activado, true)]
    [InlineData(ModosRendimientoVideo.Desactivado, false)]
    [InlineData(null, true)]            // settings.json editado a mano: como Automático
    [InlineData("", true)]
    [InlineData("cualquiera", true)]
    public void Escalado_SegunModo(string? modo, bool esperado) =>
        MotorVideo.UsarEscaladoInteligente(modo).Should().Be(esperado);

    [Theory]
    [InlineData(FabricanteGrafica.Intel, NivelEscalado.Ligero)]       // medido en una HD 620: el medio pierde 9 fotogramas por segundo
    [InlineData(FabricanteGrafica.Desconocido, NivelEscalado.Ligero)]
    [InlineData(FabricanteGrafica.Nvidia, NivelEscalado.Medio)]
    [InlineData(FabricanteGrafica.Amd, NivelEscalado.Medio)]
    public void NivelAnime4K_SegunLaTarjeta(FabricanteGrafica fabricante, NivelEscalado esperado) =>
        MotorVideo.NivelAnime4K(fabricante).Should().Be(esperado);

    [Fact]
    public void SombreadoresAnime4K_ListaLosTresDeCadaNivelEnOrden()
    {
        MotorVideo.SombreadoresAnime4K(NivelEscalado.Ligero).Should().Equal(
            "Anime4K_Clamp_Highlights.glsl", "Anime4K_Restore_CNN_S.glsl", "Anime4K_Upscale_CNN_x2_S.glsl");
        MotorVideo.SombreadoresAnime4K(NivelEscalado.Medio).Should().Equal(
            "Anime4K_Clamp_Highlights.glsl", "Anime4K_Restore_CNN_M.glsl", "Anime4K_Upscale_CNN_x2_M.glsl");
    }

    [Fact]
    public void LosShadersDeAnime4K_EstanJuntoALaApp()
    {
        string carpeta = System.IO.Path.Combine(AppContext.BaseDirectory, "Shaders", "Anime4K");
        foreach (string nombre in System.Linq.Enumerable.Concat(MotorVideo.SombreadoresAnime4K(NivelEscalado.Ligero), MotorVideo.SombreadoresAnime4K(NivelEscalado.Medio)))
        {
            string ruta = System.IO.Path.Combine(carpeta, nombre);
            System.IO.File.Exists(ruta).Should().BeTrue($"{nombre} se copia con la app");
            System.IO.File.ReadAllText(ruta).Should().StartWith("// MIT License", "cada shader lleva su aviso de licencia");
        }
    }

    [Fact]
    public void LosShadersQueNoAmplian_SoloActuanSiElVideoSeAgranda()
    {
        // "Restaurar" y "recortar brillos" corrían siempre: medido en la app con una Intel HD 620, a 1080p sin ampliar perdían
        // 8 fotogramas por segundo. Cada pasada de los cinco archivos lleva la condición de los shaders de ampliación.
        const string condicion = "//!WHEN OUTPUT.w MAIN.w / 1.200 > OUTPUT.h MAIN.h / 1.200 > *";
        string carpeta = System.IO.Path.Combine(AppContext.BaseDirectory, "Shaders", "Anime4K");
        foreach (string ruta in System.IO.Directory.GetFiles(carpeta, "*.glsl"))
        {
            string[] lineas = System.IO.File.ReadAllLines(ruta);
            int pasadas = System.Linq.Enumerable.Count(lineas, l => l.StartsWith("//!DESC", StringComparison.Ordinal));
            int condicionadas = System.Linq.Enumerable.Count(lineas, l => l.Trim() == condicion);
            pasadas.Should().BeGreaterThan(0);
            condicionadas.Should().Be(pasadas, $"todas las pasadas de {System.IO.Path.GetFileName(ruta)} llevan la condición");
        }
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
            ModoSaltosVideo = ModosRendimientoVideo.Rapidos, AceleracionHardwareVideo = false, EscaladoInteligenteVideo = ModosRendimientoVideo.Activado
        });

        (vm.ModoSaltosVideo, vm.AceleracionHardwareVideo, vm.EscaladoInteligenteVideo)
            .Should().Be((ModosRendimientoVideo.Rapidos, false, ModosRendimientoVideo.Activado));
    }

    [Fact]
    public void Configuracion_ConValoresRarosEnElArchivo_VuelveAAutomatico()
    {
        var (vm, _) = CrearConfiguracion(new AppSettings { ModoSaltosVideo = "turbo", EscaladoInteligenteVideo = "" });

        (vm.ModoSaltosVideo, vm.EscaladoInteligenteVideo).Should().Be((ModosRendimientoVideo.Automatico, ModosRendimientoVideo.Automatico));
    }

    [Fact]
    public async Task Configuracion_GuardaLosAjustes()
    {
        var (vm, config) = CrearConfiguracion(new AppSettings());
        vm.ModoSaltosVideo = ModosRendimientoVideo.Exactos;
        vm.AceleracionHardwareVideo = false;
        vm.EscaladoInteligenteVideo = ModosRendimientoVideo.Desactivado;

        await vm.GuardarPreferenciasAsync();

        (config.ModoSaltosVideo, config.AceleracionHardwareVideo, config.EscaladoInteligenteVideo)
            .Should().Be((ModosRendimientoVideo.Exactos, false, ModosRendimientoVideo.Desactivado));
    }

    [Fact]
    public async Task Configuracion_ElModoDeCompatibilidadSeCargaYSeGuarda()
    {
        new AppSettings().VideoPorSoftware.Should().BeFalse("apagado por defecto: solo para equipos donde el video no se ve");
        var (vm, config) = CrearConfiguracion(new AppSettings { VideoPorSoftware = true });
        vm.VideoPorSoftware.Should().BeTrue();

        vm.VideoPorSoftware = false;
        await vm.GuardarPreferenciasAsync();

        config.VideoPorSoftware.Should().BeFalse();
    }

    [Fact]
    public async Task Configuracion_ElColorLigeroSeCargaYSeGuarda()
    {
        new AppSettings().ColorLigeroVideo.Should().BeFalse("apagado por defecto: la calidad de imagen de siempre");
        var (vm, config) = CrearConfiguracion(new AppSettings());
        vm.ColorLigeroVideo.Should().BeFalse();

        vm.ColorLigeroVideo = true;
        await vm.GuardarPreferenciasAsync();

        config.ColorLigeroVideo.Should().BeTrue();
    }

    [Fact]
    public async Task AjustesDeUnaVersionAnterior_SeLeenSinLasClavesRetiradas()
    {
        // Etapa 6 de libmpv: ya no hay motor clásico, ni tarjeta que elegir, ni borde en la caja de los subtítulos. Quien actualiza
        // trae esas claves en su settings.json (aquí con el valor antiguo «Flyleaf» a propósito).
        string carpeta = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "alt_ajustes_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(carpeta);
        try
        {
            string archivo = System.IO.Path.Combine(carpeta, "settings.json");
            System.IO.File.WriteAllText(archivo, """
                {
                  "MotorVideo": "Flyleaf",
                  "TarjetaGraficaVideo": "Intel(R) HD Graphics 620",
                  "AceleracionHardwareVideo": false,
                  "EstiloSubtitulos": { "Tamano": 50, "OpacidadFondo": 60, "GrosorBorde": 2, "ColorBorde": "#FF0000" }
                }
                """);
            var servicio = new SettingsService(archivo);

            var config = servicio.ObtenerConfiguracion();

            config.AceleracionHardwareVideo.Should().BeFalse("lo que sigue existiendo se conserva");
            (config.EstiloSubtitulos.Tamano, config.EstiloSubtitulos.OpacidadFondo).Should().Be((50, 60));

            await servicio.GuardarConfiguracionAsync(config);

            string guardado = System.IO.File.ReadAllText(archivo);
            guardado.Should().NotContain("MotorVideo").And.NotContain("TarjetaGraficaVideo").And.NotContain("GrosorBorde").And.NotContain("ColorBorde");
            guardado.Should().Contain("\"AceleracionHardwareVideo\": false").And.Contain("\"OpacidadFondo\": 60");
        }
        finally
        {
            try { System.IO.Directory.Delete(carpeta, true); } catch { }
        }
    }

    [Fact]
    public void ElDiagnosticoDelEquipo_NoInventaUnaTarjetaQueNoConoce()
    {
        // La tarjeta la dice el lienzo al pintar un video: sin haber abierto ninguno (o pintando por software) solo se dan los hilos.
        var (vm, _) = CrearConfiguracion(new AppSettings());

        vm.ActualizarRendimientoVideo();

        vm.DiagnosticoEquipoTexto.Should().Contain(Environment.ProcessorCount.ToString()).And.NotContain("?");
    }

    [Fact]
    public void ElDiagnostico_ExplicaComoSeReprodujoElUltimoVideo()
    {
        ConfiguracionViewModel.DescribirUltimoVideo(null).Should().Be(LocalizationService.T("Cfg_DiagSinVideo"));

        string texto = ConfiguracionViewModel.DescribirUltimoVideo(
            new InformeVideo("av1", false, "Intel(R) HD Graphics 620", EstrategiaSaltos.Equilibrados, false, DateTime.UtcNow));

        texto.Should().Contain("AV1").And.Contain(LocalizationService.T("Cfg_DiagPorProcesador")).And.Contain(LocalizationService.T("Cfg_DiagSaltosEquilibrados"));
    }
}
