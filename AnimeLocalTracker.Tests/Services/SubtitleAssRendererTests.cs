using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// Dibujo real con el libass del ffmpeg incluido, sobre guiones .ass mínimos escritos en una carpeta temporal.
/// El fotograma es BGRA premultiplicado: un rojo opaco es (B=0, G=0, R=255, A=255).
/// </summary>
public class SubtitleAssRendererTests : IDisposable
{
    private const int Ancho = 320, Alto = 180;

    // En la app las bibliotecas de FFmpeg las carga Flyleaf (MotorVideo); en las pruebas el motor no arranca.
    private static readonly Lazy<bool> Bibliotecas = new(() =>
    {
        SubtitleAssRenderer.CargarBibliotecas(Path.Combine(AppContext.BaseDirectory, "FFmpeg"));
        return true;
    });

    private const string LineaRoja = @"Dialogue: 0,0:00:01.00,0:00:03.00,Default,,0,0,0,,{\pos(10,10)\c&H0000FF&}HOLA";
    private const string CajaBlancaAl50 = @"Dialogue: 0,0:00:00.00,0:00:10.00,Default,,0,0,0,,{\pos(0,0)\p1\1a&H80&}m 0 0 l 100 0 100 100 0 100";
    private const string CajaBlancaOpaca = @"Dialogue: 0,0:00:00.00,0:00:10.00,Default,,0,0,0,,{\pos(0,0)\p1}m 0 0 l 100 0 100 100 0 100";

    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), "ALT_ass_" + Guid.NewGuid().ToString("N"));
    private readonly SubtitleAssRenderer _sut = new();
    private readonly byte[] _fotograma = new byte[Ancho * Alto * 4];

    public SubtitleAssRendererTests()
    {
        _ = Bibliotecas.Value;
        Directory.CreateDirectory(_carpeta);
    }

    public void Dispose()
    {
        _sut.Cerrar();
        try { Directory.Delete(_carpeta, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>Guion de 320×180, letra blanca sin contorno ni sombra, alineada arriba a la izquierda.</summary>
    private string EscribirGuion(string evento, string? matriz = "None", string nombre = "prueba.ass")
    {
        string cabeceraMatriz = matriz == null ? string.Empty : $"YCbCr Matrix: {matriz}\n";
        string guion =
            "[Script Info]\nScriptType: v4.00+\nPlayResX: 320\nPlayResY: 180\n" + cabeceraMatriz +
            "\n[V4+ Styles]\n" +
            "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n" +
            "Style: Default,Arial,40,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,-1,0,0,0,100,100,0,0,1,0,0,7,0,0,0,1\n" +
            "\n[Events]\n" +
            "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n" +
            evento + "\n";

        string ruta = Path.Combine(_carpeta, nombre);
        File.WriteAllText(ruta, guion);
        return ruta;
    }

    private Task<bool> Abrir(string ruta) => _sut.AbrirAsync(ruta, -1, Ancho, Alto, CancellationToken.None);

    private (byte B, byte G, byte R, byte A) Pixel(int x, int y)
    {
        int i = (y * Ancho + x) * 4;
        return (_fotograma[i], _fotograma[i + 1], _fotograma[i + 2], _fotograma[i + 3]);
    }

    private bool HayRojoOpaco(int x0, int y0, int x1, int y1)
    {
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                var (b, g, r, a) = Pixel(x, y);
                if (b <= 10 && g <= 10 && r >= 245 && a >= 245) return true;
            }
        return false;
    }

    private bool TodoTransparente(int desdeFila, int hastaFila)
        => _fotograma.AsSpan(desdeFila * Ancho * 4, (hastaFila - desdeFila) * Ancho * 4).IndexOfAnyExcept((byte)0) < 0;

    [Fact]
    public async Task Renderizar_ConLineaActiva_DibujaEnSuPosicionYDejaElRestoTransparente()
    {
        (await Abrir(EscribirGuion(LineaRoja))).Should().BeTrue();
        _sut.Renderizar(TimeSpan.FromSeconds(5), _fotograma, out _, out _); // primera llamada: estrena el búfer

        _sut.Renderizar(TimeSpan.FromSeconds(2), _fotograma, out int filaInicial, out int filas).Should().BeTrue();

        HayRojoOpaco(0, 0, Ancho / 2, Alto / 2).Should().BeTrue("la línea está en \\pos(10,10), arriba a la izquierda");
        TodoTransparente(Alto / 2, Alto).Should().BeTrue("en la mitad inferior no hay ninguna línea");
        filas.Should().BeGreaterThan(0);
        (filaInicial + filas).Should().BeLessThanOrEqualTo(Alto / 2, "solo se avisa de la franja donde está la línea");
    }

    [Fact]
    public async Task Renderizar_EnUnInstanteSinLineas_NoCambiaNada()
    {
        await Abrir(EscribirGuion(LineaRoja));
        _sut.Renderizar(TimeSpan.FromSeconds(5), _fotograma, out _, out _);

        _sut.Renderizar(TimeSpan.FromSeconds(6), _fotograma, out _, out int filas).Should().BeTrue();

        filas.Should().Be(0);
        TodoTransparente(0, Alto).Should().BeTrue();
    }

    [Fact]
    public async Task Renderizar_ConUnBuferNuevoConBasura_LoDejaLimpioYAvisaDelFotogramaEntero()
    {
        await Abrir(EscribirGuion(LineaRoja));
        Array.Fill(_fotograma, (byte)7);

        _sut.Renderizar(TimeSpan.FromSeconds(5), _fotograma, out int filaInicial, out int filas).Should().BeTrue();

        TodoTransparente(0, Alto).Should().BeTrue();
        (filaInicial, filas).Should().Be((0, Alto));
    }

    [Fact]
    public async Task Renderizar_CuandoLaLineaTermina_LimpiaSuFranjaYAvisaDeElla()
    {
        await Abrir(EscribirGuion(LineaRoja));
        _sut.Renderizar(TimeSpan.FromSeconds(5), _fotograma, out _, out _);
        _sut.Renderizar(TimeSpan.FromSeconds(2), _fotograma, out int filaLinea, out int filasLinea);

        _sut.Renderizar(TimeSpan.FromSeconds(5), _fotograma, out int filaInicial, out int filas).Should().BeTrue();

        TodoTransparente(0, Alto).Should().BeTrue();
        (filaInicial, filas).Should().Be((filaLinea, filasLinea), "hay que repintar la franja donde estaba la línea");
    }

    [Fact]
    public async Task Renderizar_AlRetrocederEnElTiempo_DaElMismoFotograma()
    {
        await Abrir(EscribirGuion(LineaRoja));
        _sut.Renderizar(TimeSpan.FromSeconds(2), _fotograma, out _, out _).Should().BeTrue();
        byte[] primero = (byte[])_fotograma.Clone();

        _sut.Renderizar(TimeSpan.FromSeconds(5), _fotograma, out _, out _).Should().BeTrue();
        _sut.Renderizar(TimeSpan.FromSeconds(2), _fotograma, out _, out _).Should().BeTrue();

        _fotograma.Should().Equal(primero);
    }

    [Fact]
    public async Task Renderizar_ColorSemitransparente_DaLaTransparenciaExacta()
    {
        await Abrir(EscribirGuion(CajaBlancaAl50));

        _sut.Renderizar(TimeSpan.FromSeconds(1), _fotograma, out _, out _).Should().BeTrue();

        var (b, g, r, a) = Pixel(50, 50);
        a.Should().BeInRange(125, 129, "un 50 % de opacidad no debe salir al 25 %");
        b.Should().BeInRange(125, 129);
        g.Should().BeInRange(125, 129);
        r.Should().BeInRange(125, 129);
    }

    [Theory]
    [InlineData(null)]     // el guion no declara matriz: ffmpeg comprime a 16–235 y hay que estirarlo
    [InlineData("TV.601")]
    [InlineData("None")]   // ffmpeg ya pinta a rango completo: no hay que tocarlo
    [InlineData("PC.709")]
    public async Task Renderizar_BlancoOpaco_SaleBlancoPuroSeaCualSeaLaMatrizDelGuion(string? matriz)
    {
        await Abrir(EscribirGuion(CajaBlancaOpaca, matriz));

        _sut.Renderizar(TimeSpan.FromSeconds(1), _fotograma, out _, out _).Should().BeTrue();

        Pixel(50, 50).Should().Be(((byte)255, (byte)255, (byte)255, (byte)255));
    }

    [Fact]
    public async Task AbrirAsync_NombreDeArchivoDeFansub_Funciona()
    {
        string ruta = EscribirGuion(LineaRoja, nombre: "[Fansub] It's épisode, 01 (1080p) [ABCD1234].ass");

        (await Abrir(ruta)).Should().BeTrue();
        _sut.Renderizar(TimeSpan.FromSeconds(2), _fotograma, out _, out _).Should().BeTrue();
        HayRojoOpaco(0, 0, Ancho / 2, Alto / 2).Should().BeTrue();
    }

    [Fact]
    public async Task AbrirAsync_ArchivoInexistente_DevuelveFalse()
    {
        (await Abrir(Path.Combine(_carpeta, "no-existe.ass"))).Should().BeFalse();
        _sut.Renderizar(TimeSpan.FromSeconds(2), _fotograma, out _, out _).Should().BeFalse();
    }

    [Fact]
    public async Task AbrirAsync_TamanoCero_DevuelveFalse()
        => (await _sut.AbrirAsync(EscribirGuion(LineaRoja), -1, 0, 0, CancellationToken.None)).Should().BeFalse();

    [Fact]
    public async Task Renderizar_ConBuferMasPequenoQueElFotograma_DevuelveFalse()
    {
        await Abrir(EscribirGuion(LineaRoja));

        _sut.Renderizar(TimeSpan.FromSeconds(2), new byte[16], out _, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Renderizar_TrasCerrar_DevuelveFalse()
    {
        await Abrir(EscribirGuion(LineaRoja));

        _sut.Cerrar();

        _sut.Renderizar(TimeSpan.FromSeconds(2), _fotograma, out _, out _).Should().BeFalse();
        _sut.Ancho.Should().Be(0);
    }
}
