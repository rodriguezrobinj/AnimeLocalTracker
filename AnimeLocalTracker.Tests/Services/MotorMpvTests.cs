using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
        await EsperarHasta(() => avisos.Count == 1);
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
}
