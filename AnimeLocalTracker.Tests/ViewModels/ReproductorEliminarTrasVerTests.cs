using System.Collections.Generic;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>El reproductor avisa al servicio de "Eliminar tras ver" solo cuando el episodio se vio de verdad reproduciéndolo.</summary>
public class ReproductorEliminarTrasVerTests
{
    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<IAnimeTrackingService> _tracking = new();
    private readonly Mock<IAuthService> _auth = new();
    private readonly Mock<ILimpiadorDeEpisodios> _limpiador = new();

    private ReproductorViewModel CrearSut()
    {
        _db.Setup(d => d.ObtenerRegistrosPorAnimeAsync(It.IsAny<int>())).ReturnsAsync(new List<RegistroEpisodio>());
        return new ReproductorViewModel(_db.Object, _tracking.Object, _auth.Object, limpiadorDeEpisodios: _limpiador.Object);
    }

    [Fact]
    public async Task AlCerrarTrasVerElEpisodio_AvisaAlLimpiador()
    {
        var sut = CrearSut();
        sut.CargarVideo(@"C:\Anime\Ep05.mkv", 101, "Solo Leveling", 5);
        await sut.RealizarAutoTrackingAsync();

        sut.Dispose();

        _limpiador.Verify(l => l.AplicarTrasVerAsync(101, 5), Times.Once);
    }

    [Fact]
    public void AlCerrarSinHaberloVisto_NoAvisa()
    {
        var sut = CrearSut();
        sut.CargarVideo(@"C:\Anime\Ep05.mkv", 101, "Solo Leveling", 5);

        sut.Dispose();

        _limpiador.Verify(l => l.AplicarTrasVerAsync(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task AlPasarAlSiguienteEpisodioTrasVerElAnterior_AvisaDelAnterior()
    {
        var sut = CrearSut();
        sut.CargarVideo(@"C:\Anime\Ep05.mkv", 101, "Solo Leveling", 5);
        await sut.RealizarAutoTrackingAsync();

        sut.CargarVideo(@"C:\Anime\Ep06.mkv", 101, "Solo Leveling", 6);

        _limpiador.Verify(l => l.AplicarTrasVerAsync(101, 5), Times.Once);
        sut.Dispose();
        _limpiador.Verify(l => l.AplicarTrasVerAsync(101, 6), Times.Never); // el 6 no se llegó a ver
    }

    [Fact]
    public async Task AlRecargarElMismoEpisodio_NoAvisa()
    {
        var sut = CrearSut();
        sut.CargarVideo(@"C:\Anime\Ep05.mkv", 101, "Solo Leveling", 5);
        await sut.RealizarAutoTrackingAsync();

        sut.CargarVideo(@"C:\Anime\Ep05.mkv", 101, "Solo Leveling", 5);

        _limpiador.Verify(l => l.AplicarTrasVerAsync(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
        sut.Dispose();
    }
}
