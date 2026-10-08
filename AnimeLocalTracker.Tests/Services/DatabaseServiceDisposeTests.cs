using System;
using System.IO;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// Al desechar el servicio debe cerrarse la conexión: con ella abierta Windows no deja borrar la base ni sus archivos auxiliares
/// (-wal y -shm), así que las pruebas dejaban cientos de bases en %TEMP%. El cierre limpio también vuelca el WAL al archivo principal.
/// </summary>
public class DatabaseServiceDisposeTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), $"AnimeTracker_Dispose_{Guid.NewGuid():N}");

    public DatabaseServiceDisposeTests() => Directory.CreateDirectory(_carpeta);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(_carpeta, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task AlDesecharElServicio_LaBaseYSusArchivosAuxiliaresSePuedenBorrar()
    {
        string ruta = Path.Combine(_carpeta, "biblioteca.db");
        var sut = new DatabaseService(ruta);
        await sut.InicializarBaseDatosAsync();
        await sut.GuardarAnimeAsync(new AnimeItem { AniListId = 1, Titulo = "Frieren" });

        sut.Dispose();

        var borrar = () => File.Delete(ruta);
        borrar.Should().NotThrow("la conexión ya no mantiene el archivo abierto");
        File.Exists(ruta + "-wal").Should().BeFalse("el cierre limpio elimina el WAL");
        File.Exists(ruta + "-shm").Should().BeFalse();
    }

    [Fact]
    public async Task AlDesecharElServicio_LosDatosGuardadosSiguenAhiParaOtraInstancia()
    {
        string ruta = Path.Combine(_carpeta, "biblioteca.db");
        var primera = new DatabaseService(ruta);
        await primera.InicializarBaseDatosAsync();
        await primera.GuardarAnimeAsync(new AnimeItem { AniListId = 1, Titulo = "Frieren" });
        primera.Dispose();

        using var segunda = new DatabaseService(ruta);
        await segunda.InicializarBaseDatosAsync();

        (await segunda.ObtenerTodosLosAnimesAsync()).Should().ContainSingle().Which.Titulo.Should().Be("Frieren");
    }

    [Fact]
    public void DesecharUnServicioSinInicializar_NoFalla_NiDosVeces()
    {
        var sut = new DatabaseService(Path.Combine(_carpeta, "biblioteca.db"));

        sut.Dispose();
        var otraVez = () => sut.Dispose();

        otraVez.Should().NotThrow();
    }
}
