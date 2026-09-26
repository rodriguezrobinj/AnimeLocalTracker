using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>Lectura de la duración de un mp3 (ffprobe simulado: las pruebas no lanzan procesos ni tocan la carpeta de datos).</summary>
public class AudioDurationServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"AnimeTracker_Duracion_{Guid.NewGuid():N}");

    public AudioDurationServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(_dir, recursive: true); } catch { /* ignore */ }
    }

    private string CrearArchivo(string nombre = "tema.mp3", string contenido = "mp3")
    {
        string ruta = Path.Combine(_dir, nombre);
        File.WriteAllText(ruta, contenido);
        return ruta;
    }

    // === Interpretación de la salida de ffprobe ===

    [Theory]
    [InlineData("92.136000", 92.136)]
    [InlineData("92.136000\n", 92.136)]
    [InlineData("  90  \r\n", 90)]
    [InlineData("N/A\n88.5\n", 88.5)]
    [InlineData("1.5", 1.5)]
    public void Parsear_DeberiaLeerLosSegundosConPuntoDecimalSinDependerDelIdioma(string salida, double segundos)
    {
        AudioDurationService.Parsear(salida)!.Value.TotalSeconds.Should().BeApproximately(segundos, 1e-6);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("N/A")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("99999999")]
    public void Parsear_ConSalidaNoCreible_DeberiaDevolverNulo(string? salida)
    {
        AudioDurationService.Parsear(salida).Should().BeNull();
    }

    // === Servicio ===

    [Fact]
    public async Task Obtener_DeberiaDevolverLaDuracionQueDiceFfprobe()
    {
        var sut = new AudioDurationService((_, _) => Task.FromResult<string?>("92.136\n"));

        var d = await sut.ObtenerDuracionAsync(CrearArchivo(), CancellationToken.None);

        d!.Value.TotalSeconds.Should().BeApproximately(92.136, 1e-6);
    }

    [Fact]
    public async Task Obtener_SiElArchivoNoExiste_DeberiaDevolverNuloSinLanzarFfprobe()
    {
        int llamadas = 0;
        var sut = new AudioDurationService((_, _) => { llamadas++; return Task.FromResult<string?>("10"); });

        (await sut.ObtenerDuracionAsync(Path.Combine(_dir, "no-existe.mp3"), CancellationToken.None)).Should().BeNull();
        (await sut.ObtenerDuracionAsync("", CancellationToken.None)).Should().BeNull();

        llamadas.Should().Be(0);
    }

    [Fact]
    public async Task Obtener_LaSegundaVezDeberiaSalirDeLaCacheSinVolverALanzarFfprobe()
    {
        int llamadas = 0;
        var sut = new AudioDurationService((_, _) => { llamadas++; return Task.FromResult<string?>("60"); });
        string ruta = CrearArchivo();

        await sut.ObtenerDuracionAsync(ruta, CancellationToken.None);
        var segunda = await sut.ObtenerDuracionAsync(ruta, CancellationToken.None);

        segunda!.Value.TotalSeconds.Should().Be(60);
        llamadas.Should().Be(1);
    }

    [Fact]
    public async Task Obtener_SiElArchivoCambia_DeberiaVolverAMedirlo()
    {
        int llamadas = 0;
        var sut = new AudioDurationService((_, _) => { llamadas++; return Task.FromResult<string?>(llamadas == 1 ? "60" : "75"); });
        string ruta = CrearArchivo(contenido: "corto");
        await sut.ObtenerDuracionAsync(ruta, CancellationToken.None);

        File.WriteAllText(ruta, "contenido bastante mas largo que el anterior");
        var nueva = await sut.ObtenerDuracionAsync(ruta, CancellationToken.None);

        nueva!.Value.TotalSeconds.Should().Be(75);
        llamadas.Should().Be(2);
    }

    [Fact]
    public async Task Obtener_SiFfprobeFallaOImprimeBasura_DeberiaDevolverNuloYNoRecordarElFallo()
    {
        int llamadas = 0;
        var sut = new AudioDurationService((_, _) => { llamadas++; return Task.FromResult<string?>(llamadas == 1 ? null : "N/A"); });
        string ruta = CrearArchivo();

        (await sut.ObtenerDuracionAsync(ruta, CancellationToken.None)).Should().BeNull();
        (await sut.ObtenerDuracionAsync(ruta, CancellationToken.None)).Should().BeNull();

        llamadas.Should().Be(2, "un fallo no se guarda en la caché: podría ser algo pasajero");
    }

    [Fact]
    public async Task Obtener_SiLaEjecucionLanzaUnaExcepcion_DeberiaDevolverNulo()
    {
        var sut = new AudioDurationService((_, _) => throw new InvalidOperationException("ffprobe no está"));

        var d = await sut.ObtenerDuracionAsync(CrearArchivo(), CancellationToken.None);

        d.Should().BeNull();
    }

    [Fact]
    public async Task Obtener_CancelandoAntesDeEmpezar_DeberiaDevolverNulo()
    {
        var sut = new AudioDurationService((_, _) => Task.FromResult<string?>("60"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var d = await sut.ObtenerDuracionAsync(CrearArchivo(), cts.Token);

        d.Should().BeNull();
    }

    [Fact]
    public async Task Obtener_NoDeberiaLanzarMasDeDosFfprobeALaVez()
    {
        int enCurso = 0, maximo = 0;
        var sut = new AudioDurationService(async (_, ct) =>
        {
            int ahora = Interlocked.Increment(ref enCurso);
            int visto;
            while (ahora > (visto = Volatile.Read(ref maximo))) Interlocked.CompareExchange(ref maximo, ahora, visto);
            await Task.Delay(40, ct);
            Interlocked.Decrement(ref enCurso);
            return "30";
        });
        var archivos = Enumerable.Range(1, 8).Select(i => CrearArchivo($"t{i}.mp3", $"c{i}")).ToList();

        var resultados = await Task.WhenAll(archivos.Select(a => sut.ObtenerDuracionAsync(a, CancellationToken.None)));

        resultados.Should().OnlyContain(d => d != null);
        maximo.Should().BeLessThanOrEqualTo(2);
        maximo.Should().BeGreaterThan(0);
    }
}
