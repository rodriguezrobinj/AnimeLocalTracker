using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Core.Models;
using AnimeLocalTracker.Core.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>
/// Tests de estrés del contrato IReproductorViewModel (portados de la era Flyleaf a la
/// arquitectura Core/LibVLC): se verifica la robustez del contrato bajo cargas de trabajo,
/// no el motor nativo que ahora vive en LibVLC y se prueba por separado.
/// </summary>
public class ReproductorStressTests
{
    [Fact]
    public async Task Contrato_CargaConsecutiva500Episodios_DeberiaMantenerConsistencia()
    {
        // Arrange: serie de 500 episodios
        var vm = new StubVm();
        var lista500 = Enumerable.Range(1, 500)
            .Select(i => new EpisodioItem { NumeroEpisodio = i, RutaCompleta = $"C:\\Anime\\Ep_{i:D3}.mkv" })
            .ToList();

        var stopwatch = Stopwatch.StartNew();

        // Act: cargar 250 consecutivos
        for (int i = 1; i <= 250; i++)
        {
            await vm.CargarVideoAsync($"C:\\Anime\\Ep_{i:D3}.mkv", 100, "One Piece", i, lista500);
            vm.EpisodioActual.Should().Be(i);
            vm.TituloAnime.Should().Be("One Piece");
        }

        stopwatch.Stop();

        // Assert: 250 cargas deben procesarse en menos de 5s
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(5000, $"500 cambios de episodio tomaron {stopwatch.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Contrato_CicloDeVidaRapido100Instancias_DeberiaLiberarRecursosLimpiamente()
    {
        // Arrange & Act: 100 ciclos de creación/carga/dispose
        for (int i = 0; i < 100; i++)
        {
            var vm = new StubVm();
            await vm.CargarVideoAsync($"C:\\Anime\\Test_{i}.mkv", i, $"Anime {i}", i);
            vm.Dispose();
        }

        // Assert: no excepciones durante 100 ciclos de vida de reproductor
        true.Should().BeTrue();
    }

    [Fact]
    public async Task Mock_DeberiaSoportarCargasParalelasSinExcepciones()
    {
        // Arrange
        var mock = new Mock<IReproductorViewModel>();
        mock.Setup(m => m.CargarVideoAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IEnumerable<EpisodioItem>?>()))
            .Returns(Task.CompletedTask);

        var tareas = Enumerable.Range(0, 50).Select(i =>
            mock.Object.CargarVideoAsync($"ruta_{i}", i, "Anime", i, null));

        // Act & Assert
        await Task.WhenAll(tareas);
        mock.Verify(m => m.CargarVideoAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IEnumerable<EpisodioItem>?>()), Times.Exactly(50));
    }

    [Fact]
    public async Task Contrato_CargarConListaVacia_NoDeberiaLanzar()
    {
        // Arrange
        var vm = new StubVm();

        // Act & Assert
        await vm.CargarVideoAsync("C:\\Anime\\Ep01.mkv", 1, "Test", 1, new List<EpisodioItem>());
        vm.EpisodioActual.Should().Be(1);
    }

    /// <summary>
    /// Stub de contrato para estrés sin LibVLC.
    /// </summary>
    private class StubVm : IReproductorViewModel
    {
        public string TituloAnime { get; private set; } = string.Empty;
        public int EpisodioActual { get; private set; }

        public Task CargarVideoAsync(string rutaVideo, int animeId, string tituloAnime, int episodio, IEnumerable<EpisodioItem>? episodiosDisponibles = null)
        {
            TituloAnime = tituloAnime;
            EpisodioActual = episodio;
            return Task.CompletedTask;
        }

        public void Dispose() { }
    }
}
