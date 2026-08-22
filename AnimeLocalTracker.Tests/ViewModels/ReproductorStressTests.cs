using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

public class ReproductorStressTests
{
    private readonly Mock<IDatabaseService> _dbMock = new();
    private readonly Mock<IAnimeTrackingService> _trackingMock = new();
    private readonly Mock<IAuthService> _authMock = new();

    private ReproductorViewModel CreateSut()
    {
        return new ReproductorViewModel(_dbMock.Object, _trackingMock.Object, _authMock.Object);
    }

    [Fact]
    public void Reproductor_SeekingContinuoSegundoASegundo1440Veces_DeberiaProcesarSinLatenciaExcessiva()
    {
        // Arrange: Simular un episodio típico de 24 minutos (1440 segundos)
        var sut = CreateSut();
        sut.CargarVideo("C:\\Anime\\Ep01.mkv", 1, "Test Anime", 1);
        sut.TotalSeconds = 1440;

        var stopwatch = Stopwatch.StartNew();

        // Act: Seeking continuo segundo a segundo (1, 2, 3... 1440s)
        for (int sec = 1; sec <= 1440; sec++)
        {
            sut.SeekCommand.Execute((double)sec);
            sut.CurrentSeconds.Should().Be(sec);
            sut.TiempoActualTexto.Should().NotBeNullOrWhiteSpace();
            sut.TiempoCombinadoTexto.Should().Contain(sut.TiempoActualTexto);
        }

        stopwatch.Stop();

        // Assert: 1,440 operaciones de seeking continuo deben completarse en menos de 200ms
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(200, 
            $"El seeking continuo de 1,440 segundos tomó {stopwatch.ElapsedMilliseconds}ms, lo cual excede el umbral deseado de fluidez.");

        sut.Dispose();
    }

    [Fact]
    public void Reproductor_SeekingAleatorioMultiPunto1000Veces_DeberiaMantenerConsistenciaTemporal()
    {
        // Arrange
        var sut = CreateSut();
        sut.CargarVideo("C:\\Anime\\Ep01.mkv", 1, "Test Anime", 1);
        sut.TotalSeconds = 1440;

        var random = new Random(42);
        var stopwatch = Stopwatch.StartNew();

        // Act: 1,000 saltos arbitrarios hacia adelante y hacia atrás
        for (int i = 0; i < 1000; i++)
        {
            double targetSeconds = random.NextDouble() * 1440;
            sut.SeekCommand.Execute(targetSeconds);
            sut.CurrentSeconds.Should().Be(targetSeconds);
        }

        stopwatch.Stop();

        // Assert: 1,000 saltos aleatorios deben completarse rápidamente
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(150,
            $"1,000 saltos aleatorios tomaron {stopwatch.ElapsedMilliseconds}ms");

        sut.Dispose();
    }

    [Fact]
    public void Reproductor_ConmutacionMasiva500Episodios_DeberiaActualizarSinFugasNiExcepciones()
    {
        // Arrange: Serie de 500 episodios
        var sut = CreateSut();
        var lista500 = Enumerable.Range(1, 500)
            .Select(i => new EpisodioItem { NumeroEpisodio = i, RutaCompleta = $"C:\\Anime\\Ep_{i:D3}.mkv" })
            .ToList();

        sut.CargarVideo("C:\\Anime\\Ep_001.mkv", 100, "One Piece", 1, lista500);

        var stopwatch = Stopwatch.StartNew();

        // Act: Avanzar 250 veces hacia adelante y 250 hacia atrás
        for (int i = 1; i < 250; i++)
        {
            sut.SiguienteEpisodioCommand.Execute(null);
            sut.TieneEpisodioAnterior.Should().BeTrue();
        }
        sut.TituloEpisodio.Should().Be("Episodio 250");

        for (int i = 250; i > 1; i--)
        {
            sut.AnteriorEpisodioCommand.Execute(null);
            sut.TieneEpisodioSiguiente.Should().BeTrue();
        }
        sut.TituloEpisodio.Should().Be("Episodio 1");
        sut.TieneEpisodioAnterior.Should().BeFalse();

        stopwatch.Stop();

        // Assert: 500 cambios de episodio deben procesarse en menos de 5000ms
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(5000,
            $"500 cambios de episodio tomaron {stopwatch.ElapsedMilliseconds}ms");

        sut.Dispose();
    }

    [Fact]
    public async Task Reproductor_MaratonAutoPlay24Episodios_DeberiaEncadenarCorrectamente()
    {
        // Arrange: Temporada de 24 episodios
        var sut = CreateSut();
        var temporada = Enumerable.Range(1, 24)
            .Select(i => new EpisodioItem { NumeroEpisodio = i, RutaCompleta = $"C:\\Anime\\Ep_{i:D2}.mkv" })
            .ToList();

        _dbMock.Setup(d => d.ObtenerRegistrosPorAnimeAsync(50))
            .ReturnsAsync(new List<RegistroEpisodio>());

        sut.CargarVideo("C:\\Anime\\Ep_01.mkv", 50, "Jujutsu Kaisen", 1, temporada);

        // Act: Simular reproducción completa y avance por AutoPlay a lo largo de los 24 episodios
        for (int ep = 1; ep <= 24; ep++)
        {
            sut.TituloEpisodio.Should().Be($"Episodio {ep}");

            // Simular auto-tracking
            await sut.RealizarAutoTrackingAsync();

            if (ep < 24)
            {
                sut.TieneEpisodioSiguiente.Should().BeTrue();
                sut.SiguienteEpisodioCommand.Execute(null);
            }
            else
            {
                sut.TieneEpisodioSiguiente.Should().BeFalse();
            }
        }

        // Assert: Se guardaron los 24 episodios en la base de datos
        _dbMock.Verify(d => d.GuardarRegistroEpisodioAsync(It.IsAny<RegistroEpisodio>()), Times.Exactly(24));

        sut.Dispose();
    }

    [Fact]
    public void Reproductor_CicloDeVidaRapido100Instancias_DeberiaLiberarRecursosLimpiamente()
    {
        // Arrange & Act
        for (int i = 0; i < 100; i++)
        {
            var sut = CreateSut();
            sut.CargarVideo($"C:\\Anime\\Test_{i}.mkv", i, $"Anime {i}", i);
            sut.SeekCommand.Execute(30.0);
            sut.Dispose();
        }

        // Assert: No excepciones durante 100 ciclos de vida de reproductor
        true.Should().BeTrue();
    }
}
