using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>Volumen de la música y reproducción continua en settings.json (archivo temporal propio: nunca el del usuario).</summary>
public class SettingsServiceMusicaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AnimeLocalTracker_TestSettingsMusica_" + Guid.NewGuid().ToString("N"));
    private readonly string _archivo;

    public SettingsServiceMusicaTests()
    {
        Directory.CreateDirectory(_dir);
        _archivo = Path.Combine(_dir, "settings.json");
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(_dir, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public void Valores_PorDefecto_VolumenAlto_YReproduccionContinuaActivada()
    {
        var config = new AppSettings();

        config.VolumenMusica.Should().Be(0.8);
        config.ReproduccionContinuaMusica.Should().BeTrue();
    }

    [Fact]
    public async Task Persistencia_VolumenYContinua_DeberianSobrevivirAUnReinicio()
    {
        var primero = new SettingsService(_archivo);
        var config = primero.ObtenerConfiguracion();
        config.VolumenMusica = 0.37;
        config.ReproduccionContinuaMusica = false;
        await primero.GuardarConfiguracionAsync(config);

        var trasReiniciar = new SettingsService(_archivo).ObtenerConfiguracion();

        trasReiniciar.VolumenMusica.Should().Be(0.37);
        trasReiniciar.ReproduccionContinuaMusica.Should().BeFalse();
    }

    [Fact]
    public void Compatibilidad_UnSettingsAntiguoSinEstosCampos_DeberiaUsarLosPorDefectoSinPerderLoDemas()
    {
        File.WriteAllText(_archivo, """{ "Idioma": "en", "DescargasSimultaneas": 5 }""");

        var config = new SettingsService(_archivo).ObtenerConfiguracion();

        config.VolumenMusica.Should().Be(0.8);
        config.ReproduccionContinuaMusica.Should().BeTrue();
        config.Idioma.Should().Be("en");
        config.DescargasSimultaneas.Should().Be(5);
    }

    [Theory]
    [InlineData(7.5, 1.0)]
    [InlineData(-3.0, 0.0)]
    [InlineData(0.42, 0.42)]
    public void Saneado_UnVolumenEditadoAManoFueraDeRango_DeberiaAjustarseAlRango(double escrito, double esperado)
    {
        File.WriteAllText(_archivo, JsonSerializer.Serialize(new { VolumenMusica = escrito }));

        new SettingsService(_archivo).ObtenerConfiguracion().VolumenMusica.Should().Be(esperado);
    }

    [Fact]
    public void Saneado_UnVolumenNoNumerico_NoDeberiaImpedirArrancarNiPerderElResto()
    {
        File.WriteAllText(_archivo, """{ "VolumenMusica": "muy alto", "Idioma": "en" }""");

        var config = new SettingsService(_archivo).ObtenerConfiguracion();

        config.Should().NotBeNull();
        config.VolumenMusica.Should().Be(0.8, "si el archivo no se puede leer se arranca con los valores por defecto");
    }
}
