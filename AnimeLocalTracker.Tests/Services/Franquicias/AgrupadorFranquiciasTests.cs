#pragma warning disable CA1861 // Datos constantes de prueba: un arreglo por llamada es lo más legible aquí
using System.Collections.Generic;
using System.Linq;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services.Franquicias;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services.Franquicias;

public class AgrupadorFranquiciasTests
{
    private static RelacionAnime Rel(int desde, int hacia, string tipo) =>
        new() { AnimeId = desde, RelacionadoId = hacia, Tipo = tipo };

    [Fact]
    public void AnimesSinRelaciones_CadaUnoEsSuPropiaFranquicia()
    {
        var mapa = AgrupadorFranquicias.Agrupar(new[] { 5, 9 }, new List<RelacionAnime>());

        mapa[5].Should().Be(5);
        mapa[9].Should().Be(9);
    }

    [Fact]
    public void SerieConPeliculasYEspeciales_QuedaComoUnaSolaFranquicia()
    {
        // One Piece (10) con dos películas (11, 12) y un especial (13) — relaciones vistas desde ambos lados
        var aristas = new List<RelacionAnime>
        {
            Rel(10, 11, "SIDE_STORY"), Rel(11, 10, "PARENT"),
            Rel(10, 12, "SPIN_OFF"), Rel(12, 10, "PARENT"),
            Rel(13, 10, "PARENT")
        };

        var mapa = AgrupadorFranquicias.Agrupar(new[] { 10, 11, 12, 13 }, aristas);

        mapa.Values.Distinct().Should().ContainSingle().Which.Should().Be(10);
    }

    [Fact]
    public void Temporadas_SeUnenPorPrecuelaYSecuela_YLaRaizEsElIdMenor()
    {
        var aristas = new List<RelacionAnime> { Rel(30, 20, "PREQUEL"), Rel(20, 30, "SEQUEL"), Rel(20, 25, "SEQUEL") };

        var mapa = AgrupadorFranquicias.Agrupar(new[] { 30, 20, 25 }, aristas);

        mapa.Values.Should().OnlyContain(v => v == 20);
    }

    [Fact]
    public void UnNodoExternoSirveDePuente_TienesLaTemporada1YLa3YLa2NoLaTienes()
    {
        // La temporada 2 (id 2) NO está en la biblioteca, pero une la 1 y la 3
        var aristas = new List<RelacionAnime> { Rel(1, 2, "SEQUEL"), Rel(2, 3, "SEQUEL") };

        var mapa = AgrupadorFranquicias.Agrupar(new[] { 1, 3 }, aristas);

        mapa[1].Should().Be(mapa[3]);
        mapa.Keys.Should().BeEquivalentTo(new[] { 1, 3 }, "el nodo externo no aparece en el resultado");
    }

    [Fact]
    public void ElResultadoNoDependeDelOrdenDeLasAristas()
    {
        var aristas = new List<RelacionAnime> { Rel(4, 2, "SEQUEL"), Rel(2, 9, "SEQUEL"), Rel(9, 7, "SIDE_STORY") };

        var adelante = AgrupadorFranquicias.Agrupar(new[] { 4, 2, 9, 7 }, aristas);
        var alReves = AgrupadorFranquicias.Agrupar(new[] { 7, 9, 2, 4 }, Enumerable.Reverse(aristas));

        adelante.Should().BeEquivalentTo(alReves);
        adelante.Values.Should().OnlyContain(v => v == 2);
    }

    [Theory]
    [InlineData("CHARACTER")]
    [InlineData("OTHER")]
    [InlineData("ADAPTATION")]
    [InlineData("SOURCE")]
    [InlineData("COMPILATION")]
    public void RelacionesQueNoSonDeFranquicia_NoUnen(string tipo)
    {
        var mapa = AgrupadorFranquicias.Agrupar(new[] { 1, 2 }, new[] { Rel(1, 2, tipo) });

        mapa[1].Should().NotBe(mapa[2]);
    }

    [Fact]
    public void SerieCruce_ConVariosPadres_NoFusionaLasFranquiciasDeSusPadres()
    {
        // Isekai Quartet (100) es "hijo" de cuatro franquicias distintas: no debe convertirlas en una sola.
        var aristas = new List<RelacionAnime>
        {
            Rel(100, 1, "PARENT"), Rel(100, 2, "PARENT"), Rel(100, 3, "PARENT"),
            Rel(1, 100, "SPIN_OFF"), Rel(2, 100, "SPIN_OFF"), Rel(3, 100, "SPIN_OFF")
        };

        var mapa = AgrupadorFranquicias.Agrupar(new[] { 1, 2, 3, 100 }, aristas);

        new[] { mapa[1], mapa[2], mapa[3] }.Distinct().Should().HaveCount(3, "cada padre sigue siendo su propia franquicia");
    }

    [Fact]
    public void SerieCruce_SusSecuelasSiSeSiguenUniendo()
    {
        var aristas = new List<RelacionAnime>
        {
            Rel(100, 1, "PARENT"), Rel(100, 2, "PARENT"),
            Rel(100, 101, "SEQUEL"), Rel(101, 100, "PREQUEL")
        };

        var mapa = AgrupadorFranquicias.Agrupar(new[] { 100, 101 }, aristas);

        mapa[100].Should().Be(mapa[101], "la secuela de la propia serie cruce sí pertenece a su franquicia");
    }

    [Fact]
    public void UnSoloPadre_NoEsCruce_YSiUneAlHijoConSuPadre()
    {
        var mapa = AgrupadorFranquicias.Agrupar(new[] { 1, 50 }, new[] { Rel(50, 1, "PARENT") });

        mapa[1].Should().Be(mapa[50]);
    }

    [Fact]
    public void Franquicias_DistintasNoSeMezclan()
    {
        var aristas = new List<RelacionAnime> { Rel(1, 2, "SEQUEL"), Rel(10, 11, "SEQUEL") };

        var mapa = AgrupadorFranquicias.Agrupar(new[] { 1, 2, 10, 11 }, aristas);

        mapa[1].Should().Be(mapa[2]);
        mapa[10].Should().Be(mapa[11]);
        mapa[1].Should().NotBe(mapa[10]);
    }

    [Fact]
    public void EsRelacionDeFranquicia_ReconoceSoloLosTiposEsperados()
    {
        new[] { "PREQUEL", "SEQUEL", "PARENT", "SIDE_STORY", "SPIN_OFF", "SUMMARY", "ALTERNATIVE", "sequel" }
            .Should().OnlyContain(t => AgrupadorFranquicias.EsRelacionDeFranquicia(t));
        AgrupadorFranquicias.EsRelacionDeFranquicia("CHARACTER").Should().BeFalse();
        AgrupadorFranquicias.EsRelacionDeFranquicia(null).Should().BeFalse();
    }
}
