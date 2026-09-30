using AnimeLocalTracker.Core;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Core;

/// <summary>
/// Identidad de títulos separando serie y temporada. Casos sacados de búsquedas reales en Nyaa y
/// AnimeAv1 que antes descargaban la temporada equivocada.
/// </summary>
public class FirmaTituloTests
{
    private static FirmaTitulo.Evaluacion Evaluar(string nuestro, string suyo) => FirmaTitulo.Evaluar([nuestro], [suyo]);

    [Theory]
    [InlineData("Mushoku Tensei II: Isekai Ittara Honki Dasu", "Mushoku Tensei III: Isekai Ittara Honki Dasu")]
    [InlineData("Mairimashita! Iruma-kun 2", "Mairimashita! Iruma-kun S4")]
    [InlineData("Mairimashita! Iruma-kun 2", "Mairimashita Iruma-kun 4th Season")]
    [InlineData("Shingeki no Kyojin Season 3", "Shingeki no Kyojin - The Final Season Part 3")]
    [InlineData("Re:Zero kara Hajimeru Isekai Seikatsu 2nd Season Part 2", "Re:Zero kara Hajimeru Isekai Seikatsu 2nd Season")]
    [InlineData("Otonari no Tenshi-sama ni Itsunomanika Dame Ningen ni Sareteita Ken", "Otonari no Tenshi-sama ni Itsunomanika Dame Ningen ni Sareteita Ken S2")]
    public void Evaluar_MismaSerieOtraTemporada_EsConflicto(string nuestro, string suyo)
    {
        var e = Evaluar(nuestro, suyo);

        e.ConflictoTemporada().Should().BeTrue();
        e.MismaTemporada.Should().BeLessThan(0.8);
    }

    [Theory]
    [InlineData("Otonari no Tenshi-sama ni Itsunomanika Dame Ningen ni Sareteita Ken 2nd Season", "Otonari no Tenshi-sama ni Itsunomanika Dame Ningen ni Sareteita Ken S2")]
    [InlineData("Otonari no Tenshi-sama ni Itsunomanika Dame Ningen ni Sareteita Ken 2nd Season", "Otonari no Tenshi-sama ni Itsunomanika Dame Ningen ni Sareteita Ken 2")]
    [InlineData("Mairimashita! Iruma-kun 2", "Mairimashita! Iruma-kun 2nd Season")]
    [InlineData("Mushoku Tensei III: Isekai Ittara Honki Dasu", "Mushoku Tensei S3")]
    [InlineData("JUJUTSU KAISEN Season 3: The Culling Game Part 1", "Jujutsu Kaisen S3")]
    [InlineData("Kaiju No. 8", "Kaiju No. 8")]
    [InlineData("Kaiju No. 8 2nd Season", "Kaiju No. 8 S2")]
    [InlineData("Dogul Wang", "DogulWang")]
    [InlineData("Sousou no Frieren", "Sousou no Frieren")]
    public void Evaluar_MismaSerieYTemporada_Coincide(string nuestro, string suyo)
    {
        Evaluar(nuestro, suyo).MismaTemporada.Should().BeGreaterThanOrEqualTo(0.8);
    }

    [Theory]
    // Temporadas con nombre de arco: sin número no se puede recortar el subtítulo.
    [InlineData("Kimetsu no Yaiba: Yuukaku-hen", "Kimetsu no Yaiba")]
    [InlineData("Kaiju No. 8 2nd Season", "Kaiju No. 8")]
    [InlineData("ONE PIECE", "Mistress Kanan is Devilishly Easy S01")]
    public void Evaluar_OtraSerieUOtroArco_NoCoincide(string nuestro, string suyo)
    {
        Evaluar(nuestro, suyo).MismaTemporada.Should().BeLessThan(0.8);
    }

    [Fact]
    public void Interpretar_NumeroSueltoAlFinal_DaDosLecturas()
    {
        var lecturas = FirmaTitulo.Interpretar("Mairimashita! Iruma-kun 4");

        lecturas.Should().Contain(l => l.Base == "mairimashita iruma kun" && l.Temporada == 4);
        lecturas.Should().Contain(l => l.Base == "mairimashita iruma kun 4" && l.Temporada == 1);
    }

    [Fact]
    public void Interpretar_TemporadaEnJapones_SeReconoce()
    {
        FirmaTitulo.Interpretar("無職転生 ～異世界行ったら本気だす～ 第3期")[0].Temporada.Should().Be(3);
    }

    [Theory]
    [InlineData("Kusunoki's Garden of Gods", false, true)]
    [InlineData("Un Paraíso en Otro Mundo", false, true)]
    [InlineData("บ้านพักคุสึโนกิกับสวนเทพเจ้า", true, false)]
    [InlineData("Божественный сад у поместья Кусуноки", true, false)]
    [InlineData("きみが死ぬまで恋をしたい", false, false)]
    [InlineData("きみが死ぬまで恋をしたい", true, true)]
    [InlineData("海贼王", true, false)]
    public void EsAlfabetoBuscable_ClasificaPorAlfabeto(string titulo, bool incluirJapones, bool esperado)
    {
        FirmaTitulo.EsAlfabetoBuscable(titulo, incluirJapones).Should().Be(esperado);
    }
}
