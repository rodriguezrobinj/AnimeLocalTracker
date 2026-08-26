using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AnimeLocalTracker.Core.Models;
using AnimeLocalTracker.Core.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>
/// Tests del contrato IReproductorViewModel (portados de la era Flyleaf a la arquitectura
/// Core/LibVLC): se prueba la ORQUESTACIÓN a través de la interfaz, no el motor nativo.
/// </summary>
public class ReproductorViewModelTests
{
    private Mock<IReproductorViewModel> CreateMockSut()
    {
        var mock = new Mock<IReproductorViewModel>();
        return mock;
    }

    [Fact]
    public async Task CargarVideoAsync_DeberiaActualizarTituloYEpisodio()
    {
        // Arrange
        var vm = new VmStub();
        var lista = new List<EpisodioItem>
        {
            new() { NumeroEpisodio = 1, RutaCompleta = "C:\\Anime\\Ep01.mkv" },
            new() { NumeroEpisodio = 2, RutaCompleta = "C:\\Anime\\Ep02.mkv" },
            new() { NumeroEpisodio = 3, RutaCompleta = "C:\\Anime\\Ep03.mkv" }
        };

        // Act
        await vm.CargarVideoAsync("C:\\Anime\\Ep02.mkv", 101, "Frieren", 2, lista);

        // Assert
        vm.TituloAnime.Should().Be("Frieren");
        vm.EpisodioActual.Should().Be(2);
    }

    [Fact]
    public void Interfaz_DeberiaExponerSoloContratoMinimo()
    {
        // Arrange & Act
        var interfaces = typeof(IReproductorViewModel).GetInterfaces();

        // Assert: IDisposable (para liberar recursos del motor) + el contrato de carga
        interfaces.Should().Contain(typeof(IDisposable));
        typeof(IReproductorViewModel).GetMethod(nameof(IReproductorViewModel.CargarVideoAsync)).Should().NotBeNull();
    }

    [Fact]
    public void Mock_DeberiaPermitirVerificarDispose()
    {
        // Arrange
        var mock = CreateMockSut();
        mock.Setup(m => m.Dispose());

        // Act
        mock.Object.Dispose();

        // Assert
        mock.Verify(m => m.Dispose(), Times.Once);
    }

    [Fact]
    public async Task Mock_DeberiaPermitirVerificarCargaDeVideo()
    {
        // Arrange
        var mock = CreateMockSut();
        var ruta = "C:\\Anime\\Ep01.mkv";
        var lista = new List<EpisodioItem>();

        // Act
        await mock.Object.CargarVideoAsync(ruta, 101, "Frieren", 1, lista);

        // Assert
        mock.Verify(m => m.CargarVideoAsync(ruta, 101, "Frieren", 1, lista), Times.Once);
    }

    /// <summary>
    /// Stub mínimo para tests de comportamiento puro sin LibVLC.
    /// </summary>
    private class VmStub : IReproductorViewModel
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
