using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using FluentAssertions;
using SQLite;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>Historial de descargas: migración v7, orden, retención, borrado selectivo y limpieza total.</summary>
public class DatabaseServiceDescargasTests : IDisposable
{
    private readonly string _rutaDb;
    private readonly DatabaseService _sut;

    public DatabaseServiceDescargasTests()
    {
        _rutaDb = Path.Combine(Path.GetTempPath(), $"AnimeTracker_Descargas_{Guid.NewGuid():N}.db");
        _sut = new DatabaseService(_rutaDb);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _sut.Dispose();
        try { if (File.Exists(_rutaDb)) File.Delete(_rutaDb); } catch { /* ignore */ }
    }

    private static DescargaHistorial Descarga(int episodio, DateTime fechaUtc, bool completada = true, string? error = null) => new()
    {
        AniListId = 10,
        AnimeTitulo = "Frieren",
        NumeroEpisodio = episodio,
        CarpetaDestino = @"C:\Anime\Frieren",
        RutaArchivo = completada ? $@"C:\Anime\Frieren\Episodio {episodio:D2}.mp4" : string.Empty,
        TamanoBytes = completada ? 350L * 1024 * 1024 : 0,
        FechaUtc = fechaUtc,
        Completada = completada,
        Error = error,
        TitulosAlternativos = "Sousou no Frieren | Frieren: Beyond Journey's End"
    };

    [Fact]
    public async Task Migracion_CreaLaTablaYElIndicePorFecha()
    {
        await _sut.InicializarBaseDatosAsync();

        using var conexion = new SQLiteConnection(_rutaDb);
        conexion.ExecuteScalar<int>("PRAGMA user_version;").Should().BeGreaterThanOrEqualTo(7);
        conexion.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'DescargaHistorial';").Should().Be(1);
        conexion.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'IX_DescargaHistorial_FechaUtc';").Should().Be(1);
    }

    [Fact]
    public async Task Guardar_YObtener_DevuelveLoMasRecientePrimeroYConservaTodosLosCampos()
    {
        await _sut.InicializarBaseDatosAsync();
        var hace2Dias = DateTime.UtcNow.AddDays(-2);
        var ahora = DateTime.UtcNow;

        await _sut.GuardarDescargaHistorialAsync(Descarga(1, hace2Dias));
        await _sut.GuardarDescargaHistorialAsync(Descarga(2, ahora, completada: false, error: "El servidor cerró la conexión"));

        var historial = await _sut.ObtenerDescargasHistorialAsync();

        historial.Select(d => d.NumeroEpisodio).Should().Equal(2, 1);
        var fallida = historial[0];
        fallida.Completada.Should().BeFalse();
        fallida.Error.Should().Be("El servidor cerró la conexión");
        fallida.CarpetaDestino.Should().Be(@"C:\Anime\Frieren");
        fallida.TitulosAlternativos.Should().Contain("Sousou no Frieren");
        var ok = historial[1];
        ok.Completada.Should().BeTrue();
        ok.TamanoBytes.Should().Be(350L * 1024 * 1024);
        ok.RutaArchivo.Should().EndWith("Episodio 01.mp4");
    }

    [Fact]
    public async Task Obtener_RespetaElLimite()
    {
        await _sut.InicializarBaseDatosAsync();
        for (int i = 1; i <= 5; i++) await _sut.GuardarDescargaHistorialAsync(Descarga(i, DateTime.UtcNow.AddMinutes(i)));

        (await _sut.ObtenerDescargasHistorialAsync(limite: 3)).Select(d => d.NumeroEpisodio).Should().Equal(5, 4, 3);
    }

    [Fact]
    public async Task Guardar_ConservaSoloLas500MasRecientes()
    {
        // Sin retención la tabla crecería sin límite con el uso diario.
        await _sut.InicializarBaseDatosAsync();
        var baseFecha = DateTime.UtcNow.AddDays(-30);
        for (int i = 1; i <= 510; i++) await _sut.GuardarDescargaHistorialAsync(Descarga(i, baseFecha.AddMinutes(i)));

        var todas = await _sut.ObtenerDescargasHistorialAsync(limite: 1000);

        todas.Should().HaveCount(500);
        todas.Min(d => d.NumeroEpisodio).Should().Be(11, "se descartaron las 10 más antiguas");
        todas.Max(d => d.NumeroEpisodio).Should().Be(510);
    }

    [Fact]
    public async Task Eliminar_QuitaSoloEsaFila()
    {
        await _sut.InicializarBaseDatosAsync();
        await _sut.GuardarDescargaHistorialAsync(Descarga(1, DateTime.UtcNow.AddMinutes(-2)));
        await _sut.GuardarDescargaHistorialAsync(Descarga(2, DateTime.UtcNow.AddMinutes(-1)));
        var primera = (await _sut.ObtenerDescargasHistorialAsync()).Single(d => d.NumeroEpisodio == 1);

        await _sut.EliminarDescargaHistorialAsync(primera.Id);

        (await _sut.ObtenerDescargasHistorialAsync()).Select(d => d.NumeroEpisodio).Should().Equal(2);
    }

    [Fact]
    public async Task Limpiar_SoloFallidas_ConservaLasCompletadas()
    {
        await _sut.InicializarBaseDatosAsync();
        await _sut.GuardarDescargaHistorialAsync(Descarga(1, DateTime.UtcNow.AddMinutes(-3)));
        await _sut.GuardarDescargaHistorialAsync(Descarga(2, DateTime.UtcNow.AddMinutes(-2), completada: false, error: "x"));
        await _sut.GuardarDescargaHistorialAsync(Descarga(3, DateTime.UtcNow.AddMinutes(-1), completada: false, error: "y"));

        await _sut.LimpiarDescargasHistorialAsync(soloFallidas: true);

        (await _sut.ObtenerDescargasHistorialAsync()).Select(d => d.NumeroEpisodio).Should().Equal(1);
    }

    [Fact]
    public async Task Limpiar_Todo_VaciaElHistorial()
    {
        await _sut.InicializarBaseDatosAsync();
        await _sut.GuardarDescargaHistorialAsync(Descarga(1, DateTime.UtcNow));

        await _sut.LimpiarDescargasHistorialAsync();

        (await _sut.ObtenerDescargasHistorialAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task VaciarBiblioteca_TambienBorraElHistorialDeDescargas()
    {
        // PRI-01 "Borrar todos mis datos": el historial guarda títulos y rutas del usuario y no debe sobrevivir.
        await _sut.InicializarBaseDatosAsync();
        await _sut.GuardarDescargaHistorialAsync(Descarga(1, DateTime.UtcNow));

        await _sut.VaciarBibliotecaAsync();

        (await _sut.ObtenerDescargasHistorialAsync()).Should().BeEmpty();
    }
}
