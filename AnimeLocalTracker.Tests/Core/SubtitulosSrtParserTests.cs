using System;
using AnimeLocalTracker.Core;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Core;

public class SubtitulosSrtParserTests
{
    [Fact]
    public void ParseaUnBloqueSimple()
    {
        const string srt = "1\n00:00:01,000 --> 00:00:04,500\nHola mundo\n";

        var cues = SubtitulosSrtParser.Parsear(srt);

        cues.Should().HaveCount(1);
        cues[0].Inicio.Should().Be(TimeSpan.FromSeconds(1));
        cues[0].Fin.Should().Be(new TimeSpan(0, 0, 0, 4, 500));
        cues[0].Texto.Should().Be("Hola mundo");
    }

    [Fact]
    public void ParseaVariosBloquesSeparadosPorLineaEnBlanco()
    {
        const string srt =
            "1\n00:00:01,000 --> 00:00:02,000\nUno\n\n" +
            "2\n00:00:03,000 --> 00:00:05,000\nDos\n";

        var cues = SubtitulosSrtParser.Parsear(srt);

        cues.Should().HaveCount(2);
        cues[0].Texto.Should().Be("Uno");
        cues[1].Texto.Should().Be("Dos");
    }

    [Fact]
    public void UneVariasLineasDeTextoConSaltoDeLinea()
    {
        const string srt = "1\n00:00:01,000 --> 00:00:04,000\nLínea A\nLínea B\n";

        var cues = SubtitulosSrtParser.Parsear(srt);

        cues.Should().ContainSingle().Which.Texto.Should().Be("Línea A\nLínea B");
    }

    [Fact]
    public void FuncionaSinNumeroDeIndice()
    {
        // Algunos conversores omiten el número de bloque: solo la marca de tiempo y el texto.
        const string srt = "00:00:01,000 --> 00:00:02,000\nSin índice\n";

        var cues = SubtitulosSrtParser.Parsear(srt);

        cues.Should().ContainSingle().Which.Texto.Should().Be("Sin índice");
    }

    [Fact]
    public void QuitaEtiquetasHtmlYSsaSupervivientes()
    {
        const string srt = "1\n00:00:01,000 --> 00:00:02,000\n{\\an8}<i>cursiva</i>\n";

        var cues = SubtitulosSrtParser.Parsear(srt);

        cues.Should().ContainSingle().Which.Texto.Should().Be("cursiva");
    }

    [Fact]
    public void AceptaPuntoComoSeparadorDeMilisegundos()
    {
        // Algunas conversiones de VTT/ASS dejan "." en vez de ",".
        const string srt = "1\n00:00:01.000 --> 00:00:02.000\nTexto\n";

        var cues = SubtitulosSrtParser.Parsear(srt);

        cues.Should().ContainSingle();
    }

    [Fact]
    public void DescartaBloquesSinTextoOConFinAntesDelInicio()
    {
        const string srt =
            "1\n00:00:01,000 --> 00:00:02,000\n\n\n" + // sin texto
            "2\n00:00:05,000 --> 00:00:03,000\nInvertido\n"; // fin antes que inicio

        var cues = SubtitulosSrtParser.Parsear(srt);

        cues.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ContenidoVacioODevuelveListaVacia(string? contenido)
    {
        SubtitulosSrtParser.Parsear(contenido).Should().BeEmpty();
    }

    [Fact]
    public void ContenidoBasuraSinMarcasDeTiempoNoLanza()
    {
        SubtitulosSrtParser.Parsear("esto no es un srt para nada\nni una sola marca de tiempo").Should().BeEmpty();
    }
}
