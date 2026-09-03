using AnimeLocalTracker.Core;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Core;

/// <summary>
/// Fallback hermético de coincidencia de títulos (el daemon rapidfuzz es la vía
/// preferida; C# cubre cuando Python no está disponible).
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

    [Fact]
    public void MejorSimilitud_ConAlternativos_DeberiaUsarElMejor()
    {
        // El título no coincide pero el nombre alternativo (ja-jp) sí es cercano
        double score = TituloSimilaridad.MejorSimilitud("ドラゴンボールZ 神と神", NombresPeliculaSitio);

        score.Should().Be(1.0);
    }
}
