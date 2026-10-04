using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// Un plugin C# roto o colgado no debe degradar la app (p. ej. congelar la reproducción de video):
/// se aísla, se registra y se sigue sin él.
/// </summary>
public class CSharpPluginLoaderTests : IDisposable
{
    private const string VariableLento = "ALT_TEST_PLUGIN_LENTO";

    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), "alt_plugins_" + Guid.NewGuid().ToString("N"));

    public CSharpPluginLoaderTests() => Directory.CreateDirectory(_carpeta);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(VariableLento, null);
        try { Directory.Delete(_carpeta, recursive: true); } catch { /* el ensamblado cargado puede seguir bloqueado en Windows */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>Proveedor público con constructor sin parámetros: lo que el cargador busca en un plugin.
    /// El ensamblado de tests se usa como "plugin" copiándolo a la carpeta de plugins.</summary>
    public sealed class ProveedorPluginFalso : IProveedorVideo
    {
        public ProveedorPluginFalso()
        {
            // Simula un plugin cuyo constructor se cuelga (solo cuando el test lo pide).
            if (Environment.GetEnvironmentVariable(VariableLento) == "1")
            {
                Thread.Sleep(TimeSpan.FromSeconds(4));
            }
        }

        public string Nombre => "PluginFalsoDePrueba";

        public Task<string?> BuscarUrlEpisodioAsync(IEnumerable<string> titulos, int numeroEpisodio, int? aniListId = null, string? audioPreferido = null, string? servidorPreferido = null, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<string?> GetVideoUrlAsync(string pageUrl, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }

    private void CopiarEnsambladoDeTestsComoPlugin()
    {
        string origen = typeof(ProveedorPluginFalso).Assembly.Location;
        File.Copy(origen, Path.Combine(_carpeta, "PluginDePrueba.dll"));
    }

    [Fact]
    public void CarpetaInexistente_DevuelveListaVacia()
    {
        CSharpPluginLoader.CargarProveedoresVideo(Path.Combine(_carpeta, "no_existe")).Should().BeEmpty();
    }

    [Fact]
    public void CarpetaVacia_DevuelveListaVacia()
    {
        CSharpPluginLoader.CargarProveedoresVideo(_carpeta).Should().BeEmpty();
    }

    [Fact]
    public void DllQueNoEsEnsamblado_SeIgnoraSinLanzar()
    {
        File.WriteAllText(Path.Combine(_carpeta, "roto.dll"), "esto no es un ensamblado .NET");

        var act = () => CSharpPluginLoader.CargarProveedoresVideo(_carpeta);

        act.Should().NotThrow();
        act().Should().BeEmpty();
    }

    [Fact]
    public void DllValido_CargaSusProveedores()
    {
        CopiarEnsambladoDeTestsComoPlugin();

        var proveedores = CSharpPluginLoader.CargarProveedoresVideo(_carpeta);

        proveedores.Should().Contain(p => p.Nombre == "PluginFalsoDePrueba");
    }

    [Fact]
    public void DllValido_NoSeCargaEnElContextoPorDefecto()
    {
        CopiarEnsambladoDeTestsComoPlugin();

        var proveedor = CSharpPluginLoader.CargarProveedoresVideo(_carpeta)
            .Find(p => p.Nombre == "PluginFalsoDePrueba");

        proveedor.Should().NotBeNull();
        // Aislado: el tipo viene de otro AssemblyLoadContext, no de Assembly.LoadFrom en el por defecto.
        System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(proveedor!.GetType().Assembly)
            .Should().NotBe(System.Runtime.Loader.AssemblyLoadContext.Default);
    }

    [Fact]
    public void PluginColgado_SeDescartaPorTiempoSinBloquearLaApp()
    {
        CopiarEnsambladoDeTestsComoPlugin();
        Environment.SetEnvironmentVariable(VariableLento, "1");

        var reloj = Stopwatch.StartNew();
        var proveedores = CSharpPluginLoader.CargarProveedoresVideo(_carpeta, TimeSpan.FromMilliseconds(500));
        reloj.Stop();

        proveedores.Should().BeEmpty();
        reloj.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
    }

    // === SEC-01: predicado de confianza ===

    [Fact]
    public void CargarProveedoresVideo_SiElPredicadoRechazaElArchivo_NoDeberiaCargarloNiEjecutarSuConstructor()
    {
        CopiarEnsambladoDeTestsComoPlugin();
        var consultados = new List<string>();

        var proveedores = CSharpPluginLoader.CargarProveedoresVideo(_carpeta, esConfiable: (nombre, _) => { consultados.Add(nombre); return false; });

        proveedores.Should().BeEmpty();
        consultados.Should().Equal("PluginDePrueba.dll");
    }

    [Fact]
    public void CargarProveedoresVideo_SiElPredicadoAceptaElArchivo_DeberiaCargarlo()
    {
        CopiarEnsambladoDeTestsComoPlugin();

        var proveedores = CSharpPluginLoader.CargarProveedoresVideo(_carpeta, esConfiable: (_, _) => true);

        proveedores.Should().Contain(p => p.Nombre == "PluginFalsoDePrueba");
    }

    [Fact]
    public void CargarProveedoresVideo_CargaExactamenteLosBytesQueElPredicadoComprobo()
    {
        CopiarEnsambladoDeTestsComoPlugin();
        string rutaPlugin = Path.Combine(_carpeta, "PluginDePrueba.dll");
        byte[] enDisco = File.ReadAllBytes(rutaPlugin);
        byte[]? comprobado = null;

        var proveedores = CSharpPluginLoader.CargarProveedoresVideo(_carpeta, esConfiable: (_, contenido) => { comprobado = contenido; return true; });

        proveedores.Should().Contain(p => p.Nombre == "PluginFalsoDePrueba");
        comprobado.Should().Equal(enDisco);
        // Cargado desde memoria: el archivo no queda en uso, así que cambiarlo después ya no cambia lo que se ejecutó.
        var borrar = () => File.Delete(rutaPlugin);
        borrar.Should().NotThrow("el plugin se carga de los bytes comprobados, no volviendo a leer la ruta");
    }

    private static System.Runtime.Loader.AssemblyLoadContext ContextoDe(IProveedorVideo proveedor)
        => System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(proveedor.GetType().Assembly)!;

    /// <summary>Copia junto al plugin una biblioteca de la que depende (xunit.core: varias clases de este ensamblado la usan).</summary>
    private void CopiarDependenciaJuntoAlPlugin()
    {
        string origen = typeof(FactAttribute).Assembly.Location;
        File.Copy(origen, Path.Combine(_carpeta, Path.GetFileName(origen)));
    }

    [Fact]
    public void UnaDependenciaSinConfianza_NoSeCargaDesdeLaCarpetaDePlugins()
    {
        CopiarEnsambladoDeTestsComoPlugin();
        CopiarDependenciaJuntoAlPlugin();

        var proveedor = CSharpPluginLoader.CargarProveedoresVideo(_carpeta, esConfiable: (nombre, _) => nombre == "PluginDePrueba.dll")
            .Find(p => p.Nombre == "PluginFalsoDePrueba");

        proveedor.Should().NotBeNull();
        ContextoDe(proveedor!).Assemblies.Should().NotContain(a => a.GetName().Name == "xunit.core",
            "una biblioteca dejada junto a un plugin aprobado es código igual que el plugin: sin su propia aprobación no entra");
    }

    [Fact]
    public void UnaDependenciaConConfianza_SeCargaConElPlugin()
    {
        CopiarEnsambladoDeTestsComoPlugin();
        CopiarDependenciaJuntoAlPlugin();

        var proveedor = CSharpPluginLoader.CargarProveedoresVideo(_carpeta, esConfiable: (_, _) => true)
            .Find(p => p.Nombre == "PluginFalsoDePrueba");

        proveedor.Should().NotBeNull();
        ContextoDe(proveedor!).Assemblies.Should().Contain(a => a.GetName().Name == "xunit.core");
    }
}
