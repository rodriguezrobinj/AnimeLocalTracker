using System.Collections.Generic;
using System.Linq;
using AnimeLocalTracker.Controls;
using AnimeLocalTracker.Models;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Models;

/// <summary>De los tramos de saltos a los marcadores de la barra de progreso del reproductor.</summary>
public class SegmentoLineaTiempoTests
{
    private static AniSkipResult T(string tipo, double ini, double fin) => new() { SkipType = tipo, Interval = new AniSkipInterval { StartTime = ini, EndTime = fin } };

    [Fact]
    public void Crear_ConvierteOpeningEndingYResumen_OrdenadosPorInicio()
    {
        var r = SegmentoLineaTiempo.Crear([T("ed", 1300, 1390), T("recap", 0, 40), T("op", 60, 150)], 1400);

        r.Select(s => (s.Tipo, s.Inicio, s.Fin)).Should().Equal(
            (TipoSegmentoLineaTiempo.Resumen, 0d, 40d), (TipoSegmentoLineaTiempo.Opening, 60d, 150d), (TipoSegmentoLineaTiempo.Ending, 1300d, 1390d));
    }

    [Fact]
    public void Crear_LosMixedSoloSeUsanSiNoHayUnoNormalDelMismoTipo()
    {
        var conNormal = SegmentoLineaTiempo.Crear([T("op", 47, 137), T("mixed-op", 75, 135), T("ed", 1343, 1431), T("mixed-ed", 24, 114)], 1450);
        var soloMixed = SegmentoLineaTiempo.Crear([T("mixed-op", 75, 135), T("mixed-ed", 1300, 1400)], 1450);

        conNormal.Should().HaveCount(2, "no se apilan dos marcas casi iguales");
        conNormal.Select(s => s.Tipo).Should().Equal(TipoSegmentoLineaTiempo.Opening, TipoSegmentoLineaTiempo.Ending);
        soloMixed.Select(s => s.Tipo).Should().Equal(TipoSegmentoLineaTiempo.Opening, TipoSegmentoLineaTiempo.Ending);
    }

    [Theory]
    [InlineData(100, 100)]
    [InlineData(100, 50)]
    [InlineData(-20, -5)]
    public void Crear_DescartaTramosInvalidos(double ini, double fin) =>
        SegmentoLineaTiempo.Crear([T("op", ini, fin)], 1400).Should().BeEmpty();

    [Fact]
    public void Crear_RecortaAlLargoDelEpisodio_YDescartaLosQueEmpiezanDespues()
    {
        var r = SegmentoLineaTiempo.Crear([T("ed", 1350, 1500), T("recap", 2000, 2050)], 1400);

        r.Should().ContainSingle().Which.Fin.Should().Be(1400);
    }

    [Fact]
    public void Crear_SinDuracionConocida_NoRecorta() =>
        SegmentoLineaTiempo.Crear([T("ed", 1350, 1500)], 0).Should().ContainSingle().Which.Fin.Should().Be(1500);

    [Fact]
    public void Crear_IgnoraTiposDesconocidosYNulos()
    {
        SegmentoLineaTiempo.Crear([T("raro", 10, 20)], 1400).Should().BeEmpty();
        SegmentoLineaTiempo.Crear(null, 1400).Should().BeEmpty();
        SegmentoLineaTiempo.Crear(new List<AniSkipResult>(), 1400).Should().BeEmpty();
    }

    [Fact]
    public void Crear_UnInicioNegativoSeAjustaACero() =>
        SegmentoLineaTiempo.Crear([T("op", -3, 87)], 1400).Should().ContainSingle().Which.Inicio.Should().Be(0);

    [Fact]
    public void Duracion_EsFinMenosInicio() =>
        new SegmentoLineaTiempo(TipoSegmentoLineaTiempo.Opening, 60, 150).Duracion.Should().Be(90);
}

/// <summary>Posición de las marcas: debe coincidir con el recorrido real de la bolita del deslizador.</summary>
public class MarcadoresLineaTiempoTests
{
    [Theory]
    [InlineData(0, 100, 220, 10, 10)]      // inicio: el centro de la bolita empieza a media bolita del borde
    [InlineData(100, 100, 220, 10, 210)]   // final
    [InlineData(50, 100, 220, 10, 110)]    // mitad
    [InlineData(-5, 100, 220, 10, 10)]     // fuera de rango: se ajusta
    [InlineData(500, 100, 220, 10, 210)]
    public void PosicionX_SigueElRecorridoDeLaBolita(double segundos, double duracion, double ancho, double margen, double esperado) =>
        MarcadoresLineaTiempo.PosicionX(segundos, duracion, ancho, margen).Should().BeApproximately(esperado, 0.001);

    private static readonly List<SegmentoLineaTiempo> Tramos =
    [
        new(TipoSegmentoLineaTiempo.Opening, 60, 150),
        new(TipoSegmentoLineaTiempo.Ending, 1300, 1390)
    ];

    [Theory]
    [InlineData(0, null)]
    [InlineData(59.9, null)]
    [InlineData(60, TipoSegmentoLineaTiempo.Opening)]      // el inicio cuenta
    [InlineData(100, TipoSegmentoLineaTiempo.Opening)]
    [InlineData(150, null)]                                // el fin no
    [InlineData(1350, TipoSegmentoLineaTiempo.Ending)]
    [InlineData(1400, null)]
    public void TramoEn_DevuelveElTramoEnElQueEstaLaReproduccion(double segundos, TipoSegmentoLineaTiempo? esperado) =>
        MarcadoresLineaTiempo.TramoEn(Tramos, segundos)?.Tipo.Should().Be(esperado!.Value);

    [Fact]
    public void TramoEn_ConReproduccionFueraDeTramos_NoDevuelveNada() =>
        MarcadoresLineaTiempo.TramoEn(Tramos, 30).Should().BeNull();

    [Fact]
    public void TramoEn_FueraDeTodosOSinTramos_EsNulo()
    {
        MarcadoresLineaTiempo.TramoEn(Tramos, 700).Should().BeNull();
        MarcadoresLineaTiempo.TramoEn(null, 100).Should().BeNull();
        MarcadoresLineaTiempo.TramoEn([], 100).Should().BeNull();
    }

    [Fact]
    public void PosicionX_SinDuracion_EsElMargen() =>
        MarcadoresLineaTiempo.PosicionX(30, 0, 200, 10).Should().Be(10);

    [Fact]
    public void PosicionX_ConAnchoMenorQueLosMargenes_NoDaNegativos() =>
        MarcadoresLineaTiempo.PosicionX(50, 100, 10, 10).Should().BeGreaterThanOrEqualTo(0);
}
