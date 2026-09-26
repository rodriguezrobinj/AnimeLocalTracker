using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AnimeLocalTracker.Core;
using AnimeLocalTracker.Models;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Core;

/// <summary>SEC-01: los plugins solo corren si están activados Y el usuario confió en ese contenido exacto (huella SHA-256).</summary>
public class ConfianzaPluginsTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), "alt_confianza_" + Guid.NewGuid().ToString("N"));

    public ConfianzaPluginsTests() => Directory.CreateDirectory(_carpeta);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(_carpeta, true); } catch { /* ignore */ }
    }

    private string Archivo(string nombre, string contenido)
    {
        string ruta = Path.Combine(_carpeta, nombre);
        File.WriteAllText(ruta, contenido, Encoding.UTF8);
        return ruta;
    }

    [Fact]
    public void CalcularSha256_ConVectorConocido_DeberiaDarLaHuellaEsperada()
    {
        // SHA-256("abc") — vector de prueba estándar (FIPS 180-2)
        string ruta = Path.Combine(_carpeta, "abc.bin");
        File.WriteAllBytes(ruta, Encoding.ASCII.GetBytes("abc"));

        ConfianzaPlugins.CalcularSha256(ruta).Should().Be("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD");
    }

    [Theory]
    [InlineData("plugin.dll", true)]
    [InlineData("Plugin.PY", true)]
    [InlineData("mi proveedor.dll", true)]
    [InlineData("..\\otro.py", false)]
    [InlineData("../otro.py", false)]
    [InlineData("sub\\p.dll", false)]
    [InlineData("C:\\Windows\\x.dll", false)]
    [InlineData("p.dll:flujo", false)]
    [InlineData("..", false)]
    [InlineData(".", false)]
    [InlineData("script.exe", false)]
    [InlineData("readme.txt", false)]
    [InlineData("sinextension", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void EsNombreDePluginValido_DeberiaAceptarSoloNombresSimplesConExtensionDePlugin(string? nombre, bool esperado)
    {
        ConfianzaPlugins.EsNombreDePluginValido(nombre).Should().Be(esperado);
    }

    [Fact]
    public void Evaluar_SinEntrada_DeberiaSerSinConfiar()
    {
        ConfianzaPlugins.Evaluar(new AppSettings(), "a.dll", "HASH").Should().Be(EstadoConfianzaPlugin.SinConfiar);
    }

    [Fact]
    public void Evaluar_ConHuellaIgual_DeberiaSerConfiableSinImportarMayusculas()
    {
        var cfg = new AppSettings { PluginsConfiables = new Dictionary<string, string> { ["A.DLL"] = "abcdef" } };

        ConfianzaPlugins.Evaluar(cfg, "a.dll", "ABCDEF").Should().Be(EstadoConfianzaPlugin.Confiable);
    }

    [Fact]
    public void Evaluar_ConHuellaDistinta_DeberiaSerModificado()
    {
        var cfg = new AppSettings { PluginsConfiables = new Dictionary<string, string> { ["a.dll"] = "AAAA" } };

        ConfianzaPlugins.Evaluar(cfg, "a.dll", "BBBB").Should().Be(EstadoConfianzaPlugin.Modificado);
    }

    [Fact]
    public void Evaluar_ConHuellaAprobadaVacia_DeberiaSerSinConfiar()
    {
        var cfg = new AppSettings { PluginsConfiables = new Dictionary<string, string> { ["a.dll"] = "  " } };

        ConfianzaPlugins.Evaluar(cfg, "a.dll", "BBBB").Should().Be(EstadoConfianzaPlugin.SinConfiar);
    }

    [Fact]
    public void PuedeEjecutarse_PorDefecto_NoDeberiaEjecutarNada()
    {
        string ruta = Archivo("p.py", "print('hola')");

        ConfianzaPlugins.PuedeEjecutarse(new AppSettings(), ruta).Should().BeFalse("los plugins vienen apagados");
    }

    [Fact]
    public void PuedeEjecutarse_ActivadoPeroSinConfiarEnElArchivo_DeberiaRechazar()
    {
        string ruta = Archivo("p.py", "print('hola')");

        ConfianzaPlugins.PuedeEjecutarse(new AppSettings { PluginsHabilitados = true }, ruta).Should().BeFalse();
    }

    [Fact]
    public void PuedeEjecutarse_ConfiadoPeroInterruptorApagado_DeberiaRechazar()
    {
        string ruta = Archivo("p.py", "print('hola')");
        var cfg = new AppSettings
        {
            PluginsHabilitados = false,
            PluginsConfiables = new Dictionary<string, string> { ["p.py"] = ConfianzaPlugins.CalcularSha256(ruta) }
        };

        ConfianzaPlugins.PuedeEjecutarse(cfg, ruta).Should().BeFalse();
    }

    [Fact]
    public void PuedeEjecutarse_ActivadoYConfiado_DeberiaPermitir()
    {
        string ruta = Archivo("p.py", "print('hola')");
        var cfg = new AppSettings
        {
            PluginsHabilitados = true,
            PluginsConfiables = new Dictionary<string, string> { ["p.py"] = ConfianzaPlugins.CalcularSha256(ruta) }
        };

        ConfianzaPlugins.PuedeEjecutarse(cfg, ruta).Should().BeTrue();
    }

    [Fact]
    public void PuedeEjecutarse_SiElArchivoCambiaDespuesDeConfiar_DeberiaRechazar()
    {
        // El caso de ataque: otro programa reemplaza el plugin aprobado por uno malicioso
        string ruta = Archivo("p.py", "print('inofensivo')");
        var cfg = new AppSettings
        {
            PluginsHabilitados = true,
            PluginsConfiables = new Dictionary<string, string> { ["p.py"] = ConfianzaPlugins.CalcularSha256(ruta) }
        };
        File.WriteAllText(ruta, "import os; os.system('malicioso')");

        ConfianzaPlugins.PuedeEjecutarse(cfg, ruta).Should().BeFalse();
    }

    [Fact]
    public void PuedeEjecutarse_ConArchivoInexistente_DeberiaRechazarSinLanzar()
    {
        var cfg = new AppSettings { PluginsHabilitados = true, PluginsConfiables = new Dictionary<string, string> { ["no.py"] = "AA" } };

        ConfianzaPlugins.PuedeEjecutarse(cfg, Path.Combine(_carpeta, "no.py")).Should().BeFalse();
    }
}
