using AnimeLocalTracker.Core;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

public class SubtitulosAssTests
{
    // Contenedor de ejemplo: 0 video, 1 audio, 2 y 3 subtítulos, 4 audio, 5 subtítulos.
    private static readonly int[] PistasDeSubtitulos = { 2, 3, 5 };
    private static readonly int[] PistasDesordenadas = { 5, 2, 3 };
    private static readonly int[] DosPistas = { 2, 3 };

    [Theory]
    [InlineData(true, false, false, false, true)]   // ASS incrustada, estilo original
    [InlineData(true, false, false, true, false)]   // el usuario pidió su estilo
    [InlineData(false, false, false, false, false)] // SRT
    [InlineData(true, true, false, false, false)]   // pista de imagen
    [InlineData(true, false, true, false, false)]   // archivo externo (fuera de alcance)
    public void DebeDibujarse_SoloAssIncrustadaSinEstiloPropio(bool esAss, bool esImagen, bool esExterna, bool usarMiEstilo, bool esperado)
        => SubtitulosAss.DebeDibujarse(esAss, esImagen, esExterna, usarMiEstilo).Should().Be(esperado);

    [Fact]
    public void IndiceEntreSubtitulos_CuentaLasPistasDeSubtitulosAnteriores()
    {
        SubtitulosAss.IndiceEntreSubtitulos(PistasDeSubtitulos, 2).Should().Be(0);
        SubtitulosAss.IndiceEntreSubtitulos(PistasDeSubtitulos, 3).Should().Be(1);
        SubtitulosAss.IndiceEntreSubtitulos(PistasDeSubtitulos, 5).Should().Be(2);
    }

    [Fact]
    public void IndiceEntreSubtitulos_NoDependeDelOrdenDeLaLista()
        => SubtitulosAss.IndiceEntreSubtitulos(PistasDesordenadas, 5).Should().Be(2);

    [Fact]
    public void IndiceEntreSubtitulos_PistaQueNoEstaEnLaLista_DevuelveMenosUno()
        => SubtitulosAss.IndiceEntreSubtitulos(DosPistas, 4).Should().Be(-1);

    [Theory]
    [InlineData(1280, 720, 1920, 1080, 1280, 720)]  // cabe: tamaño del video
    [InlineData(1920, 1080, 1366, 768, 1364, 768)]  // no cabe: se reduce sin deformar, dimensiones pares
    [InlineData(1440, 1080, 1366, 768, 1024, 768)]  // 4:3
    [InlineData(0, 0, 1366, 768, 0, 0)]             // tamaño de video desconocido
    [InlineData(1920, 1080, 0, 0, 0, 0)]            // pantalla desconocida
    public void TamanoDibujo_AjustaALaPantallaSinDeformar(int anchoVideo, int altoVideo, int anchoPantalla, int altoPantalla, int ancho, int alto)
        => SubtitulosAss.TamanoDibujo(anchoVideo, altoVideo, anchoPantalla, altoPantalla).Should().Be((ancho, alto));

    [Theory]
    [InlineData(null, true)]                                              // sin cabecera: ffmpeg asume TV
    [InlineData("[Script Info]\nPlayResX: 640\n", true)]                  // no declara matriz
    [InlineData("[Script Info]\nYCbCr Matrix: TV.601\n", true)]
    [InlineData("[Script Info]\r\nYCbCr Matrix: TV.709\r\n", true)]
    [InlineData("[Script Info]\nYCbCr Matrix: None\n", false)]
    [InlineData("[Script Info]\nYCbCr Matrix: PC.709\n", false)]
    [InlineData("[Script Info]\n  ycbcr matrix:   pc.601  \n", false)]    // mayúsculas y espacios
    [InlineData("[Script Info]\nYCbCr Matrix: algo raro\n", true)]        // desconocida: como TV
    public void RangoComprimido_SegunLaMatrizQueDeclaraElGuion(string? cabecera, bool esperado)
        => SubtitulosAss.RangoComprimido(cabecera).Should().Be(esperado);
}
