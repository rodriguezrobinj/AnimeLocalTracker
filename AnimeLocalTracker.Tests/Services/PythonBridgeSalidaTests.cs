using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Python;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// El resultado de un comando de un solo uso es la ÚLTIMA línea JSON de la salida del daemon. Antes se interpretaba toda la salida como
/// un único JSON: si el comando escribía algo más antes (el progreso de yt-dlp en una descarga HLS: ~1800 líneas) fallaba y la app lo
/// daba por "respuesta vacía del daemon".
/// </summary>
public class PythonBridgeSalidaTests
{
    private const string Resultado = """{"success": true, "file": "C:\\video.mp4", "title": "master"}""";

    [Fact]
    public void UnaSolaLineaJson_SeInterpretaComoSiempre()
    {
        var r = PythonBridgeService.InterpretarSalida<DownloadService.DownloadStreamResult>("""{"success": true, "ruta_archivo": "C:\\video.mp4"}""");

        r.Should().NotBeNull();
        r!.Success.Should().BeTrue();
        r.RutaArchivo.Should().Be("C:\\video.mp4");
    }

    [Fact]
    public void ConLineasDeProgresoAntes_TomaLaUltimaLineaJson()
    {
        string salida = "\n[download]   0.0% of ~ 248.02MiB at    2.74KiB/s ETA Unknown (frag 0/147)\n"
                        + "[download]  45.3% of ~ 287.00MiB at   11.2MiB/s ETA 00:12 (frag 66/147)\n"
                        + Resultado + "\n";

        var r = PythonBridgeService.InterpretarSalida<ProveedorVideoAnimeAv1.StreamResult>(salida);

        r.Should().NotBeNull();
        r!.Success.Should().BeTrue();
        r.Title.Should().Be("master");
    }

    [Fact]
    public void ConLineasEnBlancoAlFinal_TambienFunciona()
    {
        var r = PythonBridgeService.InterpretarSalida<ProveedorVideoAnimeAv1.StreamResult>("aviso suelto\n" + Resultado + "\r\n\r\n   \n");

        r.Should().NotBeNull();
        r!.Success.Should().BeTrue();
    }

    [Fact]
    public void SiLaUltimaLineaJsonEsUnError_SeDevuelveElError()
    {
        var r = PythonBridgeService.InterpretarSalida<ProveedorVideoAnimeAv1.StreamResult>("[download] 3%\n{\"success\": false, \"error\": \"HTTP 403\"}\n");

        r!.Success.Should().BeFalse();
        r.Error.Should().Be("HTTP 403");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  ")]
    [InlineData("esto no es json\notra línea")]
    [InlineData("{ json roto")]
    public void SinJsonUtilizable_DevuelveNulo(string salida) =>
        PythonBridgeService.InterpretarSalida<ProveedorVideoAnimeAv1.StreamResult>(salida).Should().BeNull();
}
