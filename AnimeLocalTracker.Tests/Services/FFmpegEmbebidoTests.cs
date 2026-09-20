using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// ffmpeg.exe/ffprobe.exe embebidos: son builds "shared" (~0,8 MB) que cargan las DLLs de FFmpeg de la misma
/// carpeta (ver tools\Get-FFmpegBinaries.ps1, que las instala si faltan). Si las DLLs de la carpeta son de otra
/// versión mayor, o faltan, los exe no arrancan: estas pruebas lo detectan en el CI antes de publicar.
/// Sin los binarios descargados (clon sin ejecutar build.ps1) las pruebas se omiten, como RustNativeTests.
/// </summary>
public class FFmpegEmbebidoTests
{
    private static string Carpeta => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FFmpeg");

    private static (int Codigo, string Salida) Ejecutar(string exe, string argumentos)
    {
        var psi = new ProcessStartInfo(Path.Combine(Carpeta, exe), argumentos)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proceso = Process.Start(psi)!;
        var salida = proceso.StandardOutput.ReadToEndAsync();
        var error = proceso.StandardError.ReadToEndAsync();
        if (!proceso.WaitForExit(30_000))
        {
            proceso.Kill(entireProcessTree: true);
            throw new TimeoutException($"{exe} no terminó en 30 s");
        }
        return (proceso.ExitCode, salida.Result + error.Result);
    }

    private static bool HayBinarios => File.Exists(Path.Combine(Carpeta, "ffmpeg.exe")) && File.Exists(Path.Combine(Carpeta, "ffprobe.exe"));

    [Theory]
    [InlineData("ffmpeg.exe", "ffmpeg version")]
    [InlineData("ffprobe.exe", "ffprobe version")]
    public void Ejecutable_DeberiaArrancarConLasDllsDelPaquete(string exe, string textoEsperado)
    {
        if (!HayBinarios) return;

        var (codigo, salida) = Ejecutar(exe, "-version");

        codigo.Should().Be(0, $"{exe} debe encontrar las DLLs avcodec/avformat/... en su carpeta. Salida: {salida}");
        salida.Should().Contain(textoEsperado);
    }

    [Fact]
    public void Ejecutable_DeberiaUsarLasMismasVersionesMayoresQueLasDllsCargadas()
    {
        if (!HayBinarios) return;

        var (codigo, salida) = Ejecutar("ffmpeg.exe", "-version");
        codigo.Should().Be(0, salida);

        // "libavcodec     63.  1.101 / 63.  8.101" = versión con la que se compiló / versión de la DLL cargada.
        var bibliotecas = Regex.Matches(salida, @"^lib(?<n>\w+)\s+(?<c>\d+)\.\s*\d+\.\s*\d+\s*/\s*(?<r>\d+)\.", RegexOptions.Multiline)
            .Select(m => (Nombre: m.Groups["n"].Value, Compilada: m.Groups["c"].Value, Cargada: m.Groups["r"].Value))
            .ToList();

        bibliotecas.Should().NotBeEmpty("ffmpeg -version lista las bibliotecas libav*");
        bibliotecas.Should().OnlyContain(b => b.Compilada == b.Cargada,
            "el exe y las DLLs deben compartir la versión mayor (si no, hay que subir la versión fijada en tools\\Get-FFmpegBinaries.ps1)");
    }

    [Fact]
    public void Ejecutable_NoDeberiaSerLaBuildEstaticaPesada()
    {
        if (!HayBinarios) return;

        // Un ffmpeg.exe estático (~98 MB) lleva dentro otra copia de los códecs que ya están en las DLLs:
        // infla el instalador ~72 MB comprimidos. Si falla, ejecuta build.ps1 (descarga el build "shared").
        new FileInfo(Path.Combine(Carpeta, "ffmpeg.exe")).Length.Should().BeLessThan(5L * 1024 * 1024);
        new FileInfo(Path.Combine(Carpeta, "ffprobe.exe")).Length.Should().BeLessThan(5L * 1024 * 1024);
        Directory.GetFiles(Carpeta, "avcodec-*.dll").Should().NotBeEmpty("los exe shared necesitan las DLLs en la misma carpeta");
    }
}
