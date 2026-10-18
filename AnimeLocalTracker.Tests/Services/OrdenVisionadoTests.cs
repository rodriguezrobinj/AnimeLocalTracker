#pragma warning disable CA1861 // Datos constantes de prueba
using System;
using System.Collections.Generic;
using System.Linq;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services.Franquicias;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>Orden de visionado: línea principal por precuelas/secuelas, extras colgados y spin-offs aparte.</summary>
public class OrdenVisionadoTests
{
    private static RelacionAnime R(int a, int b, string tipo) => new() { AnimeId = a, RelacionadoId = b, Tipo = tipo };

    private static Func<int, int> Fechas(Dictionary<int, int> fechas) => id => fechas.GetValueOrDefault(id);

    private static int[] Linea(ResultadoOrdenVisionado r) => r.LineaPrincipal.Select(p => p.AniListId).ToArray();

    // Shingeki: 1 (T1, 2013) → 2 (T2, 2017) → 3 (T3, 2018); 10 = OVA de la T1; 20 = spin-off (Chuugakkou).
    private static readonly RelacionAnime[] Shingeki =
    {
        R(1, 2, "SEQUEL"), R(2, 1, "PREQUEL"), R(2, 3, "SEQUEL"), R(3, 2, "PREQUEL"),
        R(1, 10, "SIDE_STORY"), R(10, 1, "PARENT"), R(1, 20, "SPIN_OFF"),
    };
    private static readonly Dictionary<int, int> FechasShingeki = new() { [1] = 20132, [2] = 20172, [3] = 20183, [10] = 20134, [20] = 20154 };

    [Fact]
    public void Saga_LineaPrincipal_ExtraColgado_YSpinOffAparte()
    {
        var r = OrdenVisionado.Construir(2, Shingeki, Fechas(FechasShingeki));

        Linea(r).Should().Equal(1, 2, 3);
        r.LineaPrincipal[0].Extras.Should().Equal(10);
        r.LineaPrincipal[1].Extras.Should().BeEmpty();
        r.Aparte.Should().Equal(20);
    }

    [Fact]
    public void AbrirDesdeLaOva_DaLaLineaDeSuPadre()
    {
        var r = OrdenVisionado.Construir(10, Shingeki, Fechas(FechasShingeki));

        Linea(r).Should().Equal(1, 2, 3);
        r.LineaPrincipal[0].Extras.Should().Equal(10);
    }

    [Fact]
    public void DosSecuelas_SeOrdenanPorFecha()
    {
        var aristas = new[] { R(1, 3, "SEQUEL"), R(1, 2, "SEQUEL") };
        var r = OrdenVisionado.Construir(1, aristas, Fechas(new() { [1] = 20093, [2] = 20101, [3] = 20121 }));

        Linea(r).Should().Equal(1, 2, 3);
    }

    [Fact]
    public void SoloPrecuela_SeLeeDesdeElOtroLado()
    {
        var r = OrdenVisionado.Construir(1, new[] { R(2, 1, "PREQUEL") }, Fechas(new()));

        Linea(r).Should().Equal(1, 2);
    }

    [Fact]
    public void CicloDeSecuelas_NoPierdeNiRepite()
    {
        var aristas = new[] { R(1, 2, "SEQUEL"), R(2, 1, "SEQUEL") };
        var r = OrdenVisionado.Construir(2, aristas, Fechas(new() { [1] = 20101, [2] = 20121 }));

        Linea(r).Should().Equal(1, 2);
    }

    [Fact]
    public void SinFecha_VaAlFinalDeSuGrupo()
    {
        var aristas = new[] { R(1, 2, "SEQUEL"), R(1, 3, "SEQUEL") };
        var r = OrdenVisionado.Construir(1, aristas, Fechas(new() { [1] = 20101, [3] = 20111 }));

        Linea(r).Should().Equal(1, 3, 2);
    }

    [Fact]
    public void CruceDePersonajesYOtros_QuedanFuera()
    {
        var aristas = new[] { R(1, 2, "SEQUEL"), R(1, 50, "CHARACTER"), R(1, 51, "OTHER"), R(1, 52, "ADAPTATION") };
        var r = OrdenVisionado.Construir(1, aristas, Fechas(new()));

        Linea(r).Should().Equal(1, 2);
        r.LineaPrincipal.SelectMany(p => p.Extras).Should().BeEmpty();
        r.Aparte.Should().BeEmpty();
    }

    [Fact]
    public void ExtraUnidoADosPartes_VaBajoLaUltima()
    {
        var aristas = new[] { R(1, 2, "SEQUEL"), R(30, 1, "PARENT"), R(30, 2, "PARENT") };
        var r = OrdenVisionado.Construir(1, aristas, Fechas(new() { [1] = 20101, [2] = 20111, [30] = 20112 }));

        r.LineaPrincipal[0].Extras.Should().BeEmpty();
        r.LineaPrincipal[1].Extras.Should().Equal(30);
    }

    [Fact]
    public void TopeDeSesenta_SeQuedaConLasMasCercanas()
    {
        var aristas = Enumerable.Range(1, 99).Select(i => R(i, i + 1, "SEQUEL")).ToArray();
        var r = OrdenVisionado.Construir(1, aristas, Fechas(new()));

        Linea(r).Should().HaveCount(OrdenVisionado.MaximoPartes).And.OnlyHaveUniqueItems();
        Linea(r).Should().StartWith(new[] { 1, 2, 3 });
        Linea(r).Max().Should().Be(OrdenVisionado.MaximoPartes);
    }

    [Fact]
    public void SinRelaciones_SoloElPropioAnime()
    {
        var r = OrdenVisionado.Construir(7, Array.Empty<RelacionAnime>(), Fechas(new()));

        Linea(r).Should().Equal(7);
        r.Aparte.Should().BeEmpty();
    }

    [Fact]
    public void UnExtraQueTambienEsSpinOff_NoSeRepite()
    {
        var aristas = new[] { R(1, 2, "SEQUEL"), R(1, 40, "SIDE_STORY"), R(2, 40, "SPIN_OFF") };
        var r = OrdenVisionado.Construir(1, aristas, Fechas(new()));

        r.LineaPrincipal.SelectMany(p => p.Extras).Should().Equal(40);
        r.Aparte.Should().BeEmpty();
    }

    [Theory]
    [InlineData(2013, 4, 20132)]
    [InlineData(2013, 1, 20131)]
    [InlineData(2013, 12, 20134)]
    [InlineData(2013, null, 20130)]
    [InlineData(null, 5, 0)]
    [InlineData(0, 5, 0)]
    public void FechaComparable_UsaLaEscalaDeOrdenEstreno(int? anio, int? mes, int esperado)
    {
        OrdenVisionado.FechaComparable(anio, mes).Should().Be(esperado);
    }
}
