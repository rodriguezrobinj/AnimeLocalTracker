using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>Visor de registros: lectura de los tres formatos de archivo y filtros del ViewModel (carpeta temporal propia).</summary>
public sealed class VisorRegistrosTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), "VisorRegistros_" + Guid.NewGuid().ToString("N"));
    private string Sesiones => Path.Combine(_carpeta, "sesiones");
    private static readonly string[] FuentesEjemplo = { "App", "DownloadService", "AnimeAv1VideoSourceResolver" };
    private static readonly string[] NivelesProblema = { "ERROR", "WARN" };

    private const string SesionEjemplo =
        "==== Sesión 2026-09-30 00:40:26 · Windows 10.0.19045.0 · .NET 8.0.31 · registro detallado: no ====\n" +
        "00:40:26.699 INFO  [App] AnimeLocalTracker iniciando (versión 1.0.5.0, x64).\n" +
        "00:40:27.100 DEBUG · [AnimeAv1VideoSourceResolver] Media remonster rechazado: malId 56690 != esperado 42203.\n" +
        "00:40:27.200 WARN  [DownloadService] No se encontró enlace para 'Re:Zero' Ep 1.\n" +
        "00:40:28.000 ERROR [App] UI Thread Exception\n" +
        "    System.InvalidOperationException: algo falló\n" +
        "       at AnimeLocalTracker.Foo()\n";

    public VisorRegistrosTests() => Directory.CreateDirectory(Sesiones);

    public void Dispose()
    {
        try { Directory.Delete(_carpeta, recursive: true); } catch { /* temporal */ }
    }

    private string EscribirSesion(string nombre, string contenido)
    {
        string ruta = Path.Combine(Sesiones, nombre);
        File.WriteAllText(ruta, contenido, new UTF8Encoding(false));
        return ruta;
    }

    // ── Lector ──

    [Fact]
    public void Analizar_Sesion_LeeNivelesFuentesContextoCabeceraYTraza()
    {
        var entradas = LectorRegistros.Analizar(SesionEjemplo, new DateTime(2026, 9, 30));

        entradas.Should().HaveCount(5);
        entradas[0].EsCabecera.Should().BeTrue();
        entradas[1].Should().BeEquivalentTo(new { Nivel = "INFO", Fuente = "App", Fecha = (DateTime?)new DateTime(2026, 9, 30, 0, 40, 26, 699) });
        entradas[2].EsContexto.Should().BeTrue();
        entradas[2].Nivel.Should().Be("DEBUG");
        entradas[4].Nivel.Should().Be("ERROR");
        entradas[4].Detalle.Should().Be("System.InvalidOperationException: algo falló\n   at AnimeLocalTracker.Foo()");
    }

    [Fact]
    public void Analizar_ErroresLogYRegistroAntiguo_LeenLaFechaCompleta()
    {
        var errores = LectorRegistros.Analizar("2026-09-29 23:34:49.123 WARN  [NyaaSourceService] sin candidatos\n", null);
        var antiguo = LectorRegistros.Analizar("[2026-09-27 18:58:13] [INFO] [App] AnimeLocalTracker iniciando.\n", null);

        errores.Single().Should().BeEquivalentTo(new { Nivel = "WARN", Fuente = "NyaaSourceService", Mensaje = "sin candidatos", Fecha = (DateTime?)new DateTime(2026, 9, 29, 23, 34, 49, 123) });
        antiguo.Single().Should().BeEquivalentTo(new { Nivel = "INFO", Fuente = "App", Fecha = (DateTime?)new DateTime(2026, 9, 27, 18, 58, 13) });
    }

    [Fact]
    public void LeerDesde_SoloLineasCompletasYContinuaDondeSeQuedo()
    {
        string ruta = EscribirSesion("2026-09-30_00-40-26.log", "00:00:00.000 INFO  [A] uno\n00:00:01.000 INFO  [A] do");

        var (texto1, sig1) = LectorRegistros.LeerDesde(ruta, 0);
        texto1.Should().Be("00:00:00.000 INFO  [A] uno\n", "la línea a medias se deja para la próxima lectura");

        File.AppendAllText(ruta, "s\n");
        var (texto2, _) = LectorRegistros.LeerDesde(ruta, sig1);
        texto2.Should().Be("00:00:01.000 INFO  [A] dos\n");
    }

    [Fact]
    public void ListarArchivos_SesionActualPrimeroLuegoErroresSesionesYElAntiguoAlFinal()
    {
        EscribirSesion("2026-09-28_10-00-00.log", "x\n");
        string actual = EscribirSesion("2026-09-30_00-40-26.log", "x\n");
        EscribirSesion("2026-09-29_10-00-00.log", "x\n");
        EscribirSesion("0000-anteriores (app.log antiguo).log", "x\n");
        File.WriteAllText(Path.Combine(_carpeta, "errores.log"), "x\n");

        var tipos = LectorRegistros.ListarArchivos(_carpeta, actual).Select(a => (a.Tipo, Path.GetFileName(a.Ruta))).ToList();

        tipos.Should().Equal(
            (TipoArchivoRegistro.SesionActual, "2026-09-30_00-40-26.log"),
            (TipoArchivoRegistro.Errores, "errores.log"),
            (TipoArchivoRegistro.Sesion, "2026-09-29_10-00-00.log"),
            (TipoArchivoRegistro.Sesion, "2026-09-28_10-00-00.log"),
            (TipoArchivoRegistro.Antiguo, "0000-anteriores (app.log antiguo).log"));
    }

    // ── ViewModel ──

    private async Task<VisorRegistrosViewModel> CrearVisorAsync()
    {
        string actual = EscribirSesion("2026-09-30_00-40-26.log", SesionEjemplo);
        var vm = new VisorRegistrosViewModel(carpeta: _carpeta, rutaSesionActual: actual);
        await vm.CargarAsync();
        return vm;
    }

    [Fact]
    public async Task CargarAsync_AbreLaSesionActual_LoMasRecienteArriba()
    {
        var vm = await CrearVisorAsync();

        vm.EsSesionActual.Should().BeTrue();
        vm.TotalEntradas.Should().Be(4, "la cabecera no cuenta como entrada");
        (vm.NumErrores, vm.NumAvisos, vm.NumInfo, vm.NumDebug).Should().Be((1, 1, 1, 1));
        vm.EntradasVisibles[0].Nivel.Should().Be("ERROR");
        vm.EntradasVisibles[^1].EsCabecera.Should().BeTrue();
        vm.Fuentes.Should().Contain(FuentesEjemplo);
    }

    [Fact]
    public async Task TextoResumen_NoCuentaElSeparadorDeSesion()
    {
        var vm = await CrearVisorAsync();

        vm.TextoResumen.Should().StartWith("4 de 4", "antes decía \"5 de 4\": contaba la cabecera como entrada visible");
        vm.Archivos[0].ToString().Should().Be(vm.Archivos[0].Etiqueta, "es lo que leen el selector y los lectores de pantalla");
    }

    [Fact]
    public async Task SoloProblemas_DejaAvisosYErrores()
    {
        var vm = await CrearVisorAsync();

        await vm.SoloProblemasCommand.ExecuteAsync(null);

        // La cabecera de la sesión (separador) se sigue viendo: indica de qué sesión son las entradas.
        vm.EntradasVisibles.Where(e => !e.EsCabecera).Select(e => e.Nivel).Should().BeEquivalentTo(NivelesProblema);
    }

    [Fact]
    public async Task FiltroPorFuenteYTexto()
    {
        var vm = await CrearVisorAsync();

        vm.FuenteSeleccionada = "App";
        await vm.AplicarFiltrosAsync();
        vm.EntradasVisibles.Should().OnlyContain(e => e.Fuente == "App");

        await vm.LimpiarFiltrosCommand.ExecuteAsync(null);
        vm.TextoBusqueda = "remonster";
        await vm.AplicarFiltrosAsync();
        vm.EntradasVisibles.Should().ContainSingle().Which.Fuente.Should().Be("AnimeAv1VideoSourceResolver");

        vm.TextoBusqueda = "Foo()"; // también busca en la traza
        await vm.AplicarFiltrosAsync();
        vm.EntradasVisibles.Should().ContainSingle().Which.Nivel.Should().Be("ERROR");
    }

    [Fact]
    public async Task RefrescarEnVivo_AnadeLoNuevoArriba()
    {
        var vm = await CrearVisorAsync();

        File.AppendAllText(Path.Combine(Sesiones, "2026-09-30_00-40-26.log"), "00:41:00.000 WARN  [PythonDaemon] RuntimeWarning: divide by zero\n");
        await vm.RefrescarEnVivoAsync();

        vm.TotalEntradas.Should().Be(5);
        vm.EntradasVisibles[0].Fuente.Should().Be("PythonDaemon");
        vm.Fuentes.Should().Contain("PythonDaemon");
    }

    [Fact]
    public async Task TextoParaCopiar_EnOrdenCronologicoConLaTraza()
    {
        var vm = await CrearVisorAsync();

        var texto = vm.TextoParaCopiar();

        texto.IndexOf("iniciando", StringComparison.Ordinal).Should().BeLessThan(texto.IndexOf("UI Thread Exception", StringComparison.Ordinal));
        texto.Should().Contain("    System.InvalidOperationException: algo falló");
        texto.Should().Contain("DEBUG · [AnimeAv1VideoSourceResolver]");
    }

    [Fact]
    public async Task AlternarDetalle_ExpandeLaTraza()
    {
        var vm = await CrearVisorAsync();
        var error = vm.EntradasVisibles.First(e => e.TieneDetalle);

        vm.AlternarDetalleCommand.Execute(error);

        error.Expandido.Should().BeTrue();
        error.LineasDetalle.Should().Be(2);
    }
}
