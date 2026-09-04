using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

public class HistorialViewModelTests : IDisposable
{
    private readonly Mock<IDatabaseService> _dbMock;
    private readonly Mock<IPlaybackStateService> _playbackMock;
    private readonly Mock<IDialogService> _dialogMock;
    private readonly string _tempVideoFile;

    public HistorialViewModelTests()
    {
        _dbMock = new Mock<IDatabaseService>();
        _playbackMock = new Mock<IPlaybackStateService>();
        _dialogMock = new Mock<IDialogService>();

        _tempVideoFile = Path.Combine(Path.GetTempPath(), $"test_video_{Guid.NewGuid():N}.mp4");
        File.WriteAllText(_tempVideoFile, "dummy");

        WeakReferenceMessenger.Default.Reset();
    }

    public void Dispose()
    {
        WeakReferenceMessenger.Default.Reset();
        if (File.Exists(_tempVideoFile))
        {
            try { File.Delete(_tempVideoFile); } catch { /* ignore */ }
        }
        GC.SuppressFinalize(this);
    }

    private HistorialViewModel CrearSut(
        List<AnimeItem>? animes = null,
        List<RegistroEpisodio>? registros = null)
    {
        _dbMock.Setup(d => d.ObtenerHistorialEpisodiosAsync(It.IsAny<int>()))
               .ReturnsAsync(registros ?? []);
        _dbMock.Setup(d => d.ObtenerAnimesLigerosAsync())
               .ReturnsAsync(animes ?? []);

        return new HistorialViewModel(_dbMock.Object, _playbackMock.Object, _dialogMock.Object);
    }

    [Fact]
    public async Task CargarHistorial_SinRegistros_DeberiaMostrarEstadoVacio()
    {
        // Arrange
        var sut = CrearSut();

        // Act
        await sut.CargarHistorialAsync();

        // Assert
        sut.ItemsHistorial.Should().BeEmpty();
        sut.ItemsFiltrados.Should().BeEmpty();
        sut.EstaVacio.Should().BeTrue();
        sut.TieneElementos.Should().BeFalse();
        sut.TotalElementos.Should().Be(0);
        sut.TotalEnProgreso.Should().Be(0);
        sut.TotalCompletados.Should().Be(0);
    }

    [Fact]
    public async Task CargarHistorial_ConRegistros_DeberiaMapearCombinandoAnimeYEpisodio()
    {
        // Arrange
        var animes = new List<AnimeItem>
        {
            new() { AniListId = 101, Titulo = "Frieren", TotalEpisodios = 28 }
        };

        var registros = new List<RegistroEpisodio>
        {
            new()
            {
                AniListId = 101,
                NumeroEpisodio = 5,
                RutaArchivo = _tempVideoFile,
                ProgresoSegundos = 600,
                TotalSegundos = 1440,
                VistoLocal = false,
                UltimaReproduccion = DateTime.UtcNow.AddHours(-1)
            },
            new()
            {
                AniListId = 101,
                NumeroEpisodio = 4,
                RutaArchivo = _tempVideoFile,
                ProgresoSegundos = 0,
                TotalSegundos = 1440,
                VistoLocal = true,
                UltimaReproduccion = DateTime.UtcNow.AddDays(-1)
            }
        };

        var sut = CrearSut(animes, registros);

        // Act
        await sut.CargarHistorialAsync();

        // Assert
        sut.ItemsHistorial.Should().HaveCount(2);
        sut.ItemsFiltrados.Should().HaveCount(2);
        sut.TotalElementos.Should().Be(2);
        sut.TotalEnProgreso.Should().Be(1);
        sut.TotalCompletados.Should().Be(1);
        sut.EstaVacio.Should().BeFalse();
        sut.TieneElementos.Should().BeTrue();

        var itemEnProgreso = sut.ItemsHistorial.First(i => i.NumeroEpisodio == 5);
        itemEnProgreso.TituloAnime.Should().Be("Frieren");
        itemEnProgreso.EnProgreso.Should().BeTrue();
        itemEnProgreso.PorcentajeProgreso.Should().BeApproximately(600.0 / 1440.0, 0.01);
    }

    [Fact]
    public async Task CambiarFiltro_DeberiaFiltrarPorEnProgresoYCompletados()
    {
        // Arrange
        var animes = new List<AnimeItem>
        {
            new() { AniListId = 1, Titulo = "Anime Test" }
        };
        var registros = new List<RegistroEpisodio>
        {
            new() { AniListId = 1, NumeroEpisodio = 1, ProgresoSegundos = 500, TotalSegundos = 1400, VistoLocal = false, UltimaReproduccion = DateTime.UtcNow },
            new() { AniListId = 1, NumeroEpisodio = 2, ProgresoSegundos = 0, TotalSegundos = 1400, VistoLocal = true, UltimaReproduccion = DateTime.UtcNow }
        };

        var sut = CrearSut(animes, registros);
        await sut.CargarHistorialAsync();

        // Act: Filtrar En Progreso
        sut.CambiarFiltro("EnProgreso");
        sut.ItemsFiltrados.Should().HaveCount(1);
        sut.ItemsFiltrados[0].NumeroEpisodio.Should().Be(1);

        // Act: Filtrar Completados
        sut.CambiarFiltro("Completados");
        sut.ItemsFiltrados.Should().HaveCount(1);
        sut.ItemsFiltrados[0].NumeroEpisodio.Should().Be(2);

        // Act: Filtrar Todos
        sut.CambiarFiltro("Todos");
        sut.ItemsFiltrados.Should().HaveCount(2);
    }

    [Fact]
    public async Task TextoBusqueda_DeberiaFiltrarReactivoPorTitulo()
    {
        // Arrange
        var animes = new List<AnimeItem>
        {
            new() { AniListId = 1, Titulo = "Attack on Titan" },
            new() { AniListId = 2, Titulo = "Bocchi the Rock" }
        };
        var registros = new List<RegistroEpisodio>
        {
            new() { AniListId = 1, NumeroEpisodio = 1, UltimaReproduccion = DateTime.UtcNow },
            new() { AniListId = 2, NumeroEpisodio = 1, UltimaReproduccion = DateTime.UtcNow }
        };

        var sut = CrearSut(animes, registros);
        await sut.CargarHistorialAsync();

        // Act: Búsqueda que coincide con 1
        sut.TextoBusqueda = "bocchi";
        sut.ItemsFiltrados.Should().HaveCount(1);
        sut.ItemsFiltrados[0].TituloAnime.Should().Be("Bocchi the Rock");
        sut.SinResultadosBusqueda.Should().BeFalse();

        // Act: Búsqueda sin coincidencias
        sut.TextoBusqueda = "Evangelion";
        sut.ItemsFiltrados.Should().BeEmpty();
        sut.SinResultadosBusqueda.Should().BeTrue();

        // Act: Limpiar búsqueda
        sut.TextoBusqueda = string.Empty;
        sut.ItemsFiltrados.Should().HaveCount(2);
        sut.SinResultadosBusqueda.Should().BeFalse();
    }

    [Fact]
    public void Reanudar_ArchivoExiste_DeberiaEnviarNavegarMensajeReproductor()
    {
        // Arrange
        var sut = CrearSut();
        var item = new HistorialItemViewModel
        {
            AniListId = 50,
            NumeroEpisodio = 3,
            TituloAnime = "Test Anime",
            RutaArchivo = _tempVideoFile
        };

        NavegarMensaje_Reproductor? mensajeRecibido = null;
        WeakReferenceMessenger.Default.Register<NavegarMensaje_Reproductor>(this, (r, m) =>
        {
            mensajeRecibido = m;
        });

        // Act
        sut.Reanudar(item);

        // Assert
        mensajeRecibido.Should().NotBeNull();
        mensajeRecibido!.AnimeId.Should().Be(50);
        mensajeRecibido.Episodio.Should().Be(3);
        mensajeRecibido.RutaVideo.Should().Be(_tempVideoFile);
    }

    [Fact]
    public void Reanudar_ArchivoNoExiste_DeberiaMostrarDialogoError()
    {
        // Arrange
        var sut = CrearSut();
        var item = new HistorialItemViewModel
        {
            AniListId = 50,
            NumeroEpisodio = 3,
            TituloAnime = "Test Anime",
            RutaArchivo = @"C:\inexistente\video_no_existe.mkv"
        };

        // Act
        sut.Reanudar(item);

        // Assert
        _dialogMock.Verify(d => d.MostrarDialogoAsync(
            It.IsAny<string>(), It.IsAny<string>(), false, It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task NavegarDetalle_DeberiaEnviarNavegarMensajeDetalle()
    {
        // Arrange
        var anime = new AnimeItem { AniListId = 77, Titulo = "Anime 77" };
        _dbMock.Setup(d => d.ObtenerAnimePorIdAsync(77)).ReturnsAsync(anime);

        var sut = CrearSut();
        var item = new HistorialItemViewModel { AniListId = 77, NumeroEpisodio = 1 };

        NavegarMensaje_Detalle? mensajeRecibido = null;
        WeakReferenceMessenger.Default.Register<NavegarMensaje_Detalle>(this, (r, m) =>
        {
            mensajeRecibido = m;
        });

        // Act
        await sut.NavegarDetalleAsync(item);

        // Assert
        mensajeRecibido.Should().NotBeNull();
        mensajeRecibido!.AnimeSeleccionado.AniListId.Should().Be(77);
    }

    [Fact]
    public async Task AlternarVisto_DeberiaSincronizarYActualizarEstado()
    {
        // Arrange
        var sut = CrearSut();
        var item = new HistorialItemViewModel
        {
            AniListId = 10,
            NumeroEpisodio = 2,
            RutaArchivo = _tempVideoFile,
            TotalSegundos = 1400,
            VistoLocal = false,
            ProgresoSegundos = 300
        };
        sut.ItemsHistorial.Add(item);

        _playbackMock.Setup(p => p.MarcarComoVistoYSincronizarAsync(10, 2, _tempVideoFile, 1400, false))
                     .ReturnsAsync(true);

        // Act
        await sut.AlternarVistoAsync(item);

        // Assert
        item.VistoLocal.Should().BeTrue();
        item.ProgresoSegundos.Should().Be(0);
        _playbackMock.Verify(p => p.MarcarComoVistoYSincronizarAsync(10, 2, _tempVideoFile, 1400, false), Times.Once);
    }

    [Fact]
    public async Task EliminarItem_DeberiaLlamarADatabaseYRemoverDeColeccion()
    {
        // Arrange
        var sut = CrearSut();
        var item = new HistorialItemViewModel
        {
            AniListId = 20,
            NumeroEpisodio = 1
        };
        sut.ItemsHistorial.Add(item);
        sut.ItemsFiltrados.Add(item);

        // Act
        await sut.EliminarItemAsync(item);

        // Assert
        _dbMock.Verify(d => d.LimpiarRegistroHistorialAsync(20, 1), Times.Once);
        sut.ItemsHistorial.Should().NotContain(item);
        sut.ItemsFiltrados.Should().NotContain(item);
    }

    [Fact]
    public async Task LimpiarHistorial_Confirmado_DeberiaVaciarTodo()
    {
        // Arrange
        var sut = CrearSut();
        sut.ItemsHistorial.Add(new HistorialItemViewModel { AniListId = 1, NumeroEpisodio = 1 });
        sut.ItemsFiltrados.Add(new HistorialItemViewModel { AniListId = 1, NumeroEpisodio = 1 });

        _dialogMock.Setup(d => d.MostrarDialogoAsync(
            It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<string>(), It.IsAny<string>()))
                   .ReturnsAsync(true);

        // Act
        await sut.LimpiarHistorialAsync();

        // Assert
        _dbMock.Verify(d => d.LimpiarTodoElHistorialAsync(), Times.Once);
        sut.ItemsHistorial.Should().BeEmpty();
        sut.ItemsFiltrados.Should().BeEmpty();
        sut.EstaVacio.Should().BeTrue();
    }

    [Fact]
    public async Task LimpiarHistorial_Cancelado_NoDeberiaModificarDb()
    {
        // Arrange
        var sut = CrearSut();
        var item = new HistorialItemViewModel { AniListId = 1, NumeroEpisodio = 1 };
        sut.ItemsHistorial.Add(item);
        sut.ItemsFiltrados.Add(item);

        _dialogMock.Setup(d => d.MostrarDialogoAsync(
            It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<string>(), It.IsAny<string>()))
                   .ReturnsAsync(false);

        // Act
        await sut.LimpiarHistorialAsync();

        // Assert
        _dbMock.Verify(d => d.LimpiarTodoElHistorialAsync(), Times.Never);
        sut.ItemsHistorial.Should().Contain(item);
    }

    [Fact]
    public void Receive_EpisodioActualizadoMensaje_DeberiaActualizarItem()
    {
        // Arrange
        var sut = CrearSut();
        var item = new HistorialItemViewModel
        {
            AniListId = 5,
            NumeroEpisodio = 1,
            ProgresoSegundos = 100,
            TotalSegundos = 1000,
            VistoLocal = false
        };
        sut.ItemsHistorial.Add(item);

        // Act
        sut.Receive(new EpisodioActualizadoMensaje(5, 1, true, 0, 1000));

        // Assert
        item.VistoLocal.Should().BeTrue();
        item.ProgresoSegundos.Should().Be(0);
    }
}
