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
    private readonly Mock<IFileScannerService> _fileScannerMock;
    private readonly string _tempVideoFile;

    public HistorialViewModelTests()
    {
        _dbMock = new Mock<IDatabaseService>();
        _playbackMock = new Mock<IPlaybackStateService>();
        _dialogMock = new Mock<IDialogService>();
        _fileScannerMock = new Mock<IFileScannerService>();
        _fileScannerMock.Setup(f => f.EscanearEpisodiosAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<EpisodioItem>());

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

        return new HistorialViewModel(_dbMock.Object, _playbackMock.Object, _dialogMock.Object, _fileScannerMock.Object);
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
    public async Task Reanudar_ArchivoExiste_DeberiaEnviarNavegarMensajeReproductor()
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
        await sut.ReanudarAsync(item);

        // Assert
        mensajeRecibido.Should().NotBeNull();
        mensajeRecibido!.AnimeId.Should().Be(50);
        mensajeRecibido.Episodio.Should().Be(3);
        mensajeRecibido.RutaVideo.Should().Be(_tempVideoFile);
    }

    [Fact]
    public async Task Reanudar_ArchivoExiste_DeberiaArmarListaDeEpisodiosParaNavegacion()
    {
        // Regresión: reproducir desde Historial dejaba el reproductor sin lista de
        // episodios (a diferencia de la Ficha, que sí la arma), así que Anterior/Siguiente
        // siempre aparecían deshabilitados aunque el anime tuviera más capítulos.
        var anime = new AnimeItem { AniListId = 50, Titulo = "Test Anime", RutaCarpeta = @"C:\Anime\Test" };
        _dbMock.Setup(d => d.ObtenerAnimePorIdAsync(50)).ReturnsAsync(anime);
        _fileScannerMock.Setup(f => f.EscanearEpisodiosAsync(@"C:\Anime\Test"))
            .ReturnsAsync(new List<EpisodioItem>
            {
                new() { NumeroEpisodio = 2, RutaCompleta = @"C:\Anime\Test\Ep02.mkv" },
                new() { NumeroEpisodio = 3, RutaCompleta = _tempVideoFile },
                new() { NumeroEpisodio = 4, RutaCompleta = @"C:\Anime\Test\Ep04.mkv" }
            });

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
        await sut.ReanudarAsync(item);

        // Assert
        mensajeRecibido.Should().NotBeNull();
        mensajeRecibido!.EpisodiosDisponibles.Should().NotBeNull();
        int[] numerosEsperados = { 2, 3, 4 };
        mensajeRecibido.EpisodiosDisponibles!.Select(e => e.NumeroEpisodio).Should().BeEquivalentTo(numerosEsperados);
    }

    [Fact]
    public async Task Reanudar_ArchivoNoExiste_DeberiaMostrarDialogoError()
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
        await sut.ReanudarAsync(item);

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

    [Fact]
    public void Receive_ProgresoDelReproductor_SubeElItemAlPrincipioYActualizaLaFecha()
    {
        // Arrange: un episodio visto ayer, y otro más reciente encima en la lista
        var sut = CrearSut();
        var ayer = DateTime.UtcNow.AddDays(-1);
        var reciente = new HistorialItemViewModel { AniListId = 1, NumeroEpisodio = 1, UltimaReproduccion = DateTime.UtcNow.AddHours(-2) };
        var viejo = new HistorialItemViewModel { AniListId = 5, NumeroEpisodio = 3, UltimaReproduccion = ayer };
        sut.ItemsHistorial.Add(reciente);
        sut.ItemsHistorial.Add(viejo);

        // Act: el reproductor guarda progreso del episodio viejo (lo estás viendo ahora)
        sut.Receive(new EpisodioActualizadoMensaje(5, 3, false, 120, 1400));

        // Assert: sube al principio con la hora de ahora, sin esperar a cambiar de pestaña
        sut.ItemsHistorial[0].Should().BeSameAs(viejo);
        viejo.UltimaReproduccion.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        viejo.ProgresoSegundos.Should().Be(120);
        sut.ItemsAgrupados.OfType<HistorialItemViewModel>().First().Should().BeSameAs(viejo);
    }

    [Fact]
    public void Receive_MarcadoManualDesdeDetalle_NoFabricaFechaNiReordena()
    {
        // Arrange
        var sut = CrearSut();
        var fecha = DateTime.UtcNow.AddDays(-3);
        var primero = new HistorialItemViewModel { AniListId = 1, NumeroEpisodio = 1, UltimaReproduccion = DateTime.UtcNow.AddHours(-1) };
        var item = new HistorialItemViewModel { AniListId = 5, NumeroEpisodio = 3, VistoLocal = true, UltimaReproduccion = fecha };
        sut.ItemsHistorial.Add(primero);
        sut.ItemsHistorial.Add(item);

        // Act: Detalle avisa de un cambio manual (sin progreso ni duración)
        sut.Receive(new EpisodioActualizadoMensaje(5, 3, true, 0, 0));

        // Assert: el marcado manual no cuenta como visionado real (persistence.md #5)
        item.UltimaReproduccion.Should().Be(fecha);
        sut.ItemsHistorial[0].Should().BeSameAs(primero);
    }

    [Fact]
    public async Task Receive_EpisodioNuevoEnReproduccion_RecargaAlInstanteSinEsperarElCooldown()
    {
        // Arrange: el historial se acaba de cargar (dentro del cooldown de 30 s) y está vacío
        var sut = CrearSut();
        await sut.CargarHistorialAsync();

        _dbMock.Setup(d => d.ObtenerHistorialEpisodiosAsync(It.IsAny<int>()))
               .ReturnsAsync([new RegistroEpisodio { AniListId = 9, NumeroEpisodio = 2, ProgresoSegundos = 30, TotalSegundos = 1400, UltimaReproduccion = DateTime.UtcNow }]);

        // Act: empiezas a ver un episodio que nunca estuvo en el historial
        sut.Receive(new EpisodioActualizadoMensaje(9, 2, false, 30, 1400));

        // Assert: aparece sin cambiar de pestaña
        await EsperarAsync(() => sut.ItemsHistorial.Count == 1);
        sut.ItemsHistorial.Should().ContainSingle(i => i.AniListId == 9 && i.NumeroEpisodio == 2);
    }

    [Fact]
    public async Task Receive_MismoEpisodioNuevoVariasVeces_NoRecargaEnBucle()
    {
        // Arrange: la BD todavía no devuelve el episodio (p. ej. no se pudo guardar)
        var sut = CrearSut();
        await sut.CargarHistorialAsync();
        _dbMock.Invocations.Clear();

        // Act: el reproductor sigue guardando progreso cada pocos segundos
        for (int i = 0; i < 5; i++)
        {
            sut.Receive(new EpisodioActualizadoMensaje(9, 2, false, 30 + i, 1400));
            await Task.Delay(50);
        }

        // Assert: solo una recarga por episodio, no un SELECT por cada guardado
        _dbMock.Verify(d => d.ObtenerHistorialEpisodiosAsync(It.IsAny<int>()), Times.Once);
    }

    private static async Task EsperarAsync(Func<bool> condicion, int maxMs = 3000)
    {
        for (int t = 0; t < maxMs && !condicion(); t += 25) await Task.Delay(25);
    }
}
