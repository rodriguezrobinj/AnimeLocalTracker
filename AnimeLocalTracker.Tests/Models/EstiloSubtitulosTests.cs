using System;
using System.IO;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Models;

public class EstiloSubtitulosTests : IDisposable
{
    private readonly string _dir;
    private readonly string _archivo;

    public EstiloSubtitulosTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "AnimeLocalTracker_TestEstilo_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _archivo = Path.Combine(_dir, "settings.json");
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void PorDefecto_DeberiaConservarElAspectoOriginalDeLosSubtitulos()
    {
        var estilo = new EstiloSubtitulos();

        estilo.Fuente.Should().Be("Trebuchet MS");
        estilo.Tamano.Should().Be(44);
        estilo.Negrita.Should().BeTrue();
        estilo.Cursiva.Should().BeFalse();
        estilo.Subrayado.Should().BeFalse();
        estilo.ColorTexto.Should().Be("#FFFFFF");
        estilo.ColorContorno.Should().Be("#000000");
        estilo.OpacidadFondo.Should().Be(0, "sin caja de fondo, como antes");
        estilo.GrosorBorde.Should().Be(0);
    }

    [Fact]
    public void Normalizar_ConValoresFueraDeRango_DeberiaAjustarlosALosLimites()
    {
        var estilo = new EstiloSubtitulos
        {
            Tamano = 500,
            GrosorContorno = -4,
            OpacidadFondo = 900,
            GrosorBorde = 99
        };

        var normal = estilo.Normalizar();

        normal.Tamano.Should().Be(EstiloSubtitulos.TamanoMaximo);
        normal.GrosorContorno.Should().Be(0);
        normal.OpacidadFondo.Should().Be(100);
        normal.GrosorBorde.Should().Be(EstiloSubtitulos.GrosorBordeMaximo);
    }

    [Fact]
    public void Normalizar_ConTamanoMuyPequenoONaN_DeberiaUsarMinimoOValorPorDefecto()
    {
        new EstiloSubtitulos { Tamano = 3 }.Normalizar().Tamano.Should().Be(EstiloSubtitulos.TamanoMinimo);
        new EstiloSubtitulos { Tamano = double.NaN }.Normalizar().Tamano.Should().Be(44);
    }

    [Theory]
    [InlineData("rojo")]
    [InlineData("#FFF")]
    [InlineData("#GGGGGG")]
    [InlineData("")]
    [InlineData(null)]
    public void Normalizar_ConColorInvalido_DeberiaVolverAlColorPorDefecto(string? color)
    {
        var normal = new EstiloSubtitulos { ColorTexto = color!, ColorContorno = color!, ColorBorde = color! }.Normalizar();

        normal.ColorTexto.Should().Be("#FFFFFF");
        normal.ColorContorno.Should().Be("#000000");
        normal.ColorBorde.Should().Be("#FFFFFF");
    }

    [Fact]
    public void Normalizar_ConColorValidoEnMinusculas_DeberiaConservarloEnMayusculas()
    {
        new EstiloSubtitulos { ColorTexto = "#ffe14d" }.Normalizar().ColorTexto.Should().Be("#FFE14D");
    }

    [Fact]
    public void Normalizar_ConFuenteVacia_DeberiaUsarLaPorDefecto()
    {
        new EstiloSubtitulos { Fuente = "  " }.Normalizar().Fuente.Should().Be(EstiloSubtitulos.FuentePorDefecto);
    }

    [Fact]
    public void Normalizar_NoDeberiaModificarLaInstanciaOriginal()
    {
        var original = new EstiloSubtitulos { Tamano = 500 };

        var normal = original.Normalizar();

        original.Tamano.Should().Be(500);
        normal.Should().NotBeSameAs(original);
    }

    [Fact]
    public void Paleta_DeberiaTenerColoresValidosYSinRepetir()
    {
        EstiloSubtitulos.Paleta.Should().OnlyHaveUniqueItems();
        foreach (var (clave, hex) in EstiloSubtitulos.Paleta)
        {
            new EstiloSubtitulos { ColorTexto = hex }.Normalizar().ColorTexto.Should().Be(hex, $"{clave} debe ser un #RRGGBB válido");
        }
    }

    [Fact]
    public void Paleta_TodasLasClavesDeberianEstarTraducidas()
    {
        foreach (var (clave, _) in EstiloSubtitulos.Paleta)
        {
            LocalizationService.T(clave).Should().NotBe(clave, $"falta la traducción de {clave}");
        }
    }

    [Fact]
    public void SettingsService_ConArchivoAnteriorSinEstilo_DeberiaUsarElEstiloPorDefecto()
    {
        // settings.json de antes de que existiera el estilo de subtítulos
        File.WriteAllText(_archivo, "{ \"SubtitulosPorDefecto\": false }");

        var config = new SettingsService(_archivo).ObtenerConfiguracion();

        config.EstiloSubtitulos.Should().NotBeNull();
        config.EstiloSubtitulos.Tamano.Should().Be(44);
        config.EstiloSubtitulos.ColorTexto.Should().Be("#FFFFFF");
    }

    [Fact]
    public void SettingsService_ConEstiloNuloOEditadoAMano_DeberiaSanearlo()
    {
        File.WriteAllText(_archivo, "{ \"EstiloSubtitulos\": { \"Tamano\": 9999, \"ColorTexto\": \"azul\", \"OpacidadFondo\": -5 } }");

        var estilo = new SettingsService(_archivo).ObtenerConfiguracion().EstiloSubtitulos;

        estilo.Tamano.Should().Be(EstiloSubtitulos.TamanoMaximo);
        estilo.ColorTexto.Should().Be("#FFFFFF");
        estilo.OpacidadFondo.Should().Be(0);

        File.WriteAllText(_archivo, "{ \"EstiloSubtitulos\": null }");
        new SettingsService(_archivo).ObtenerConfiguracion().EstiloSubtitulos.Tamano.Should().Be(44);
    }

    [Fact]
    public async Task SettingsService_GuardarYRecargar_DeberiaConservarElEstilo()
    {
        var sut = new SettingsService(_archivo);
        var config = sut.ObtenerConfiguracion();
        config.EstiloSubtitulos = new EstiloSubtitulos
        {
            Fuente = "Verdana", Tamano = 60, Negrita = false, Cursiva = true, Subrayado = true,
            ColorTexto = "#FFE14D", ColorContorno = "#4DE1FF", GrosorContorno = 5,
            OpacidadFondo = 55, GrosorBorde = 2, ColorBorde = "#FF5A5A"
        };

        await sut.GuardarConfiguracionAsync(config);
        var recargado = new SettingsService(_archivo).ObtenerConfiguracion().EstiloSubtitulos;

        recargado.Should().BeEquivalentTo(config.EstiloSubtitulos);
    }
}
