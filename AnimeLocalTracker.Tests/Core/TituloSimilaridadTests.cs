using AnimeLocalTracker.Core;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Core;

/// <summary>
/// Coincidencia de títulos: por palabras (<c>Similitud</c>) y letra a letra (<c>SimilitudPorLetras</c>, el cálculo que antes
/// hacía rapidfuzz en el daemon Python).
/// </summary>
public class TituloSimilaridadTests
{
    // CA1861: arrays constantes reutilizados como campos estáticos
    private static readonly string[] NombresPeliculaSitio = { "Dragon Ball Z Película 14: Battle of Gods", "ドラゴンボールZ 神と神" };
    [Fact]
    public void Similitud_Identicos_DeberiaSerUno()
    {
        TituloSimilaridad.Similitud("Grand Blue", "Grand Blue").Should().Be(1.0);
    }

    [Fact]
    public void Similitud_DiferenciasDeSignos_DeberiaSerCasiUno()
    {
        TituloSimilaridad.Similitud("Dragon Ball Z: Battle of Gods", "Dragon Ball Z - Battle of Gods")
            .Should().Be(1.0, "los signos se normalizan");
    }

    [Fact]
    public void Similitud_TituloDelSitioConPelicula_DeberiaSerAlta()
    {
        // El caso Dragon Ball: la app dice "Battle of Gods", el sitio "Película 14: Battle of Gods"
        TituloSimilaridad.Similitud(
            "Dragon Ball Z: Battle of Gods",
            "Dragon Ball Z Película 14: Battle of Gods")
            .Should().BeGreaterThanOrEqualTo(0.7);
    }

    [Fact]
    public void Similitud_AnimesDistintosDeLaMismaFranquicia_DeberiaSerBaja()
    {
        TituloSimilaridad.Similitud("Dragon Ball Z", "Dragon Ball Super")
            .Should().BeLessThan(0.7);
    }

    [Fact]
    public void Similitud_VacioONull_DeberiaSerCero()
    {
        TituloSimilaridad.Similitud("", "Anime").Should().Be(0);
        TituloSimilaridad.Similitud(null, "Anime").Should().Be(0);
        TituloSimilaridad.Similitud("Anime", null).Should().Be(0);
    }

    // Puntuaciones de fuzz.token_sort_ratio de rapidfuzz 3.14.5 (sobre los textos en minúsculas, como los mandaba la app al daemon
    // Python): el cálculo en C# tiene que dar exactamente lo mismo para que ninguna página cambie de aceptada a rechazada.
    [Theory]
    [InlineData("Jujutsu Kaizen", "Jujutsu Kaisen", 0.9285714285714286)]
    [InlineData("BLACK TORCH", "Black Torch", 1.0)]
    [InlineData("Naruto Shippuuden", "Naruto: Shippuden", 0.9411764705882352)]
    [InlineData("Kaisen Jujutsu", "Jujutsu Kaisen", 1.0)]
    [InlineData("Re:Zero kara Hajimeru Isekai Seikatsu", "Re Zero kara Hajimeru Isekai Seikatsu", 0.8648648648648648)]
    [InlineData("Mushoku Tensei II: Isekai Ittara Honki Dasu", "Mushoku Tensei III: Isekai Ittara Honki Dasu", 0.9885057471264368)]
    [InlineData("Dragon Ball Z: Battle of Gods", "Dragon Ball Z Película 14: Battle of Gods", 0.8)]
    [InlineData("Dragon Ball Z", "Dragon Ball Super", 0.8)]
    [InlineData("Sousou no Frieren", "Frieren: Beyond Journey's End", 0.4782608695652174)]
    [InlineData("ドラゴンボールZ 神と神", "ドラゴンボールZ　神と神", 1.0)]
    [InlineData("Grand Blue", "One Piece", 0.21052631578947367)]
    [InlineData("Kage no Jitsuryokusha ni Naritakute!", "Kage no Jitsuryokusha ni Naritakute! 2nd Season", 0.8674698795180723)]
    [InlineData("a", "b", 0.0)]
    public void SimilitudPorLetras_DaLaMismaPuntuacionQueRapidfuzz(string a, string b, double esperada)
    {
        TituloSimilaridad.SimilitudPorLetras(a, b).Should().BeApproximately(esperada, 1e-12);
        TituloSimilaridad.SimilitudPorLetras(b, a).Should().BeApproximately(esperada, 1e-12);
    }

    [Fact]
    public void SimilitudPorLetras_VacioONull_DeberiaSerCero()
    {
        TituloSimilaridad.SimilitudPorLetras("", "Anime").Should().Be(0);
        TituloSimilaridad.SimilitudPorLetras("Anime", "   ").Should().Be(0);
        TituloSimilaridad.SimilitudPorLetras(null, null).Should().Be(0);
    }

    // Lo que el parecido letra a letra no ve: si la diferencia es de escritura (vale) o de una palabra entera (otro anime).
    [Theory]
    [InlineData("Jujutsu Kaizen", "Jujutsu Kaisen", true)]             // errata
    [InlineData("Naruto Shippuuden", "Naruto: Shippuden", true)]      // romanización
    [InlineData("Yuu Yuu Hakusho", "Yu Yu Hakusho", true)]
    [InlineData("Sword Art Online", "SwordArt Online", true)]         // palabras pegadas
    [InlineData("Re:Zero kara Hajimeru", "Re Zero kara Hajimeru", true)]
    [InlineData("Kaisen Jujutsu", "Jujutsu Kaisen", true)]
    [InlineData("Dragon Ball Z", "Dragon Ball Super", false)]         // otra palabra
    [InlineData("Dragon Ball Z", "Dragon Ball GT", false)]
    [InlineData("Dragon Ball", "Dragon Ball Z", false)]               // una palabra de más: lo juzga el parecido por palabras
    [InlineData("Dragon Ball Z: Battle of Gods", "Dragon Ball Z Película 14: Battle of Gods", false)]
    [InlineData("", "Anime", false)]
    public void SoloCambiaLaEscritura_DistingueUnaErrataDeOtraPalabra(string a, string b, bool esperado)
    {
        TituloSimilaridad.SoloCambiaLaEscritura(a, b).Should().Be(esperado);
        TituloSimilaridad.SoloCambiaLaEscritura(b, a).Should().Be(esperado);
    }

    [Fact]
    public void MejorSimilitud_ConAlternativos_DeberiaUsarElMejor()
    {
        // El título no coincide pero el nombre alternativo (ja-jp) sí es cercano
        double score = TituloSimilaridad.MejorSimilitud("ドラゴンボールZ 神と神", NombresPeliculaSitio);

        score.Should().Be(1.0);
    }
}
