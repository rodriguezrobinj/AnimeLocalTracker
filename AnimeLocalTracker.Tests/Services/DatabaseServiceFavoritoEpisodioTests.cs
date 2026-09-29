using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>Marcar un episodio como favorito solo cambia esa marca: antes ponía a cero el punto donde te habías quedado.</summary>
public class DatabaseServiceFavoritoEpisodioTests : IDisposable
{
    private readonly string _rutaDb;
    private readonly DatabaseService _sut;

    public DatabaseServiceFavoritoEpisodioTests()
    {
        _rutaDb = Path.Combine(Path.GetTempPath(), $"AnimeTracker_Favorito_{Guid.NewGuid():N}.db");
        _sut = new DatabaseService(_rutaDb);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _sut.Dispose();
        try { if (File.Exists(_rutaDb)) File.Delete(_rutaDb); } catch { /* ignore */ }
    }

    [Fact]
    public async Task MarcarFavorito_ConservaElProgresoYElHistorial()
    {
        await _sut.InicializarBaseDatosAsync();
        var visto = new DateTime(2026, 9, 20, 18, 0, 0, DateTimeKind.Utc);
        await _sut.GuardarRegistroEpisodioAsync(new RegistroEpisodio
        {
            AniListId = 10, NumeroEpisodio = 5, RutaArchivo = @"C:\Anime\Frieren\Episodio 05.mp4",
            ProgresoSegundos = 612, TotalSegundos = 1420, UltimaReproduccion = visto,
        });

        await _sut.GuardarFavoritoEpisodioAsync(10, 5, true, @"C:\Anime\Frieren\Episodio 05.mp4");

        var registro = (await _sut.ObtenerRegistrosPorAnimeAsync(10)).Single();
        registro.FavoritoLocal.Should().BeTrue();
        registro.ProgresoSegundos.Should().Be(612, "marcar como favorito no debe borrar por dónde ibas");
        registro.TotalSegundos.Should().Be(1420);
        registro.UltimaReproduccion.Should().NotBeNull();
        registro.VistoLocal.Should().BeFalse();
    }

    [Fact]
    public async Task SinRegistroPrevio_LoCreaSoloConLaMarca_SinFabricarHistorial()
    {
        await _sut.InicializarBaseDatosAsync();

        await _sut.GuardarFavoritoEpisodioAsync(10, 7, true, @"C:\Anime\Frieren\Episodio 07.mp4");
        await _sut.GuardarFavoritoEpisodioAsync(10, 7, false, null);

        var registro = (await _sut.ObtenerRegistrosPorAnimeAsync(10)).Single();
        registro.FavoritoLocal.Should().BeFalse();
        registro.RutaArchivo.Should().EndWith("Episodio 07.mp4");
        registro.UltimaReproduccion.Should().BeNull();
        registro.ProgresoSegundos.Should().Be(0);
    }
}
