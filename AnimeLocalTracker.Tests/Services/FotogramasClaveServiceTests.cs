using System.Collections.Generic;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// Saltos del reproductor con la lista de fotogramas clave: instantáneos cuando hay uno cerca del destino, precisos cuando no.
/// Los instantes de prueba son los reales de Kage no Jitsuryokusha ep. 20 (AV1) alrededor del minuto 10.
/// </summary>
public class FotogramasClaveServiceTests
{
    private static readonly List<double> ClavesAv1 = [588.58, 590.75, 591.25, 591.67, 593.54, 599.67, 610.08, 615.04, 625.46, 627.92, 629.38];

    [Fact]
    public void Parsear_SoloTomaLosPaquetesConMarcaDeFotogramaClave()
    {
        string salida = "0.000000,K__\n0.041667,___\n2.002000,K_\n\n3.1,__\n";

        FotogramasClaveService.Parsear(salida).Should().Equal(0.0, 2.002);
    }

    [Fact]
    public void Adelantar_ConUnFotogramaClaveCerca_EsInstantaneoYCaeEnEl()
    {
        // Desde 600, +10 → 610: hay uno en 610,08.
        var (destino, preciso) = FotogramasClaveService.ElegirDestino(ClavesAv1, 610, TipoSalto.Adelante, desde: 600);

        preciso.Should().BeFalse();
        destino.Should().BeApproximately(610.13, 0.001, "se pide el fotograma clave + 0,05 s para que el salto rápido caiga en él");
    }

    [Fact]
    public void Adelantar_SiempreAvanzaAlMenosLaMitadDelPaso()
    {
        // Desde 612, +10 → 622: 615,04 está más cerca pero solo avanzaría 3 s (menos de la mitad del paso) — no vale; 625,46 sí.
        var (destino, preciso) = FotogramasClaveService.ElegirDestino(ClavesAv1, 622, TipoSalto.Adelante, desde: 612);

        preciso.Should().BeFalse();
        destino.Should().BeApproximately(625.51, 0.001);
    }

    [Fact]
    public void Retroceder_SiempreRetrocedeAlMenosLaMitadDelPaso()
    {
        // Desde 612, -10 → 602: 599,67 está a 2,3 s del objetivo y retrocede 12,3 s → vale; 610,08 solo retrocedería 2 s → no.
        var (destino, preciso) = FotogramasClaveService.ElegirDestino(ClavesAv1, 602, TipoSalto.Atras, desde: 612);

        preciso.Should().BeFalse();
        destino.Should().BeApproximately(599.72, 0.001);
    }

    [Fact]
    public void SinFotogramaClaveEnLaVentana_ElSaltoEsPreciso()
    {
        // 604 → el más cercano (599,67 / 610,08) queda a más de 3 s: salto preciso al segundo pedido.
        FotogramasClaveService.ElegirDestino(ClavesAv1, 604.5, TipoSalto.Libre).Should().Be((604.5, true));
    }

    [Fact]
    public void SaltarElOpening_NuncaCaeMasDeMedioSegundoDentroDelTramo()
    {
        // El opening acaba en 611: 610,08 está 0,92 s antes → dentro del tramo, no vale; 615,04 sí (4 s después) → fuera de +3 s → preciso.
        FotogramasClaveService.ElegirDestino(ClavesAv1, 611, TipoSalto.SaltarTramo).Should().Be((611, true));
        FotogramasClaveService.ElegirDestino(ClavesAv1, 610.3, TipoSalto.SaltarTramo).Preciso.Should().BeFalse();
    }

    [Fact]
    public void SinListaDeFotogramasClave_TodosLosSaltosSonPrecisos()
    {
        FotogramasClaveService.ElegirDestino(null, 300, TipoSalto.Adelante, desde: 290).Should().Be((300, true));
        FotogramasClaveService.ElegirDestino([], 300, TipoSalto.Libre).Should().Be((300, true));
    }

    [Fact]
    public void EnH264ConFotogramaClaveCada2Segundos_TodosLosSaltosSonInstantaneos()
    {
        var claves = new List<double>();
        for (double t = 0; t < 1400; t += 2.002) claves.Add(t);

        for (double desde = 50; desde < 1300; desde += 37.3)
        {
            FotogramasClaveService.ElegirDestino(claves, desde + 10, TipoSalto.Adelante, desde).Preciso.Should().BeFalse();
            FotogramasClaveService.ElegirDestino(claves, desde - 10, TipoSalto.Atras, desde).Preciso.Should().BeFalse();
        }
    }
}
