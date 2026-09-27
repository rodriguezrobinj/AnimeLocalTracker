using System;
using System.Collections.Generic;
using System.Linq;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// Varios trozos por petición cuando el servidor tarda en conectar (MP4Upload cierra la conexión
/// tras cada respuesta, y a3.mp4upload.com tarda 14–26 s en negociarla).
/// </summary>
public class AgrupadorTrozosTests
{
    private static readonly TimeSpan Umbral = TimeSpan.FromSeconds(5);
    private static readonly int[][] TramosEsperadosAlReanudar = { new[] { 1, 2 }, new[] { 4 } };

    [Fact]
    public void TrozosPorPeticion_SinMedidasOServidorRapido_PideUnoSolo()
    {
        var agrupador = new AgrupadorTrozos(Umbral);
        agrupador.TrozosPorPeticion(restantes: 100, conexiones: 8).Should().Be(1);

        agrupador.RegistrarLatencia(TimeSpan.FromSeconds(0.8)); // a4.mp4upload.com
        agrupador.TrozosPorPeticion(100, 8).Should().Be(1);
    }

    [Fact]
    public void TrozosPorPeticion_ServidorLento_PideDos_YMuyLento_PideTres()
    {
        var lento = new AgrupadorTrozos(Umbral);
        lento.RegistrarLatencia(TimeSpan.FromSeconds(7));
        lento.TrozosPorPeticion(100, 8).Should().Be(2);

        var muyLento = new AgrupadorTrozos(Umbral);
        muyLento.RegistrarLatencia(TimeSpan.FromSeconds(20)); // a3.mp4upload.com
        muyLento.TrozosPorPeticion(100, 8).Should().Be(AgrupadorTrozos.MaxTrozosPorPeticion);
    }

    [Fact]
    public void TrozosPorPeticion_CercaDelFinal_NoDejaConexionesSinTrabajo()
    {
        var agrupador = new AgrupadorTrozos(Umbral);
        agrupador.RegistrarLatencia(TimeSpan.FromSeconds(20));

        agrupador.TrozosPorPeticion(restantes: 20, conexiones: 8).Should().Be(2);
        agrupador.TrozosPorPeticion(restantes: 10, conexiones: 8).Should().Be(1);
    }

    [Fact]
    public void RegistrarLatencia_UnaRespuestaRapidaSueltaNoBorraLaMediaLenta()
    {
        var agrupador = new AgrupadorTrozos(Umbral);
        agrupador.RegistrarLatencia(TimeSpan.FromSeconds(20));
        agrupador.RegistrarLatencia(TimeSpan.FromSeconds(1));

        agrupador.LatenciaMedia!.Value.Should().BeGreaterThan(Umbral);
    }

    private static List<SegmentState> Trozos(params (long Hechos, long Tamano)[] estado)
    {
        var lista = new List<SegmentState>();
        long inicio = 0;
        foreach (var (hechos, tamano) in estado)
        {
            lista.Add(new SegmentState { Start = inicio, End = inicio + tamano - 1, CurrentOffset = inicio + hechos });
            inicio += tamano;
        }
        return lista;
    }

    [Fact]
    public void ArmarTramos_TrozosSinEmpezar_VanEnUnSoloTramo()
    {
        var trozos = Trozos((0, 10), (0, 10), (0, 10));

        var tramos = AgrupadorTrozos.ArmarTramos(trozos, 0, 2);

        tramos.Should().ContainSingle().Which.Should().HaveCount(3);
    }

    [Fact]
    public void ArmarTramos_AlReanudar_SaltaLosCompletosYUnTrozoAMedioEmpiezaTramo()
    {
        // completo | a medias | sin empezar | completo | sin empezar
        var trozos = Trozos((10, 10), (4, 10), (0, 10), (10, 10), (0, 10));

        var tramos = AgrupadorTrozos.ArmarTramos(trozos, 0, 4);

        tramos.Select(t => t.Select(x => trozos.IndexOf(x)).ToArray()).Should().BeEquivalentTo(TramosEsperadosAlReanudar, o => o.WithStrictOrdering());
    }

    [Fact]
    public void ArmarTramos_UnTrozoAMedioTrasOtroSinEmpezar_NoSeJuntan()
    {
        // Juntarlos pediría otra vez los bytes que el segundo ya tiene (o dejaría un hueco).
        var trozos = Trozos((0, 10), (3, 10));

        var tramos = AgrupadorTrozos.ArmarTramos(trozos, 0, 1);

        tramos.Should().HaveCount(2);
    }
}
