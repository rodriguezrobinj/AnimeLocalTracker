using System;
using System.Collections.Generic;
using System.IO;
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

public class ActualizacionesViewModelTests
{
    private readonly Mock<IDatabaseService> _dbMock = new();
    private readonly Mock<IAnimeTrackingService> _trackingMock = new();
    private readonly Mock<IDownloadService> _downloadMock = new();
    private readonly Mock<IFileScannerService> _fileScannerMock = new();

    public ActualizacionesViewModelTests()
    {
        // Por defecto, ninguna carpeta tiene episodios descargados en disco.
        _fileScannerMock.Setup(f => f.EscanearEpisodiosAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<EpisodioItem>());
    }

    private ActualizacionesViewModel CrearSut()
    {
        return new ActualizacionesViewModel(_dbMock.Object, _trackingMock.Object, _downloadMock.Object, _fileScannerMock.Object);
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
        // Arrange: el episodio ya tiene archivo local EN DISCO (aún sin fila en
        // RegistroEpisodio, el caso típico de un episodio recién descargado que
        // todavía no se ha reproducido/marcado).
        _dbMock.Setup(d => d.ObtenerAnimesLigerosAsync()).ReturnsAsync(new List<AnimeItem>
        {
            new() { AniListId = 1, Titulo = "One Piece", Estado = "RELEASING", RutaCarpeta = @"C:\Anime\OnePiece" }
        });
        _dbMock.Setup(d => d.ObtenerTodosLosRegistrosAsync()).ReturnsAsync(new List<RegistroEpisodio>());
        _fileScannerMock.Setup(f => f.EscanearEpisodiosAsync(@"C:\Anime\OnePiece"))
            .ReturnsAsync(new List<EpisodioItem>
            {
                new() { NumeroEpisodio = 1120, RutaCompleta = @"C:\Anime\OnePiece\Ep1120.mkv" }
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
    public async Task Cargar_ConEpisodioDescargadoYRegistroDeProgreso_DeberiaExponerVistoTamanoYProgreso()
    {
        // Arrange: el episodio está en disco (tamaño calculado por el escaneo) y además
        // tiene un RegistroEpisodio con progreso guardado a medio ver (no visto, no
        // terminado) — los mismos estados que ya se muestran en la Ficha del anime.
        _dbMock.Setup(d => d.ObtenerAnimesLigerosAsync()).ReturnsAsync(new List<AnimeItem>
        {
            new() { AniListId = 1, Titulo = "One Piece", Estado = "RELEASING", RutaCarpeta = @"C:\Anime\OnePiece" }
        });
        _dbMock.Setup(d => d.ObtenerTodosLosRegistrosAsync()).ReturnsAsync(new List<RegistroEpisodio>
        {
            new() { AniListId = 1, NumeroEpisodio = 1120, VistoLocal = false, ProgresoSegundos = 300, TotalSegundos = 1400 }
        });
        _fileScannerMock.Setup(f => f.EscanearEpisodiosAsync(@"C:\Anime\OnePiece"))
            .ReturnsAsync(new List<EpisodioItem>
            {
                new() { NumeroEpisodio = 1120, RutaCompleta = @"C:\Anime\OnePiece\Ep1120.mkv", TamanoArchivoFormateado = "350 MB" }
            });
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
        var item = sut.Items[0];
        item.TamanoArchivoFormateado.Should().Be("350 MB");
        item.Visto.Should().BeFalse();
        item.ProgresoSegundos.Should().Be(300);
        item.TotalSegundos.Should().Be(1400);
        item.TieneProgresoGuardado.Should().BeTrue();
        item.ProgresoFormateado.Should().Be("05:00 / 23:20");
    }

    [Fact]
    public async Task Cargar_ConEpisodioYaVisto_NoDeberiaMostrarProgresoGuardado()
    {
        // Regresión: un episodio ya visto no debe mostrar la barra de "a medio ver"
        // aunque técnicamente conserve un ProgresoSegundos > 0 en la BD.
        _dbMock.Setup(d => d.ObtenerAnimesLigerosAsync()).ReturnsAsync(new List<AnimeItem>
        {
            new() { AniListId = 1, Titulo = "One Piece", Estado = "RELEASING", RutaCarpeta = @"C:\Anime\OnePiece" }
        });
        _dbMock.Setup(d => d.ObtenerTodosLosRegistrosAsync()).ReturnsAsync(new List<RegistroEpisodio>
        {
            new() { AniListId = 1, NumeroEpisodio = 1120, VistoLocal = true, ProgresoSegundos = 1400, TotalSegundos = 1400 }
        });
        _fileScannerMock.Setup(f => f.EscanearEpisodiosAsync(@"C:\Anime\OnePiece"))
            .ReturnsAsync(new List<EpisodioItem>
            {
                new() { NumeroEpisodio = 1120, RutaCompleta = @"C:\Anime\OnePiece\Ep1120.mkv" }
            });
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
        sut.Items[0].Visto.Should().BeTrue();
        sut.Items[0].TieneProgresoGuardado.Should().BeFalse();
    }

    [Fact]
    public async Task Cargar_ConFechaEmisionReciente_NoDeberiaFiltrarloPorZonaHoraria()
    {
        // Regresión: FechaEmision viene de DateTimeOffset.FromUnixTimeSeconds(...).DateTime
        // (Kind=Unspecified, valor ya en UTC). Usar ToUniversalTime() la trataba como hora
        // local y la desplazaba otra vez por el huso horario, ocultando del feed episodios
        // recién emitidos que Calendario sí mostraba (hasta que "ahora" alcanzaba el valor
        // ya desplazado). Un episodio emitido hace 1 minuto debe aparecer siempre.
        _dbMock.Setup(d => d.ObtenerAnimesLigerosAsync()).ReturnsAsync(new List<AnimeItem>
        {
            new() { AniListId = 1, Titulo = "One Piece", Estado = "RELEASING", RutaCarpeta = @"C:\Anime\OnePiece" }
        });
        _dbMock.Setup(d => d.ObtenerTodosLosRegistrosAsync()).ReturnsAsync(new List<RegistroEpisodio>());
        _trackingMock.Setup(t => t.ObtenerCalendarioEmisionAsync(It.IsAny<List<int>>(), It.IsAny<long>(), It.IsAny<long>()))
            .ReturnsAsync(new List<AiringEpisode>
            {
                new()
                {
                    AniListId = 1,
                    NumeroEpisodio = 1121,
                    FechaEmision = DateTime.SpecifyKind(DateTime.UtcNow.AddMinutes(-1), DateTimeKind.Unspecified)
                }
            });
        var sut = CrearSut();

        // Act
        await sut.CargarActualizacionesAsync();

        // Assert
        sut.Items.Should().ContainSingle();
        sut.Items[0].NumeroEpisodio.Should().Be(1121);
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
    public void Receive_ProgresoCompletado_DeberiaCalcularTamanoDelArchivoDescargado()
    {
        // Arrange: archivo real en disco para que File.Exists/FileInfo.Length tengan algo que leer.
        string rutaTemp = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mkv");
        File.WriteAllBytes(rutaTemp, new byte[2 * 1024 * 1024]); // 2 MB
        try
        {
            var sut = CrearSut();
            var item = new ActualizacionItemViewModel { AniListId = 1, NumeroEpisodio = 5 };
            sut.Items.Add(item);

            // Act
            sut.Receive(new AnimeLocalTracker.Messages.DescargaProgresoMensaje(
                1, 5, 100, isDownloading: false, isCompleted: true, isPaused: false, rutaTemp, null, "One Piece"));

            // Assert
            item.TamanoArchivoFormateado.Should().Be("2 MB");
        }
        finally
        {
            try { File.Delete(rutaTemp); } catch { }
        }
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

    [Fact]
    public async Task Reproducir_ConEpisodioDescargado_DeberiaArmarListaDeEpisodiosParaNavegacion()
    {
        // Regresión: reproducir desde Actualizaciones dejaba el reproductor sin lista de
        // episodios (a diferencia de la Ficha, que sí la arma), así que Anterior/Siguiente
        // siempre aparecían deshabilitados aunque el anime tuviera más capítulos.
        string rutaTemp = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mkv");
        File.WriteAllText(rutaTemp, "dummy");
        try
        {
            _fileScannerMock.Setup(f => f.EscanearEpisodiosAsync(@"C:\Anime\OnePiece"))
                .ReturnsAsync(new List<EpisodioItem>
                {
                    new() { NumeroEpisodio = 1119, RutaCompleta = @"C:\Anime\OnePiece\Ep1119.mkv" },
                    new() { NumeroEpisodio = 1120, RutaCompleta = rutaTemp }
                });

            var sut = CrearSut();
            var item = new ActualizacionItemViewModel
            {
                AniListId = 1,
                NumeroEpisodio = 1120,
                RutaCarpeta = @"C:\Anime\OnePiece",
                Descargado = true,
                RutaArchivo = rutaTemp
            };

            NavegarMensaje_Reproductor? mensajeRecibido = null;
            WeakReferenceMessenger.Default.Register<NavegarMensaje_Reproductor>(this, (r, m) =>
            {
                mensajeRecibido = m;
            });

            // Act
            await sut.ReproducirCommand.ExecuteAsync(item);

            // Assert
            mensajeRecibido.Should().NotBeNull();
            mensajeRecibido!.EpisodiosDisponibles.Should().NotBeNull();
            int[] numerosEsperados = { 1119, 1120 };
            mensajeRecibido.EpisodiosDisponibles!.Select(e => e.NumeroEpisodio).Should().BeEquivalentTo(numerosEsperados);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(this);
            try { File.Delete(rutaTemp); } catch { }
        }
    }
}
