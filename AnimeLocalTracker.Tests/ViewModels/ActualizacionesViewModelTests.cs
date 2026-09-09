using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

public class ActualizacionesViewModelTests
{
    private readonly Mock<IDatabaseService> _dbMock = new();
    private readonly Mock<IAnimeTrackingService> _trackingMock = new();
    private readonly Mock<IDownloadService> _downloadMock = new();

    private ActualizacionesViewModel CrearSut()
    {
        return new ActualizacionesViewModel(_dbMock.Object, _trackingMock.Object, _downloadMock.Object);
    }

    [Fact]
    public async Task Cargar_ConEpisodioEmitidoYNoDescargado_DeberiaAparecerEnElFeed()
    {
        // Arrange: anime RELEASING con portada local y sin registros descargados
        _dbMock.Setup(d => d.ObtenerAnimesLigerosAsync()).ReturnsAsync(new List<AnimeItem>
        {
            new() { AniListId = 1, Titulo = "One Piece", Estado = "RELEASING", RutaCarpeta = @"C:\Anime\OnePiece", UrlPortada = "cover.png" }
        });
        _dbMock.Setup(d => d.ObtenerTodosLosRegistrosAsync()).ReturnsAsync(new List<RegistroEpisodio>());
        _trackingMock.Setup(t => t.ObtenerCalendarioEmisionAsync(It.IsAny<List<int>>(), It.IsAny<long>(), It.IsAny<long>()))
            .ReturnsAsync(new List<AiringEpisode>
            {
                new() { AniListId = 1, NumeroEpisodio = 1120, FechaEmision = DateTime.UtcNow.AddHours(-3) }
            });
        var sut = CrearSut();

        // Act
        await sut.CargarActualizacionesAsync();

        // Assert
        sut.Items.Should().ContainSingle();
        sut.Items[0].NumeroEpisodio.Should().Be(1120);
        sut.Items[0].RutaCarpeta.Should().Be(@"C:\Anime\OnePiece");
        sut.TieneItems.Should().BeTrue();
    }

    [Fact]
    public async Task Cargar_ConEpisodioYaDescargado_DeberiaAparecerMarcadoComoDescargado()
    {
        // Arrange: el episodio ya tiene archivo local
        _dbMock.Setup(d => d.ObtenerAnimesLigerosAsync()).ReturnsAsync(new List<AnimeItem>
        {
            new() { AniListId = 1, Titulo = "One Piece", Estado = "RELEASING", RutaCarpeta = @"C:\Anime\OnePiece" }
        });
        _dbMock.Setup(d => d.ObtenerTodosLosRegistrosAsync()).ReturnsAsync(new List<RegistroEpisodio>
        {
            new() { AniListId = 1, NumeroEpisodio = 1120, RutaArchivo = @"C:\Anime\OnePiece\Ep1120.mkv" }
        });
        _trackingMock.Setup(t => t.ObtenerCalendarioEmisionAsync(It.IsAny<List<int>>(), It.IsAny<long>(), It.IsAny<long>()))
            .ReturnsAsync(new List<AiringEpisode>
            {
                new() { AniListId = 1, NumeroEpisodio = 1120, FechaEmision = DateTime.UtcNow.AddHours(-3) }
            });
        var sut = CrearSut();

        // Act
        await sut.CargarActualizacionesAsync();

        // Assert: ya lo tienes, pero sigue apareciendo en el feed marcado como Descargado
        sut.Items.Should().ContainSingle();
        sut.Items[0].Descargado.Should().BeTrue();
        sut.Items[0].RutaArchivo.Should().Be(@"C:\Anime\OnePiece\Ep1120.mkv");
        sut.TieneItems.Should().BeTrue();
        sut.EstaVacio.Should().BeFalse();
    }

    [Fact]
    public async Task Descargar_DeberiaEncolarLaDescargaDelEpisodio()
    {
        // Arrange
        var sut = CrearSut();
        var item = new ActualizacionItemViewModel
        {
            AniListId = 7,
            TituloAnime = "Frieren",
            NumeroEpisodio = 24,
            RutaCarpeta = @"C:\Anime\Frieren"
        };

        // Act
        await sut.DescargarCommand.ExecuteAsync(item);

        // Assert: encola con los datos del feed (sin pasar por la ficha del anime)
        _downloadMock.Verify(d => d.IniciarDescargaEpisodioAsync(7, "Frieren", @"C:\Anime\Frieren", 24, null), Times.Once);
        item.IsDownloading.Should().BeTrue("al encolarse queda en estado descargando hasta que el servicio notifique");
    }

    [Fact]
    public void Receive_ProgresoCompletado_DeberiaMarcarDescargadoSinEliminarItem()
    {
        // Arrange
        var sut = CrearSut();
        var item = new ActualizacionItemViewModel
        {
            AniListId = 1,
            NumeroEpisodio = 5,
            IsDownloading = true,
            DownloadProgress = 50
        };
        sut.Items.Add(item);

        // Act
        sut.Receive(new AnimeLocalTracker.Messages.DescargaProgresoMensaje(
            1, 5, 100, isDownloading: false, isCompleted: true, isPaused: false, @"C:\Anime\Ep05.mkv", null, "One Piece"));

        // Assert
        item.Descargado.Should().BeTrue();
        item.IsDownloading.Should().BeFalse();
        item.DownloadProgress.Should().Be(100);
        item.RutaArchivo.Should().Be(@"C:\Anime\Ep05.mkv");
        sut.Items.Should().ContainSingle();
    }

    [Fact]
    public void Reproducir_ConEpisodioNoDescargado_NoDeberiaFallar()
    {
        // Arrange
        var sut = CrearSut();
        var item = new ActualizacionItemViewModel
        {
            AniListId = 1,
            NumeroEpisodio = 5,
            Descargado = false
        };

        // Act & Assert
        sut.ReproducirCommand.Execute(item);
        item.Descargado.Should().BeFalse();
    }
}
