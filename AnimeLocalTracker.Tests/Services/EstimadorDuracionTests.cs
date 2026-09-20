using System.Collections.Generic;
using System.Linq;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// Horas vistas: los episodios marcados/importados en bloque no guardan su duración y no deben
/// contar como cero (1500 episodios vistos salían como ~7 h).
/// </summary>
public class EstimadorDuracionTests
{
    private static RegistroEpisodio Ep(double total = 0, double progreso = 0) =>
        new() { VistoLocal = true, TotalSegundos = total, ProgresoSegundos = progreso };

    [Fact]
    public void SinEpisodios_EsCero()
    {
        EstimadorDuracion.SegundosVistos(new List<RegistroEpisodio>()).Should().Be(0);
    }

    [Fact]
    public void TodosConDuracion_SumaLasDuracionesReales_SinEstimar()
    {
        var vistos = new List<RegistroEpisodio> { Ep(1200), Ep(1800), Ep(1500) };

        EstimadorDuracion.SegundosVistos(vistos).Should().Be(4500);
    }

    [Fact]
    public void UsaElMayorEntreDuracionYProgreso()
    {
        var vistos = new List<RegistroEpisodio> { Ep(total: 1400, progreso: 1500) };

        EstimadorDuracion.SegundosVistos(vistos).Should().Be(1500);
    }

    [Fact]
    public void SinDuracion_SeEstimaConElPromedioDeLosQueSiLaTienen()
    {
        // Promedio de duraciones conocidas: (1200 + 1800) / 2 = 1500 s → 3 episodios sin dato = 4500 s
        var vistos = new List<RegistroEpisodio> { Ep(1200), Ep(1800), Ep(), Ep(), Ep() };

        EstimadorDuracion.SegundosVistos(vistos).Should().Be(3000 + 4500);
    }

    [Fact]
    public void SinNingunaDuracionConocida_UsaVeinticuatroMinutosPorEpisodio()
    {
        var vistos = Enumerable.Range(0, 1500).Select(_ => Ep()).ToList();

        double horas = EstimadorDuracion.SegundosVistos(vistos) / 3600.0;

        horas.Should().BeApproximately(600, 1e-6);   // 1500 × 24 min
    }

    [Fact]
    public void UnProgresoParcialNoEntraEnElPromedio_PeroSiCuentaSuTiempo()
    {
        // Solo hay progreso parcial (60 s): cuenta esos 60 s, pero el promedio para estimar NO es 60 s
        var vistos = new List<RegistroEpisodio> { Ep(progreso: 60), Ep() };

        EstimadorDuracion.SegundosVistos(vistos).Should().Be(60 + EstimadorDuracion.DuracionPorDefectoSegundos);
    }
}
