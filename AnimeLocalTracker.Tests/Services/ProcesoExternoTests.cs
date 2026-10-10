using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Core;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>Con cmd.exe en vez de ffmpeg: lo que se prueba es lanzar, recoger las dos salidas y cortar a tiempo.</summary>
public class ProcesoExternoTests
{
    private static readonly string[] Saluda = ["/c", "echo hola"];
    private static readonly string[] FallaConAviso = ["/c", "echo fallo 1>&2 & exit 3"];
    private static readonly string[] TardaMucho = ["/c", "ping -n 30 127.0.0.1 > nul"];

    [Fact]
    public async Task EjecutarAsync_DevuelveElCodigoYLaSalida()
    {
        var resultado = await ProcesoExterno.EjecutarAsync("cmd.exe", Saluda, TimeSpan.FromSeconds(20), CancellationToken.None);

        resultado.Should().NotBeNull();
        resultado!.Codigo.Should().Be(0);
        resultado.Salida.Trim().Should().Be("hola");
    }

    [Fact]
    public async Task EjecutarAsync_RecogeLaSalidaDeErrorYElCodigoDeFallo()
    {
        var resultado = await ProcesoExterno.EjecutarAsync("cmd.exe", FallaConAviso, TimeSpan.FromSeconds(20), CancellationToken.None);

        resultado!.Codigo.Should().Be(3);
        resultado.Error.Should().Contain("fallo");
    }

    [Fact]
    public async Task EjecutarAsync_SiVenceElLimite_CortaElProcesoYLanzaCancelacion()
    {
        var reloj = Stopwatch.StartNew();

        Func<Task> accion = () => ProcesoExterno.EjecutarAsync("cmd.exe", TardaMucho, TimeSpan.FromMilliseconds(300), CancellationToken.None);

        await accion.Should().ThrowAsync<OperationCanceledException>();
        reloj.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "no debe esperar los 30 s del proceso");
    }

    [Fact]
    public async Task EjecutarAsync_SiSeCancela_LanzaCancelacion()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        Func<Task> accion = () => ProcesoExterno.EjecutarAsync("cmd.exe", TardaMucho, null, cts.Token);

        await accion.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task EjecutarBinario_DevuelveLosBytesDeLaSalidaSinTocarlos()
    {
        // 1 s de tono a 8 kHz en mono y f32: 8000 muestras × 4 bytes. Como texto se habría estropeado.
        string[] argumentos = ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=1",
            "-ac", "1", "-ar", "8000", "-f", "f32le", "-"];

        var resultado = await ProcesoExterno.EjecutarBinarioAsync(FfmpegLocator.Ffmpeg, argumentos, TimeSpan.FromSeconds(30), CancellationToken.None);

        resultado.Should().NotBeNull();
        resultado!.Codigo.Should().Be(0, resultado.Error);
        resultado.Salida.Length.Should().Be(32000);
    }

    [Fact]
    public async Task EjecutarBinario_SiVenceElLimite_MataElProcesoYLanzaCancelacion()
    {
        string[] esperaLarga = ["/c", "ping", "-n", "30", "127.0.0.1"];

        Func<Task> accion = () => ProcesoExterno.EjecutarBinarioAsync("cmd.exe", esperaLarga, TimeSpan.FromMilliseconds(300), CancellationToken.None);

        await accion.Should().ThrowAsync<OperationCanceledException>();
    }
}
