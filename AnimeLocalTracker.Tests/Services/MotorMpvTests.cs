using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Core;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

public class MotorMpvIdiomasTests
{
    [Theory]
    [InlineData("spa", "es")]
    [InlineData("jpn", "ja")]
    [InlineData("eng", "en")]
    [InlineData("por", "pt")]
    [InlineData("ger", "de")] // códigos bibliográficos: los que usan muchos .mkv
    [InlineData("fre", "fr")]
    [InlineData("chi", "zh")]
    [InlineData("es", "es")]
    [InlineData("ES", "es")]
    [InlineData("es-419", "es")]
    [InlineData("pt-BR", "pt")]
    public void IdiomaDosLetras_TraduceLoQueEscribenLosArchivos(string deLaPista, string esperado)
    {
        MotorMpv.IdiomaDosLetras(deLaPista).Should().Be(esperado);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("und")]
    [InlineData("zxx")]
    [InlineData("esto no es un idioma")]
    public void IdiomaDosLetras_SinIdiomaReconocible_DaNulo(string? deLaPista)
    {
        MotorMpv.IdiomaDosLetras(deLaPista).Should().BeNull();
    }
}

/// <summary>Lo que se le dice a libmpv, sin libmpv: estilo de subtítulos, cadena de filtros de audio y fabricante de la tarjeta.</summary>
public class MotorMpvTraduccionesTests
{
    private static Dictionary<string, string> Opciones(EstiloSubtitulos estilo, bool forzar) =>
        MotorMpv.OpcionesDeEstilo(estilo, forzar).ToDictionary(o => o.Nombre, o => o.Valor);

    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    [Fact]
    public void OpcionesDeEstilo_ElEstiloPorDefecto()
    {
        var o = Opciones(new EstiloSubtitulos(), forzar: false);

        o["sub-font"].Should().Be("Trebuchet MS");
        o["sub-font-size"].Should().Be(N(44 * MotorMpv.CeldaDeLaFuente(new EstiloSubtitulos()) * 720 / 768));
        o["sub-bold"].Should().Be("yes");
        o["sub-italic"].Should().Be("no");
        o["sub-color"].Should().Be("#FFFFFF");
        o["sub-outline-color"].Should().Be("#000000");
        o["sub-outline-size"].Should().Be(N(3 * 720.0 / 768));
        o["sub-border-style"].Should().Be("outline-and-shadow");
        o["sub-back-color"].Should().Be("#00000000", "sin caja");
        o["sub-ass-override"].Should().Be("scale", "las pistas ASS conservan su estilo, y se pueden subir cuando salen los controles");
        o["sub-ass-style-overrides"].Should().BeEmpty();
    }

    [Fact]
    public void OpcionesDeEstilo_ConCajaAl50()
    {
        var o = Opciones(new EstiloSubtitulos { OpacidadFondo = 50 }, forzar: false);

        o["sub-border-style"].Should().Be("background-box");
        o["sub-back-color"].Should().Be("#80000000");
    }

    [Fact]
    public void OpcionesDeEstilo_AlForzar_ElSubtituloPierdeSuEstiloYSusEtiquetas()
    {
        Opciones(new EstiloSubtitulos(), forzar: true)["sub-ass-override"].Should().Be("strip");
    }

    [Fact]
    public void OpcionesDeEstilo_ElSubrayadoSoloAlForzar()
    {
        var subrayado = new EstiloSubtitulos { Subrayado = true };

        Opciones(subrayado, forzar: true)["sub-ass-style-overrides"].Should().Be("Underline=-1");
        Opciones(subrayado, forzar: false)["sub-ass-style-overrides"].Should().BeEmpty("subrayaría también los subtítulos con estilo propio");
        Opciones(new EstiloSubtitulos(), forzar: true)["sub-ass-style-overrides"].Should().BeEmpty();
    }

    [Fact]
    public void OpcionesDeEstilo_UsaPuntoDecimalAunqueLaCulturaSeaEspanola()
    {
        var antes = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("es-ES");
            var o = Opciones(new EstiloSubtitulos { Tamano = 44.5, GrosorContorno = 2.5 }, forzar: false);

            o["sub-font-size"].Should().Contain(".").And.NotContain(",");
            o["sub-outline-size"].Should().Be("2.34").And.NotContain(",");
        }
        finally
        {
            CultureInfo.CurrentCulture = antes;
        }
    }

    [Fact]
    public void ElTamano_ConElVideoA768DeAlto_MideLoQueDiceConfiguracion()
    {
        // libmpv mide la celda de la fuente (más alta que el cuerpo de la letra) y escala con el alto del video desde 720.
        var estilo = new EstiloSubtitulos { Tamano = 44 };
        double deLibmpv = double.Parse(Opciones(estilo, forzar: true)["sub-font-size"], CultureInfo.InvariantCulture);

        double celdaEnPantalla = deLibmpv * 768 / 720;
        (celdaEnPantalla / MotorMpv.CeldaDeLaFuente(estilo)).Should().BeApproximately(44, 0.05, "el cuerpo de la letra en píxeles");
    }

    [Fact]
    public void LaCeldaDeLaFuente_SeMideEnLaFuenteElegida_YSiNoExisteSeUsaUnValorTipico()
    {
        MotorMpv.CeldaDeLaFuente(new EstiloSubtitulos()).Should().BeInRange(1.10, 1.25, "Trebuchet MS: 1,16");
        MotorMpv.CeldaDeLaFuente(new EstiloSubtitulos { Fuente = "Comic Sans MS" })
            .Should().BeGreaterThan(MotorMpv.CeldaDeLaFuente(new EstiloSubtitulos { Fuente = "Arial" }), "cada fuente tiene su celda");
        MotorMpv.CeldaDeLaFuente(new EstiloSubtitulos { Fuente = "" }).Should().BeInRange(1.0, 1.5);
    }

    [Fact]
    public void CadenaDeFiltros_SinFiltrosEsVacia()
    {
        MotorMpv.CadenaDeFiltros([]).Should().BeEmpty();
    }

    [Fact]
    public void CadenaDeFiltros_NombraCadaFiltroConSuId()
    {
        var filtros = EcualizadorAudio.ConstruirFiltros(true, MotorMpvTests.Ganancias(banda0: 3), true, MotorMpvTests.ModoNoche);

        string cadena = MotorMpv.CadenaDeFiltros(filtros);

        cadena.Should().StartWith("@filtros:lavfi=[").And.EndWith("]");
        cadena.Should().Contain("volume@eq_pre=volume=-3.0dB");
        cadena.Should().Contain("equalizer@eq_b0=f=31:t=o:w=1:g=3.0");
        cadena.Should().Contain($"acompressor@{EcualizadorAudio.IdModoNoche}={MotorMpvTests.ModoNoche}");
    }

    [Theory]
    [InlineData("Intel", FabricanteGrafica.Intel)]
    [InlineData("Intel Intel(R) HD Graphics 620", FabricanteGrafica.Intel)]
    [InlineData("NVIDIA Corporation GeForce RTX 3060/PCIe/SSE2", FabricanteGrafica.Nvidia)]
    [InlineData("ATI Technologies Inc. Radeon RX 580 Series", FabricanteGrafica.Amd)]
    [InlineData("AMD Radeon(TM) Graphics", FabricanteGrafica.Amd)]
    [InlineData("Microsoft Basic Render Driver", FabricanteGrafica.Desconocido)]
    [InlineData("", FabricanteGrafica.Desconocido)]
    [InlineData(null, FabricanteGrafica.Desconocido)]
    public void FabricantePorNombre_LoSacaDelTextoDelControlador(string? texto, FabricanteGrafica esperado)
    {
        MotorVideo.FabricantePorNombre(texto).Should().Be(esperado);
    }
}

/// <summary>Un video diminuto de 6 s con dos pistas de audio (japonés y español) y una de subtítulos, hecho con el FFmpeg de la app.</summary>
public sealed class VideoDePrueba : IDisposable
{
    public string Carpeta { get; } = Path.Combine(Path.GetTempPath(), "alt_mpv_" + Guid.NewGuid().ToString("N"));
    public string Ruta { get; }
    public string RutaConTildes { get; }

    public VideoDePrueba()
    {
        Directory.CreateDirectory(Carpeta);
        Ruta = Path.Combine(Carpeta, "prueba.mkv");
        string srt = Path.Combine(Carpeta, "subs.srt");
        File.WriteAllText(srt, "1\n00:00:01,000 --> 00:00:03,000\nHola\n\n2\n00:00:03,500 --> 00:00:05,000\nAdiós\n");
        string[] argumentos =
        [
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "testsrc=duration=6:size=320x180:rate=24",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=6",
            "-f", "lavfi", "-i", "sine=frequency=880:duration=6",
            "-i", srt,
            "-map", "0:v", "-map", "1:a", "-map", "2:a", "-map", "3:s",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-g", "24",
            "-c:a", "aac", "-b:a", "48k", "-c:s", "srt",
            "-metadata:s:a:0", "language=jpn", "-metadata:s:a:1", "language=spa", "-metadata:s:a:1", "title=Latino",
            "-metadata:s:s:0", "language=spa",
            Ruta
        ];
        var resultado = AnimeLocalTracker.Core.ProcesoExterno.EjecutarAsync(FfmpegLocator.Ffmpeg, argumentos, TimeSpan.FromSeconds(60), CancellationToken.None)
            .GetAwaiter().GetResult();
        if (resultado == null || resultado.Codigo != 0) throw new InvalidOperationException("No se pudo generar el video de prueba: " + resultado?.Error);

        RutaConTildes = Path.Combine(Carpeta, "[Fansub] Cañón ñandú 第02話 (1080p).mkv");
        File.Copy(Ruta, RutaConTildes);
    }

    public void Dispose()
    {
        try { Directory.Delete(Carpeta, true); } catch { }
    }
}

/// <summary>libmpv de verdad, sin ventana ni sonido. Si la biblioteca no está descargada (tools\Get-Libmpv.ps1), las pruebas no hacen nada.</summary>
public sealed class MotorMpvTests : IClassFixture<VideoDePrueba>
{
    private static readonly AjustesInicialesMotor Ajustes = new(false, null, []);
    private readonly VideoDePrueba _video;

    public MotorMpvTests(VideoDePrueba video) => _video = video;

    private static async Task EsperarHasta(Func<bool> condicion, int ms = 15000)
    {
        var limite = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condicion() && DateTime.UtcNow < limite) await Task.Delay(20);
        condicion().Should().BeTrue($"la condición debía cumplirse en {ms} ms");
    }

    private static (MotorMpv Motor, List<AperturaTerminada> Avisos) Crear()
    {
        var motor = MotorMpv.Crear(Ajustes, sinSalida: true)!;
        var avisos = new List<AperturaTerminada>();
        motor.AperturaTerminada += (_, e) => { lock (avisos) avisos.Add(e); };
        return (motor, avisos);
    }

    [Fact]
    public async Task AbrirUnVideo_AvisaReproduceYDaSusPistas()
    {
        if (!AnimeLocalTracker.Services.Native.LibMpv.Disponible) return;
        var (motor, avisos) = Crear();
        using var _ = motor;
        motor.Estado.Should().Be(EstadoMotor.Detenido);

        motor.Abrir(_video.Ruta);
        motor.Estado.Should().Be(EstadoMotor.Abriendo);

        await EsperarHasta(() => avisos.Count == 1);
        avisos[0].Should().Be(new AperturaTerminada(ClaseApertura.Video, true, null));
        await EsperarHasta(() => motor.Estado == EstadoMotor.Reproduciendo && motor.Posicion > TimeSpan.FromMilliseconds(200));
        motor.Duracion.TotalSeconds.Should().BeApproximately(6, 0.5);
        motor.TamanoVideo.Should().Be((320, 180));
        motor.ProporcionVideo.Should().BeApproximately(16 / 9.0, 0.01);
        motor.CodecVideo.Should().Be("h264");
        motor.FotogramasMostrados.Should().BeGreaterThan(0);

        motor.PistasAudio.Select(p => (p.Idioma, p.Titulo)).Should().Equal(("ja", null), ("es", "Latino"));
        motor.IndicePistaAudioActual.Should().Be(motor.PistasAudio[0].Indice);
        motor.PistasSubtitulos.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new PistaSubtitulosMotor(3, "es", null, EsImagen: false, EsAss: false, RutaExterna: null));
        motor.IndicePistaSubtitulosDelMotor.Should().Be(-1, "el motor arranca con sus subtítulos apagados: la app decide");
    }

    [Fact]
    public async Task ConSalidaDeVideo_ElArchivoNoSeCargaHastaQueHayDondePintar()
    {
        // En la app el reproductor abre en cuanto tiene motor, y la vista tarda en tener dónde pintar (hasta 2 s recién abierta la
        // app). Cargando antes, libmpv dejaba el archivo sin video y avanzando: el episodio empezaba con segundos ya consumidos y,
        // si la vista tardaba, el reproductor creía que la tarjeta gráfica no daba imagen y lo reabría por procesador.
        if (!AnimeLocalTracker.Services.Native.LibMpv.Disponible) return;
        using var motor = MotorMpv.Crear(Ajustes)!;
        motor.Silencio = true;
        var avisos = new List<AperturaTerminada>();
        motor.AperturaTerminada += (_, e) => { lock (avisos) avisos.Add(e); };

        motor.Abrir(_video.Ruta);
        await Task.Delay(700);
        avisos.Should().BeEmpty("sin dónde pintar todavía no se carga nada");
        motor.Estado.Should().Be(EstadoMotor.Abriendo);
        motor.Posicion.Should().Be(TimeSpan.Zero);

        motor.PintorCreado();
        // Al menos uno: en una máquina sin tarjeta de sonido (el CI) al aviso de "cargado" le sigue enseguida el de "nada que reproducir".
        await EsperarHasta(() => { lock (avisos) return avisos.Count >= 1; });
    }

    [Fact]
    public async Task EnElAvisoDeApertura_YaSeConocenElTamanoElCodecYLaDuracion()
    {
        // El reproductor los lee en el propio aviso (para preparar los subtítulos): el decodificador aún no ha arrancado y
        // libmpv todavía no da "width"/"height", así que salen de la pista de video del contenedor. La duración la necesita el
        // salto de reanudación, que se hace con el primer fotograma: si aún valía 0, el episodio se destapaba en el 0:00.
        if (!AnimeLocalTracker.Services.Native.LibMpv.Disponible) return;
        var motor = MotorMpv.Crear(Ajustes, sinSalida: true)!;
        using var _ = motor;
        (int, int) tamano = (-1, -1);
        string? codec = "sin leer";
        TimeSpan duracion = TimeSpan.Zero;
        var avisado = new TaskCompletionSource();
        motor.AperturaTerminada += (_, _) => { tamano = motor.TamanoVideo; codec = motor.CodecVideo; duracion = motor.Duracion; avisado.TrySetResult(); };

        motor.Abrir(_video.Ruta);
        await avisado.Task.WaitAsync(TimeSpan.FromSeconds(15));

        tamano.Should().Be((320, 180));
        codec.Should().Be("h264");
        duracion.TotalSeconds.Should().BeApproximately(6, 0.5);
    }

    [Fact]
    public async Task PausarSaltarYLlegarAlFinal()
    {
        if (!AnimeLocalTracker.Services.Native.LibMpv.Disponible) return;
        var (motor, avisos) = Crear();
        using var _ = motor;
        motor.Abrir(_video.Ruta);
        await EsperarHasta(() => avisos.Count == 1 && motor.Estado == EstadoMotor.Reproduciendo);

        motor.Pausar();
        await EsperarHasta(() => motor.Estado == EstadoMotor.EnPausa);
        long antes = motor.FotogramasMostrados;

        motor.Saltar(TimeSpan.FromSeconds(3.2), exacto: true);
        await EsperarHasta(() => Math.Abs(motor.Posicion.TotalSeconds - 3.2) < 0.1);
        await EsperarHasta(() => motor.FotogramasMostrados > antes + 1);
        motor.Estado.Should().Be(EstadoMotor.EnPausa, "saltar no reanuda");

        motor.Saltar(TimeSpan.FromSeconds(4.6), exacto: false);
        await EsperarHasta(() => motor.Posicion.TotalSeconds is > 3.9 and < 5.1); // al fotograma clave cercano (hay uno por segundo)

        motor.Velocidad = 2;
        motor.Reproducir();
        await EsperarHasta(() => motor.Estado == EstadoMotor.Terminado);
        motor.Posicion.TotalSeconds.Should().BeApproximately(6, 0.5);

        motor.Saltar(TimeSpan.Zero, exacto: false); // "reproducir de nuevo" tras el final
        motor.Reproducir();
        await EsperarHasta(() => motor.Estado == EstadoMotor.Reproduciendo && motor.Posicion.TotalSeconds < 3);
    }

    [Fact]
    public async Task CambiarDePistaDeAudio_AvisaComoUnaApertura_YNoReiniciaElVideo()
    {
        if (!AnimeLocalTracker.Services.Native.LibMpv.Disponible) return;
        var (motor, avisos) = Crear();
        using var _ = motor;
        motor.Abrir(_video.Ruta);
        await EsperarHasta(() => avisos.Count == 1 && motor.Posicion > TimeSpan.FromMilliseconds(300));

        motor.ElegirAudio(motor.PistasAudio[1]);

        await EsperarHasta(() => avisos.Count == 2); // el reproductor cuenta con este aviso para restaurar volumen y silencio
        avisos[1].Should().Be(new AperturaTerminada(ClaseApertura.Video, true, null));
        motor.IndicePistaAudioActual.Should().Be(motor.PistasAudio[1].Indice);
        motor.Estado.Should().Be(EstadoMotor.Reproduciendo);
    }

    [Fact]
    public async Task LosSubtitulosDelMotor_SeEnciendenYSeApagan()
    {
        if (!AnimeLocalTracker.Services.Native.LibMpv.Disponible) return;
        var (motor, avisos) = Crear();
        using var _ = motor;
        motor.Abrir(_video.Ruta);
        await EsperarHasta(() => avisos.Count == 1);
        var pista = motor.PistasSubtitulos[0];

        motor.MostrarSubtitulosDelMotor(pista);
        await EsperarHasta(() => motor.IndicePistaSubtitulosDelMotor == pista.Indice);

        motor.ApagarSubtitulosDelMotor();
        await EsperarHasta(() => motor.IndicePistaSubtitulosDelMotor == -1);
        avisos.Should().HaveCount(1, "los subtítulos del motor no avisan: los dibuja él y la app no tiene que leer nada");
    }

    [Fact]
    public async Task UnArchivoQueNoExiste_AvisaDelFalloYSePuedeAbrirOtro()
    {
        if (!AnimeLocalTracker.Services.Native.LibMpv.Disponible) return;
        var (motor, avisos) = Crear();
        using var _ = motor;

        motor.Abrir(Path.Combine(_video.Carpeta, "no_existe.mkv"));

        await EsperarHasta(() => avisos.Count == 1);
        avisos[0].Exito.Should().BeFalse();
        avisos[0].Error.Should().NotBeNullOrWhiteSpace();
        motor.Estado.Should().Be(EstadoMotor.Fallido);

        motor.Abrir(_video.Ruta);
        await EsperarHasta(() => avisos.Count == 2 && motor.Estado == EstadoMotor.Reproduciendo);
        avisos[1].Exito.Should().BeTrue();
    }

    [Fact]
    public async Task UnaRutaConTildesYCorchetes_SeAbre()
    {
        if (!AnimeLocalTracker.Services.Native.LibMpv.Disponible) return;
        var (motor, avisos) = Crear();
        using var _ = motor;

        motor.Abrir(_video.RutaConTildes);

        await EsperarHasta(() => avisos.Count == 1);
        avisos[0].Exito.Should().BeTrue(avisos[0].Error);
    }

    [Fact]
    public async Task DetenerYVolverAAbrir()
    {
        if (!AnimeLocalTracker.Services.Native.LibMpv.Disponible) return;
        var (motor, avisos) = Crear();
        using var _ = motor;
        motor.Abrir(_video.Ruta);
        await EsperarHasta(() => avisos.Count == 1 && motor.Estado == EstadoMotor.Reproduciendo);

        motor.Detener();
        await EsperarHasta(() => motor.Estado == EstadoMotor.Detenido);

        motor.Abrir(_video.Ruta);
        await EsperarHasta(() => avisos.Count == 2 && motor.Estado == EstadoMotor.Reproduciendo);
    }

    [Fact]
    public async Task DesecharEnPlenaReproduccion_NoSeCuelga()
    {
        if (!AnimeLocalTracker.Services.Native.LibMpv.Disponible) return;
        var (motor, avisos) = Crear();
        motor.Abrir(_video.Ruta);
        await EsperarHasta(() => avisos.Count == 1);

        var cierre = Task.Run(() => { motor.Dispose(); motor.Dispose(); });

        (await Task.WhenAny(cierre, Task.Delay(10000))).Should().Be(cierre, "desechar no debe colgarse");
        motor.Desechado.Should().BeTrue();
        motor.Estado.Should().Be(EstadoMotor.Detenido);
        motor.Abrir(_video.Ruta); // ya desechado: no hace nada ni lanza
        _ = motor.Posicion; // tras desechar conserva la última posición conocida: preguntar no lanza
    }

    [Fact]
    public void Volumen_Velocidad_YSilencio_NoLanzanSinArchivo()
    {
        if (!AnimeLocalTracker.Services.Native.LibMpv.Disponible) return;
        var (motor, _) = Crear();
        using var __ = motor;

        motor.Volumen = 40;
        motor.Silencio = true;
        motor.Velocidad = 1.5;
        motor.AceleracionPorHardware = false;
        motor.Saltar(TimeSpan.FromSeconds(10), exacto: true);
        motor.Pausar();
        motor.Reproducir();

        motor.PistasAudio.Should().BeEmpty();
        motor.CapturarFotograma().Should().BeNull();
    }

    // ── Etapa 3: subtítulos, filtros de audio, captura y tarjeta ──

    internal const string ModoNoche = "threshold=0.089:ratio=9:attack=200:release=1000:makeup=2";

    internal static List<double> Ganancias(double banda0)
    {
        var ganancias = Enumerable.Repeat(0d, EcualizadorAudio.Frecuencias.Count).ToList();
        ganancias[0] = banda0;
        return ganancias;
    }

    private async Task<(MotorMpv Motor, List<AperturaTerminada> Avisos)> CrearYAbrir()
    {
        var (motor, avisos) = Crear();
        motor.Abrir(_video.Ruta);
        await EsperarHasta(() => { lock (avisos) return avisos.Count == 1; });
        return (motor, avisos);
    }

    [Fact]
    public async Task MostrarLaPistaDeTexto_LaDibujaElMotorSinAvisoYConElEstiloDado()
    {
        if (!AnimeLocalTracker.Services.Native.LibMpv.Disponible) return;
        var (motor, avisos) = await CrearYAbrir();
        using var _ = motor;
        var pista = motor.PistasSubtitulos.Single();
        motor.DibujaSubtitulosDeTexto.Should().BeTrue();

        motor.AplicarEstiloSubtitulos(new EstiloSubtitulos { Tamano = 60, ColorTexto = "#FFE14D" }, tambienEnAss: false);
        motor.MostrarSubtitulosDelMotor(pista);

        motor.IndicePistaSubtitulosDelMotor.Should().Be(pista.Indice);
        var esperado = MotorMpv.OpcionesDeEstilo(new EstiloSubtitulos { Tamano = 60 }, forzar: true).Single(o => o.Nombre == "sub-font-size").Valor;
        double.Parse(motor.LeerPropiedad("sub-font-size"), CultureInfo.InvariantCulture)
            .Should().BeApproximately(double.Parse(esperado, CultureInfo.InvariantCulture), 0.01);
        motor.LeerPropiedad("sub-color").ToUpperInvariant().Should().EndWith("FFE14D");
        motor.LeerPropiedad("sub-ass-override").Should().Be("strip", "en una pista de texto plano el estilo siempre es el de Configuración");

        motor.Pausar();
        motor.Saltar(TimeSpan.FromSeconds(1.5), exacto: true);
        await EsperarHasta(() => motor.LeerPropiedad("sub-text").Contains("Hola"));
        lock (avisos) avisos.Should().HaveCount(1, "mostrar una pista no es abrir nada: la app no tiene nada que leer");
    }

    [Fact]
    public async Task ApagarLosSubtitulos_DejaElMotorSinPista()
    {
        if (!AnimeLocalTracker.Services.Native.LibMpv.Disponible) return;
        var (motor, _) = await CrearYAbrir();
        using var __ = motor;
        motor.Pausar();
        motor.Saltar(TimeSpan.FromSeconds(1.5), exacto: true);
        motor.MostrarSubtitulosDelMotor(motor.PistasSubtitulos.Single());
        await EsperarHasta(() => motor.LeerPropiedad("sub-text").Contains("Hola"));

        motor.ApagarSubtitulosDelMotor();

        motor.IndicePistaSubtitulosDelMotor.Should().Be(-1);
        await EsperarHasta(() => motor.LeerPropiedad("sub-text").Length == 0);
    }

    [Fact]
    public void ColocarSubtitulos_LosSubeYLosDevuelveASuSitio_SinPasarseDeLaMitad()
    {
        using var motor = MotorMpv.Crear(Ajustes, sinSalida: true);
        if (motor == null) return; // sin la biblioteca en esta máquina
        static double Leida(MotorMpv m) => double.Parse(m.LeerPropiedad("sub-pos"), CultureInfo.InvariantCulture);

        motor.ColocarSubtitulos(88.4);
        Leida(motor).Should().BeApproximately(88.4, 0.01, "con los controles a la vista");

        motor.ColocarSubtitulos(20);
        Leida(motor).Should().Be(50, "una ventana diminuta no los manda al techo");

        motor.ColocarSubtitulos(100);
        Leida(motor).Should().Be(100, "al ocultarse los controles vuelven a su sitio");
    }

    [Fact]
    public async Task AbrirOtroArchivo_EmpiezaSinSubtitulosDelMotor()
    {
        // libmpv conserva la pista elegida de un archivo al siguiente. El reproductor elige pista en cada episodio solo si el motor
        // no trae ya una: sin apagarla, el episodio siguiente se quedaba con el número de pista del anterior (otro idioma si el
        // archivo las ordena distinto) y el menú perdía «Usar mi estilo».
        if (!AnimeLocalTracker.Services.Native.LibMpv.Disponible) return;
        var (motor, avisos) = await CrearYAbrir();
        using var _ = motor;
        motor.MostrarSubtitulosDelMotor(motor.PistasSubtitulos.Single());
        motor.IndicePistaSubtitulosDelMotor.Should().BeGreaterThanOrEqualTo(0);

        motor.Abrir(_video.RutaConTildes);
        await EsperarHasta(() => { lock (avisos) return avisos.Count == 2; });

        motor.IndicePistaSubtitulosDelMotor.Should().Be(-1);
    }

    [Fact]
    public async Task AlCargarOtroArchivo_LoApuntadoDelAnteriorDejaDeValer()
    {
        // Visto en la app al pasar solo al episodio siguiente: un aviso de libmpv del archivo que terminó llegaba después de que
        // Abrir pusiera la posición a cero, y el motor seguía diciendo "30,00 s" con el archivo nuevo ya cargado (y libmpv en 0).
        // El reproductor mira la posición en cuanto el motor dice que reproduce: si caía en ese instante, daba el episodio nuevo
        // por visto nada más empezar.
        if (!AnimeLocalTracker.Services.Native.LibMpv.Disponible) return;
        var (motor, _) = await CrearYAbrir();
        using var __ = motor;
        motor.Saltar(TimeSpan.FromSeconds(5), exacto: true);
        motor.Reproducir();
        await EsperarHasta(() => motor.Estado == EstadoMotor.Terminado);
        double alAvisar = -1;
        EstadoMotor estadoAlAvisar = default;
        motor.AperturaTerminada += (_, _) => { estadoAlAvisar = motor.Estado; alAvisar = motor.Posicion.TotalSeconds; };

        motor.Abrir(_video.RutaConTildes);
        motor.SimularAvisoTardio(TimeSpan.FromSeconds(6), fin: true); // el aviso rezagado del archivo anterior
        await EsperarHasta(() => alAvisar >= 0);

        alAvisar.Should().BeLessThan(1, "en el aviso de apertura la posición ya es la del archivo nuevo");
        estadoAlAvisar.Should().NotBe(EstadoMotor.Terminado, "un archivo recién cargado no ha terminado");
    }

    [Fact]
    public async Task MostrarUnaPistaQueNoEsDeEsteArchivo_NoHaceNada()
    {
        if (!AnimeLocalTracker.Services.Native.LibMpv.Disponible) return;
        var (motor, _) = await CrearYAbrir();
        using var __ = motor;

        motor.MostrarSubtitulosDelMotor(new PistaSubtitulosMotor(99, "en", null, EsImagen: false, EsAss: true, RutaExterna: null));

        motor.IndicePistaSubtitulosDelMotor.Should().Be(-1);
    }

    [Fact]
    public async Task MontarElEcualizador_YCambiarUnaBandaEnVivo()
    {
        if (!AnimeLocalTracker.Services.Native.LibMpv.Disponible) return;
        var (motor, _) = await CrearYAbrir();
        using var __ = motor;
        await EsperarHasta(() => motor.Posicion > TimeSpan.FromMilliseconds(300));

        motor.AplicarFiltrosAudio(EcualizadorAudio.ConstruirFiltros(true, Ganancias(banda0: 3), true, ModoNoche)).Should().BeTrue();

        motor.LeerPropiedad("af").Should().Contain("equalizer@eq_b0=f=31:t=o:w=1:g=3.0").And.Contain("acompressor@");
        // El grafo se monta con el siguiente trozo de audio: hasta entonces no hay filtro al que mandarle nada.
        await EsperarHasta(() => motor.CambiarFiltroEnVivo(EcualizadorAudio.IdBanda(0), "g", "6.0"), 5000);
        motor.CambiarFiltroEnVivo(EcualizadorAudio.IdPreamplificador, "volume", "-6.0dB").Should().BeTrue();
        var antes = motor.Posicion;
        await EsperarHasta(() => motor.Posicion > antes, 3000);
    }

    [Fact]
    public async Task CambiarUnFiltroQueNoEstaMontado_DevuelveFalse()
    {
        if (!AnimeLocalTracker.Services.Native.LibMpv.Disponible) return;
        var (motor, avisos) = Crear();
        using var _ = motor;
        motor.CambiarFiltroEnVivo(EcualizadorAudio.IdBanda(0), "g", "6.0").Should().BeFalse("motor recién creado, sin archivo ni cadena");

        motor.Abrir(_video.Ruta);
        await EsperarHasta(() => { lock (avisos) return avisos.Count == 1; });
        motor.CambiarFiltroEnVivo(EcualizadorAudio.IdBanda(0), "g", "6.0").Should().BeFalse("ecualizador apagado");

        motor.AplicarFiltrosAudio(EcualizadorAudio.ConstruirFiltros(true, Ganancias(banda0: 3), false, ModoNoche));
        motor.CambiarFiltroEnVivo("no_existe", "g", "6.0").Should().BeFalse();
    }

    [Fact]
    public async Task LosFiltrosRecordados_SeMontanAlAbrirElSiguienteArchivo()
    {
        if (!AnimeLocalTracker.Services.Native.LibMpv.Disponible) return;
        var (motor, avisos) = await CrearYAbrir();
        using var _ = motor;
        motor.AplicarFiltrosAudio(EcualizadorAudio.ConstruirFiltros(true, Ganancias(banda0: 3), false, ModoNoche));

        motor.RecordarFiltrosAudio(EcualizadorAudio.ConstruirFiltros(true, Ganancias(banda0: 9), false, ModoNoche));
        motor.LeerPropiedad("af").Should().Contain("g=3.0", "recordar no remonta la cadena que suena");

        motor.Abrir(_video.RutaConTildes);
        await EsperarHasta(() => { lock (avisos) return avisos.Count == 2; });
        motor.LeerPropiedad("af").Should().Contain("equalizer@eq_b0=f=31:t=o:w=1:g=9.0");
    }

    [Fact]
    public void LosFiltrosDeLosAjustesIniciales_QuedanMontados_YQuitarlosVaciaLaCadena()
    {
        if (!AnimeLocalTracker.Services.Native.LibMpv.Disponible) return;
        var iniciales = EcualizadorAudio.ConstruirFiltros(true, Ganancias(banda0: 3), false, ModoNoche);
        using var motor = MotorMpv.Crear(new AjustesInicialesMotor(false, null, iniciales), sinSalida: true)!;

        motor.LeerPropiedad("af").Should().Contain("equalizer@eq_b0");

        motor.AplicarFiltrosAudio([]).Should().BeTrue();
        motor.LeerPropiedad("af").Should().BeEmpty();
    }

    private static string[] CapturasTemporales() => Directory.GetFiles(Path.GetTempPath(), "animelocaltracker-captura-*.png");

    [Fact]
    public async Task CapturarUnFotograma_DaLaImagenDelVideoYNoDejaTemporales()
    {
        if (!AnimeLocalTracker.Services.Native.LibMpv.Disponible) return;
        var (motor, _) = await CrearYAbrir();
        using var __ = motor;
        await EsperarHasta(() => motor.Posicion > TimeSpan.FromMilliseconds(300));

        var imagen = motor.CapturarFotograma();

        imagen.Should().NotBeNull();
        (imagen!.PixelWidth, imagen.PixelHeight).Should().Be((320, 180));
        imagen.IsFrozen.Should().BeTrue("se pasa al portapapeles y a otro hilo para guardarla");
        CapturasTemporales().Should().BeEmpty();
    }

    [Fact]
    public void CapturarSinArchivo_DevuelveNullYNoDejaTemporales()
    {
        if (!AnimeLocalTracker.Services.Native.LibMpv.Disponible) return;
        var (motor, _) = Crear();

        motor.CapturarFotograma().Should().BeNull("no hay nada abierto");
        motor.Dispose();
        motor.CapturarFotograma().Should().BeNull("motor desechado");
        CapturasTemporales().Should().BeEmpty();
    }

    [Fact]
    public void TarjetaDetectada_SeVeEnTarjetaEnUso()
    {
        if (!AnimeLocalTracker.Services.Native.LibMpv.Disponible) return;
        var (motor, _) = Crear();
        using var __ = motor;
        motor.TarjetaEnUso.Should().BeNull("sin superficie no se sabe qué tarjeta pinta");

        motor.TarjetaDetectada("  Intel(R) HD Graphics 620 ", "Intel");
        motor.TarjetaEnUso.Should().Be(new TarjetaGrafica("Intel(R) HD Graphics 620", FabricanteGrafica.Intel, 0));

        motor.TarjetaDetectada("  ", null);
        motor.TarjetaEnUso!.Nombre.Should().Be("Intel(R) HD Graphics 620", "sin nombre no cambia nada");
    }

    // ── Etapa 4: Anime4K ──

    [Fact]
    public void EncenderElEscalado_CargaLosTresShadersDelNivelLigero_YApagarloLosQuita()
    {
        if (!AnimeLocalTracker.Services.Native.LibMpv.Disponible) return;
        var (motor, _) = Crear();
        using var __ = motor;
        motor.ClaseDeEscalado.Should().Be(ClaseEscalado.PorSombreadores);
        motor.EscaladoInteligente.Should().BeFalse();

        motor.EscaladoInteligente = true;

        motor.EscaladoInteligente.Should().BeTrue();
        string cargados = motor.LeerPropiedad("glsl-shaders");
        int a = cargados.IndexOf("Anime4K_Clamp_Highlights.glsl", StringComparison.Ordinal);
        int b = cargados.IndexOf("Anime4K_Restore_CNN_S.glsl", StringComparison.Ordinal);
        int c = cargados.IndexOf("Anime4K_Upscale_CNN_x2_S.glsl", StringComparison.Ordinal);
        (a >= 0 && a < b && b < c).Should().BeTrue($"los tres, en orden: {cargados}");

        motor.EscaladoInteligente = false;

        motor.EscaladoInteligente.Should().BeFalse();
        motor.LeerPropiedad("glsl-shaders").Should().BeEmpty();
    }

    [Fact]
    public void ConTarjetaNvidia_ElNivelEsElMedio()
    {
        if (!AnimeLocalTracker.Services.Native.LibMpv.Disponible) return;
        var (motor, _) = Crear();
        using var __ = motor;
        motor.TarjetaDetectada("GeForce RTX 3060/PCIe/SSE2", "NVIDIA Corporation");

        motor.EscaladoInteligente = true;

        motor.LeerPropiedad("glsl-shaders").Should().Contain("Anime4K_Restore_CNN_M.glsl").And.Contain("Anime4K_Upscale_CNN_x2_M.glsl");
    }

    [Fact]
    public void SinLosArchivos_ElEscaladoNoSeEnciende()
    {
        // Instalación incompleta: mejor sin escalado que con medio juego de shaders, y sin lanzar.
        if (!AnimeLocalTracker.Services.Native.LibMpv.Disponible) return;
        var (motor, _) = Crear();
        using var __ = motor;
        motor.CarpetaDeSombreadores = _video.Carpeta;

        motor.EscaladoInteligente = true;

        motor.EscaladoInteligente.Should().BeFalse();
        motor.LeerPropiedad("glsl-shaders").Should().BeEmpty();
    }

    [Fact]
    public void LosFotogramasPerdidos_SinArchivoSonCero()
    {
        if (!AnimeLocalTracker.Services.Native.LibMpv.Disponible) return;
        var (motor, _) = Crear();
        using var __ = motor;

        motor.FotogramasPerdidos.Should().Be(0);
    }
}
