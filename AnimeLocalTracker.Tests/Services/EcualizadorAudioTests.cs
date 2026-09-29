using System.Linq;
using AnimeLocalTracker.Core;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>Ecualizador de 10 bandas del reproductor: ganancias válidas, ajustes predefinidos y la cadena de filtros de FFmpeg.</summary>
public class EcualizadorAudioTests
{
    [Fact]
    public void Normalizar_SinAjustesGuardados_DaDiezBandasPlanas()
    {
        EcualizadorAudio.Normalizar(null).Should().Equal(Enumerable.Repeat(0d, 10));
    }

    [Fact]
    public void Normalizar_CorrigeUnArchivoDeAjustesRaro()
    {
        var ganancias = EcualizadorAudio.Normalizar(new[] { 30, -40, double.NaN, 3.14159 });

        ganancias.Should().HaveCount(10);
        ganancias.Take(4).Should().Equal(12, -12, 0, 3.1);
        ganancias.Skip(4).Should().OnlyContain(g => g == 0);
    }

    [Fact]
    public void PresetDe_ReconoceElAjuste_OPersonalizadoSiSeTocoUnaBanda()
    {
        var cine = EcualizadorAudio.Presets.Single(p => p.Clave == "Cine").Ganancias.ToList();
        EcualizadorAudio.PresetDe(cine).Should().Be("Cine");

        cine[4] = 2;
        EcualizadorAudio.PresetDe(cine).Should().Be(EcualizadorAudio.PresetPersonalizado);
    }

    [Fact]
    public void Preamplificacion_BajaLoQueSubeLaBandaMasAlta_ParaQueNoSature()
    {
        EcualizadorAudio.Preamplificacion(new double[] { 6, 5, 4, 2, 0, 0, 0, 0, 0, 0 }).Should().Be(-6);
        EcualizadorAudio.Preamplificacion(new double[] { -3, -2, 0, 0, 0, 0, 0, 0, 0, -1 }).Should().Be(0);
    }

    [Fact]
    public void ConstruirFiltros_ApagadoYSinModoNoche_NoAñadeNada()
    {
        EcualizadorAudio.ConstruirFiltros(false, new double[10], false, "x").Should().BeEmpty();
    }

    [Fact]
    public void ConstruirFiltros_Activo_PreamplificadorLuegoDiezBandasYElModoNocheAlFinal()
    {
        var graves = EcualizadorAudio.Presets.Single(p => p.Clave == "GravesPotentes").Ganancias;

        var filtros = EcualizadorAudio.ConstruirFiltros(true, graves, true, "threshold=0.089");

        filtros.Should().HaveCount(12);
        filtros[0].Should().Be(new FiltroAudio("eq_pre", "volume", "volume=-6.0dB"));
        filtros.Skip(1).Take(10).Select(f => f.Id).Should().Equal(Enumerable.Range(0, 10).Select(EcualizadorAudio.IdBanda));
        filtros[1].Should().Be(new FiltroAudio("eq_b0", "equalizer", "f=31:t=o:w=1:g=6.0"));
        filtros[10].Argumentos.Should().Be("f=16000:t=o:w=1:g=0.0");
        filtros[11].Should().Be(new FiltroAudio("modonoche", "acompressor", "threshold=0.089"));
    }

    [Theory]
    [InlineData(31, "31")]
    [InlineData(1000, "1k")]
    [InlineData(16000, "16k")]
    public void EtiquetaFrecuencia_EsCorta(int hz, string esperado)
    {
        EcualizadorAudio.EtiquetaFrecuencia(hz).Should().Be(esperado);
    }
}
