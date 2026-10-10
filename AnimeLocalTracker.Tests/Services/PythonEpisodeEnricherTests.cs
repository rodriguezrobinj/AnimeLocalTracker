using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services.Python;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

public class PythonEpisodeEnricherTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "AnimeLocalTrackerTests_" + Guid.NewGuid());

    public PythonEpisodeEnricherTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string CrearArchivo(byte[] contenido)
    {
        string path = Path.Combine(_tempDir, Guid.NewGuid() + ".jpg");
        File.WriteAllBytes(path, contenido);
        return path;
    }

    private static byte[] JpegValidoFalso(int relleno = 2000)
    {
        // SOI (FFD8FF...) + relleno + EOI (FFD9) — suficiente para pasar la validación
        // sin necesitar un JPEG real decodificable.
        var bytes = new byte[3 + relleno + 2];
        bytes[0] = 0xFF; bytes[1] = 0xD8; bytes[2] = 0xFF;
        bytes[^2] = 0xFF; bytes[^1] = 0xD9;
        return bytes;
    }

    [Fact]
    public void EsMiniaturaValida_ConJpegCompleto_DeberiaSerValida()
    {
        string path = CrearArchivo(JpegValidoFalso());
        PythonEpisodeEnricher.EsMiniaturaValida(path).Should().BeTrue();
    }

    [Fact]
    public void EsMiniaturaValida_ConArchivoInexistente_DeberiaSerInvalida()
    {
        string path = Path.Combine(_tempDir, "no_existe.jpg");
        PythonEpisodeEnricher.EsMiniaturaValida(path).Should().BeFalse();
    }

    [Fact]
    public void EsMiniaturaValida_ConArchivoVacio_DeberiaSerInvalida()
    {
        string path = CrearArchivo(Array.Empty<byte>());
        PythonEpisodeEnricher.EsMiniaturaValida(path).Should().BeFalse();
    }

    [Fact]
    public void EsMiniaturaValida_ConJpegTruncado_DeberiaSerInvalida()
    {
        // BUG-02: proceso ffmpeg interrumpido a medias — tiene el SOI inicial pero el
        // archivo se corta antes del marcador EOI final. Este es exactamente el patrón
        // observado en miniaturas de ~900-1500 bytes que quedaban "atascadas" sin
        // regenerarse nunca, ni siquiera reintentando desde "Actualizar".
        var truncado = new byte[900];
        truncado[0] = 0xFF; truncado[1] = 0xD8; truncado[2] = 0xFF;
        // El resto queda en 0x00 — sin marcador EOI válido al final.
        string path = CrearArchivo(truncado);

        PythonEpisodeEnricher.EsMiniaturaValida(path).Should().BeFalse();
    }

    [Fact]
    public void EsMiniaturaValida_ConArchivoDemasiadoPequeno_DeberiaSerInvalida()
    {
        // Aunque tenga los marcadores correctos, una miniatura real de 320px nunca pesa
        // unos pocos bytes.
        var minusculo = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
        string path = CrearArchivo(minusculo);

        PythonEpisodeEnricher.EsMiniaturaValida(path).Should().BeFalse();
    }

    /// <summary>Video sintético (fuente lavfi de ffmpeg) con el ffmpeg embebido.</summary>
    private async Task<string> CrearVideoAsync(string fuente)
    {
        string video = Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".mp4");
        var resultado = await AnimeLocalTracker.Core.ProcesoExterno.EjecutarAsync(AnimeLocalTracker.Services.FfmpegLocator.Ffmpeg,
            ["-y", "-loglevel", "error", "-f", "lavfi", "-i", fuente, "-c:v", "mpeg4", video],
            TimeSpan.FromSeconds(60), CancellationToken.None);
        resultado!.Codigo.Should().Be(0, resultado.Error);
        return video;
    }

    [Fact]
    public async Task ExtraerMiniatura_SacaUnJpegCompletoDelAnchoDeSiempre()
    {
        string video = await CrearVideoAsync("testsrc2=s=1280x720:r=10:d=4");
        string miniatura = Path.Combine(_tempDir, "miniatura.jpg");

        (await PythonEpisodeEnricher.ExtraerMiniaturaAsync(video, miniatura)).Should().BeTrue();

        PythonEpisodeEnricher.EsMiniaturaValida(miniatura).Should().BeTrue();
        PythonEpisodeEnricher.EsFrameDemasiadoVacio(miniatura).Should().BeFalse();
        var medidas = await AnimeLocalTracker.Core.ProcesoExterno.EjecutarAsync(AnimeLocalTracker.Services.FfmpegLocator.Ffprobe,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "csv=p=0", miniatura],
            TimeSpan.FromSeconds(30), CancellationToken.None);
        medidas!.Salida.Trim().Should().Be("512,288", "las miniaturas ya guardadas miden eso y deben seguir viéndose iguales");
        Directory.GetFiles(_tempDir, "miniatura.jpg*").Should().ContainSingle("el archivo de trabajo no debe quedar junto a la miniatura");
    }

    [Fact]
    public async Task ExtraerMiniatura_SiLosInstantesSiguientesNoDanFotograma_ConservaElQueYaSaco()
    {
        // Video negro de 4 s: el fotograma del segundo 2 sale (casi vacío: se busca uno mejor) y los de los segundos 10 y 30 caen
        // más allá del final. Antes, cada intento fallido borraba el archivo: quedaba "generada" una miniatura que no existía.
        string video = await CrearVideoAsync("color=c=black:s=640x360:r=10:d=4");
        string miniatura = Path.Combine(_tempDir, "negra.jpg");

        (await PythonEpisodeEnricher.ExtraerMiniaturaAsync(video, miniatura)).Should().BeTrue();

        PythonEpisodeEnricher.EsMiniaturaValida(miniatura).Should().BeTrue();
    }

    [Fact]
    public async Task ExtraerMiniatura_SiElVideoNoExiste_NoCreaNada()
    {
        string miniatura = Path.Combine(_tempDir, "sin_video.jpg");

        (await PythonEpisodeEnricher.ExtraerMiniaturaAsync(Path.Combine(_tempDir, "no_existe.mkv"), miniatura)).Should().BeFalse();

        File.Exists(miniatura).Should().BeFalse();
    }

    [Fact]
    public async Task ExtraerMiniatura_SiElArchivoNoEsUnVideo_NoDejaUnaMiniaturaRota()
    {
        string falso = Path.Combine(_tempDir, "falso.mp4");
        await File.WriteAllBytesAsync(falso, new byte[64]);
        string miniatura = Path.Combine(_tempDir, "falsa.jpg");

        (await PythonEpisodeEnricher.ExtraerMiniaturaAsync(falso, miniatura)).Should().BeFalse();

        Directory.GetFiles(_tempDir, "falsa.jpg*").Should().BeEmpty();
    }

    [Fact]
    public void LeerDatosTecnicos_TomaResolucionCodecFpsYProfundidadDeLaPistaDeVideo()
    {
        string json = """{"streams":[{"codec_name":"hevc","width":1920,"height":1080,"pix_fmt":"yuv420p10le","r_frame_rate":"24000/1001"}]}""";

        PythonEpisodeEnricher.LeerDatosTecnicos(json)
            .Should().Be(new PythonEpisodeEnricher.DatosTecnicos("1920x1080", "hevc", "24000/1001", true));
    }

    [Theory]
    [InlineData("""{"streams":[]}""")]
    [InlineData("{}")]
    [InlineData("")]
    [InlineData("esto no es JSON")]
    public void LeerDatosTecnicos_SinPistaDeVideoOSalidaIlegible_DevuelveNull(string salida)
    {
        PythonEpisodeEnricher.LeerDatosTecnicos(salida).Should().BeNull();
    }

    [Fact]
    public async Task EnriquecerEpisodio_LeeLosDatosConFfprobeSinPasarPorElDaemon()
    {
        string video = await CrearVideoAsync("color=c=black:s=64x64:r=10:d=1");
        var episodio = new EpisodioItem { RutaCompleta = video };

        // Puente estricto: cualquier llamada al daemon haría fallar la prueba.
        await new PythonEpisodeEnricher(new Mock<IPythonBridgeService>(MockBehavior.Strict).Object).EnriquecerEpisodioAsync(episodio);

        episodio.Resolucion.Should().Be("64x64");
        episodio.CodecVideo.Should().Be("mpeg4");
        episodio.Fps.Should().Be("10/1");
        episodio.Es10Bit.Should().BeFalse();
    }

    [Fact]
    public async Task EnriquecerEpisodio_SiElArchivoNoExiste_NoTocaElEpisodio()
    {
        var episodio = new EpisodioItem { RutaCompleta = Path.Combine(_tempDir, "no_existe.mkv") };

        await new PythonEpisodeEnricher(new Mock<IPythonBridgeService>(MockBehavior.Strict).Object).EnriquecerEpisodioAsync(episodio);

        episodio.Resolucion.Should().BeEmpty();
    }

    [Fact]
    public void EnLasPruebas_LasMiniaturasNoVanALaCarpetaDeDatosDelUsuario()
    {
        // Las pruebas que crean miniaturas (esta clase y la ficha) las dejaban en Thumbnails real: una falsa por corrida.
        // TestInitializer apunta la carpeta de miniaturas a una temporal para toda la suite.
        PythonEpisodeEnricher.ObtenerRutaMiniaturaEsperada(@"C:\Anime\Falso\Episodio 01.mp4")
            .Should().NotStartWith(AnimeLocalTracker.Services.AppDataPaths.DataRoot);
    }

    [Fact]
    public void ObtenerRutaMiniaturaSiExiste_ConMiniaturaTruncadaCacheada_DeberiaEliminarlaYDevolverNull()
    {
        // Simula el escenario reportado: una miniatura corrupta ya escrita en el caché
        // determinista (SHA-256 de la ruta del video) debe descartarse automáticamente,
        // no quedarse "atascada" para siempre.
        string rutaVideoFalsa = @"C:\Anime\Falso\Episodio 07.mp4";
        string rutaCache = PythonEpisodeEnricher.ObtenerRutaMiniaturaEsperada(rutaVideoFalsa);
        Directory.CreateDirectory(Path.GetDirectoryName(rutaCache)!);

        var truncado = new byte[900];
        truncado[0] = 0xFF; truncado[1] = 0xD8; truncado[2] = 0xFF;
        File.WriteAllBytes(rutaCache, truncado);

        try
        {
            var resultado = PythonEpisodeEnricher.ObtenerRutaMiniaturaSiExiste(rutaVideoFalsa);

            resultado.Should().BeNull();
            File.Exists(rutaCache).Should().BeFalse("la miniatura corrupta debe borrarse para que se regenere");
        }
        finally
        {
            try { File.Delete(rutaCache); } catch { }
        }
    }
}
