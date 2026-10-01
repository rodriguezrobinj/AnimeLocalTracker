using AnimeLocalTracker.Services;
using FluentAssertions;
using FlyleafLib.MediaFramework.MediaFrame;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

public class ParcheSubtitulosFlyleafTests
{
    // Línea real (cartel del título, Re:Zero 4th Season ep 19, pista CR_Spanish(Latin_America)) que cerraba la app.
    private const string LineaCartel =
        @"0,0,Cart_A_Tre,CARTEL,0,0,0,,{\c&HA96974&\fad(300,500)\bord1\blur1.4\fs50\3c&HFFFFFF&\3a&H37&\pos(333.14,3.43)}Re{\fnVerdana\fs44\fscx90\c&H181622&\3c&HFFFFFF&}:ZERO\N{\fs22}-Starting Life in Another World-";

    [Fact]
    public void Aplicar_LineaQueHaciaFallarAFlyleaf_DevuelveTextoPlanoSinLanzar()
    {
        ParcheSubtitulosFlyleaf.Aplicar().Should().BeTrue();

        string texto = null!;
        List<SubStyle> estilos = null!;
        var leer = () => texto = ParseSubtitles.SSAtoSubStyles(LineaCartel, out estilos);

        leer.Should().NotThrow();
        texto.Should().Be("Re:ZERO\n-Starting Life in Another World-");
        estilos.Should().BeEmpty();
    }

    [Fact]
    public void Aplicar_LineaNormal_FlyleafSigueLeyendoSusEstilos()
    {
        ParcheSubtitulosFlyleaf.Aplicar().Should().BeTrue();

        string texto = ParseSubtitles.SSAtoSubStyles(@"0,0,Default,,0,0,0,,{\i1}Hola{\i0} mundo", out var estilos);

        texto.Should().Be("Hola mundo");
        estilos.Should().ContainSingle();
    }

    [Fact]
    public void Aplicar_LlamadoDosVeces_NoFalla()
    {
        ParcheSubtitulosFlyleaf.Aplicar().Should().BeTrue();
        ParcheSubtitulosFlyleaf.Aplicar().Should().BeTrue();
    }

    [Theory]
    [InlineData(@"0,0,Default,,0,0,0,,{\an8}Arriba\Nabajo", "Arriba\nabajo")]
    [InlineData(@"{\c&H0000FF&\3a&H37&}Solo texto", "Solo texto")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void TextoSinEtiquetas_QuitaBloquesYConvierteSaltos(string? linea, string esperado)
    {
        ParcheSubtitulosFlyleaf.TextoSinEtiquetas(linea).Should().Be(esperado);
    }
}
