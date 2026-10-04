using System;
using System.IO;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>"Borrar todos mis datos": siempre sobre carpetas temporales, nunca sobre los datos reales.</summary>
public sealed class BorradoTotalDeDatosTests : IDisposable
{
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), "ALT_borrado_" + Guid.NewGuid().ToString("N"));

    public BorradoTotalDeDatosTests() => Directory.CreateDirectory(_raiz);

    public void Dispose()
    {
        try { Directory.Delete(_raiz, recursive: true); } catch { /* limpieza best-effort */ }
    }

    private string CarpetaConDatos(string nombre)
    {
        string carpeta = Path.Combine(_raiz, nombre);
        Directory.CreateDirectory(Path.Combine(carpeta, "Music", "123"));
        File.WriteAllText(Path.Combine(carpeta, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(carpeta, "cola_descargas.json"), "[]");
        File.WriteAllText(Path.Combine(carpeta, "Music", "123", "OP1.mp3"), "audio");
        return carpeta;
    }

    [Fact]
    public void BorrarCarpetas_BorraLaCarpetaEnteraConTodoLoQueTenga()
    {
        string datos = CarpetaConDatos("datos");
        string temporales = CarpetaConDatos("temporales");

        var restos = BorradoTotalDeDatos.BorrarCarpetas([datos, temporales, Path.Combine(_raiz, "no_existe")], intentos: 1, esperaMs: 0);

        restos.Should().BeEmpty();
        Directory.Exists(datos).Should().BeFalse();
        Directory.Exists(temporales).Should().BeFalse();
    }

    [Fact]
    public void BorrarCarpetas_ConUnArchivoEnUso_BorraElRestoYAvisaDeLoQueQueda()
    {
        string datos = CarpetaConDatos("datos");
        string enUso = Path.Combine(datos, "biblioteca.db");
        File.WriteAllText(enUso, "abierta");

        using (new FileStream(enUso, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var restos = BorradoTotalDeDatos.BorrarCarpetas([datos], intentos: 2, esperaMs: 10);

            restos.Should().Equal(datos);
            File.Exists(Path.Combine(datos, "settings.json")).Should().BeFalse("lo que no está en uso se borra aunque otro archivo lo esté");
            File.Exists(Path.Combine(datos, "Music", "123", "OP1.mp3")).Should().BeFalse();
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("AnimeLocalTrackerData")]   // relativa: se resolvería contra la carpeta actual, que puede ser cualquiera
    [InlineData(@"C:\")]
    [InlineData(@"D:")]
    public void BorrarCarpetas_NuncaTocaRutasRelativasNiLaRaizDeUnaUnidad(string ruta)
    {
        BorradoTotalDeDatos.EsCarpetaBorrable(ruta).Should().BeFalse();
    }

    [Theory]
    [InlineData("app.exe --borrar-datos 4321", true, 4321)]
    [InlineData("app.exe --BORRAR-DATOS 7", true, 7)]
    [InlineData("app.exe --borrar-datos", true, 0)]          // sin proceso al que esperar
    [InlineData("app.exe --borrar-datos abc", true, 0)]
    [InlineData("app.exe --bandeja", false, 0)]
    [InlineData("app.exe", false, 0)]
    public void TryLeerPeticion_SoloConElArgumentoExacto(string lineaDeOrdenes, bool esperado, int pidEsperado)
    {
        BorradoTotalDeDatos.TryLeerPeticion(lineaDeOrdenes.Split(' '), out int pid).Should().Be(esperado);
        pid.Should().Be(pidEsperado);
    }
}
