using System;
using AnimeLocalTracker.Services.Native;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>Análisis de audio del núcleo Rust a través de la frontera real (animetracker_core.dll).</summary>
public class NativeAudioTests
{
    private static readonly float[] PrimerFotogramaEsperado =
        [0.26457512f, -7.6656466f, -6.316012f, 3.7318506f, -6.200735f, 3.379668f, -7.546138f, 2.7776082f];

    [Fact]
    public void ElNucleoNativoEstaDisponibleEnLasPruebas()
    {
        // Si falla: copiar native/animetracker_core/target/release/animetracker_core.dll a AnimeLocalTracker/ y recompilar.
        NativeMethods.IsAvailable.Should().BeTrue();
    }

    [Fact]
    public void HuellaDeAudio_DeUnaSenalFija_CoincideConLaDeReferencia()
    {
        var pcm = new float[2400];
        for (int i = 0; i < pcm.Length; i++)
        {
            double t = i / 8000.0;
            double nivel = i / 800 % 2 == 0 ? 1.0 : 0.25;
            pcm[i] = (float)(0.3 * Math.Sin(2 * Math.PI * 440 * t) + 0.2 * Math.Sin(2 * Math.PI * 1250 * t + 0.5)
                             + 0.1 * Math.Sin(2 * Math.PI * 3000 * t) * nivel);
        }

        float[]? huella = NativeMethods.HuellaDeAudio(pcm);

        huella.Should().NotBeNull();
        huella!.Length.Should().Be(3 * NativeMethods.ColumnasHuella);
        for (int j = 0; j < NativeMethods.ColumnasHuella; j++)
        {
            huella[j].Should().BeApproximately(PrimerFotogramaEsperado[j], 1e-4f, $"columna {j}");
        }
    }

    [Fact]
    public void HuellaDeAudio_ConMenosDeUnFotograma_EsVacia()
    {
        NativeMethods.HuellaDeAudio(new float[799]).Should().BeEmpty();
    }

    [Fact]
    public void MejorCoincidencia_UbicaElTemaIncrustado()
    {
        float[] tema = AudioSintetico.Ruido(300, 1);
        float[] episodio = AudioSintetico.Incrustar(AudioSintetico.Ruido(1200, 3), tema, 400);

        bool ok = NativeMethods.MejorCoincidencia(episodio, 0.0, [AudioSintetico.Ruido(300, 7), tema], null, out var mejor);

        ok.Should().BeTrue();
        mejor.Should().NotBeNull();
        mejor!.Value.Indice.Should().Be(1);
        mejor.Value.Parcial.Should().Be(0);
        mejor.Value.Inicio.Should().BeApproximately(40.0, 1e-9);
        mejor.Value.Fin.Should().BeApproximately(70.0, 1e-9);
        mejor.Value.Confianza.Should().BeApproximately(0.9950893339441097, 1e-6);
    }

    [Fact]
    public void MejorCoincidencia_ConElTramoExcluido_NoDevuelveNada()
    {
        float[] tema = AudioSintetico.Ruido(300, 1);
        float[] episodio = AudioSintetico.Incrustar(AudioSintetico.Ruido(1200, 3), tema, 400);

        bool ok = NativeMethods.MejorCoincidencia(episodio, 0.0, [tema], (40.0, 70.0), out var mejor);

        ok.Should().BeTrue("no es un fallo del motor: simplemente no hay otro sitio donde suene");
        mejor.Should().BeNull();
    }

    [Fact]
    public void MejorCoincidencia_SinTemasOSinVentana_NoLlamaAlMotor()
    {
        NativeMethods.MejorCoincidencia(AudioSintetico.Ruido(100, 1), 0.0, [], null, out var sinTemas).Should().BeTrue();
        sinTemas.Should().BeNull();
        NativeMethods.MejorCoincidencia([], 0.0, [AudioSintetico.Ruido(100, 1)], null, out var sinVentana).Should().BeTrue();
        sinVentana.Should().BeNull();
    }
}
