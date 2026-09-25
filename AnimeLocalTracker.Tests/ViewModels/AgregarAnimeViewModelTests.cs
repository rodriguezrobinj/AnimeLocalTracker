using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

public class AgregarAnimeViewModelTests : IDisposable
{
    private readonly Mock<IAnimeTrackingService> _trackingMock = new();
    private readonly Mock<IDatabaseService> _dbMock = new();
    private readonly Mock<ISettingsService> _settingsMock = new();
    private readonly Mock<IDialogService> _dialogMock = new();
    private readonly AnimeLibraryService _libraryService;
    private readonly string _tempFolder;

    public AgregarAnimeViewModelTests()
    {
        _tempFolder = Path.Combine(Path.GetTempPath(), "AnimeLocalTracker_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempFolder);
        _settingsMock.Setup(s => s.ObtenerRutaBaseAnimes()).Returns(_tempFolder);

        _dbMock.Setup(d => d.ObtenerTodosLosAnimesAsync()).ReturnsAsync(new List<AnimeItem>());
        _trackingMock.Setup(t => t.ObtenerAnimesTendenciaAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AniListMedia>
            {
                new()
                {
                    Id = 1,
                    Title = new AniListTitle { Romaji = "Frieren", English = "Frieren: Beyond Journey's End" },
                    Status = "FINISHED",
                    Episodes = 28
                }
            });

        _libraryService = new AnimeLibraryService(_dbMock.Object, _settingsMock.Object);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            if (Directory.Exists(_tempFolder))
            {
                Directory.Delete(_tempFolder, true);
            }
        }
        catch { }
    }

    private AgregarAnimeViewModel CreateSut()
    {
        return new AgregarAnimeViewModel(
            _trackingMock.Object,
            _dbMock.Object,
            _libraryService,
            _dialogMock.Object);
    }

    [Fact]
    public async Task CargarTendencias_DeberiaLlenarResultadosYActualizarEstado()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        await sut.CargarTendenciasAsync();

        // Assert
        sut.Resultados.Should().HaveCount(1);
        sut.Resultados[0].TituloPrincipal.Should().Be("Frieren");
        sut.MostrandoTendencias.Should().BeTrue();
        sut.TituloSeccion.Should().Be("Tendencias de la temporada");
        sut.BusquedaSinResultados.Should().BeFalse();
    }

    /// <summary>
    /// El debounce de la búsqueda en vivo usa un Task.Delay(350) real: su continuación se reanuda
    /// en un hilo de threadpool, y la mutación posterior de <c>Resultados</c> (ObservableCollection
    /// envuelta en un CollectionView) exige el mismo hilo que la creó — en la app real ese hilo
    /// tiene el Dispatcher de WPF y el SynchronizationContext ambiental ya lo garantiza "gratis".
    /// En un test xUnit puro no hay Dispatcher, así que se instala uno explícito y se bombea su cola
    /// de mensajes mientras se espera, en vez de un simple Task.Delay.
    /// </summary>
    private static void BombearHastaQue(Func<bool> condicion, int timeoutMs = 3000)
    {
        var frame = new DispatcherFrame();
        var cronometro = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(15) };
        timer.Tick += (_, _) =>
        {
            if (condicion() || cronometro.ElapsedMilliseconds > timeoutMs)
            {
                frame.Continue = false;
            }
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    [Fact]
    public void TextoBusqueda_ConDosCaracteresOMas_DeberiaBuscarEnVivoYNoMostrarTendencias()
    {
        // Arrange
        _trackingMock.Setup(t => t.BuscarAnimesEnVivoAsync("Bleach", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AniListMedia>
            {
                new() { Id = 50, Title = new AniListTitle { Romaji = "Bleach" }, Status = "FINISHED", Episodes = 366 }
            });

        var contextoPrevio = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            var sut = CreateSut(); // la carga inicial (mocks ya resueltos) completa sincrónicamente aquí

            // Act: IsSearching solo vuelve a false en el "finally" de EjecutarBusquedaEnVivoAsync,
            // después de que Resultados ya quedó actualizado — señal precisa de "terminó".
            sut.TextoBusqueda = "Bleach";
            BombearHastaQue(() => !sut.IsSearching && !sut.MostrandoTendencias);

            // Assert
            sut.MostrandoTendencias.Should().BeFalse();
            sut.Resultados.Should().ContainSingle(r => r.TituloPrincipal == "Bleach");
            _trackingMock.Verify(t => t.BuscarAnimesEnVivoAsync("Bleach", It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(contextoPrevio);
        }
    }

    [Fact]
    public async Task TextoBusqueda_ConUnSoloCaracter_DeberiaVolverATendencias()
    {
        // Arrange
        var sut = CreateSut();
        await Task.Delay(50);

        // Act: un solo carácter no dispara búsqueda en vivo; cae de vuelta a tendencias.
        sut.TextoBusqueda = "B";
        await Task.Delay(200);

        // Assert
        sut.MostrandoTendencias.Should().BeTrue();
        _trackingMock.Verify(t => t.BuscarAnimesEnVivoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FiltroDeGenero_DeberiaMostrarSoloResultadosConEseGenero_YLimpiarFiltrosLosRestaura()
    {
        // Arrange: dos animes de tendencias con géneros distintos.
        _trackingMock.Setup(t => t.ObtenerAnimesTendenciaAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AniListMedia>
            {
                new() { Id = 1, Title = new AniListTitle { Romaji = "Frieren" }, Status = "FINISHED", Episodes = 28, Genres = ["Fantasy", "Drama"] },
                new() { Id = 2, Title = new AniListTitle { Romaji = "Solo Leveling" }, Status = "RELEASING", Episodes = 12, Genres = ["Action"] }
            });
        var sut = CreateSut();
        await Task.Delay(50);

        // Act: filtrar por "Action"
        sut.GeneroSeleccionado = "Action";

        // Assert
        var visibles = sut.ResultadosFiltrados.Cast<AnimeBusquedaItem>().ToList();
        visibles.Should().ContainSingle(a => a.TituloPrincipal == "Solo Leveling");

        // Act: limpiar filtros
        sut.LimpiarFiltrosCommand.Execute(null);

        // Assert
        sut.GeneroSeleccionado.Should().Be(AgregarAnimeViewModel.TodosLosGeneros);
        sut.ResultadosFiltrados.Cast<AnimeBusquedaItem>().Should().HaveCount(2);
    }

    [Fact]
    public async Task ReceiveAnimeAñadido_ConItemYaEnResultados_DeberiaMarcarloEnBiblioteca()
    {
        // Arrange
        var sut = CreateSut();
        await Task.Delay(50); // deja completar la carga inicial: trae "Frieren" (Id=1) a Resultados

        sut.Resultados.Should().ContainSingle(r => r.Media.Id == 1 && !r.EstaEnBiblioteca);

        // Act: se añadió ese mismo anime desde otra pantalla (o desde AñadirAnimeAsync)
        sut.Receive(new AnimeAñadidoMensaje(new AnimeItem { AniListId = 1, Titulo = "Frieren" }));

        // Assert: el ítem ya visible en Resultados refleja el cambio sin releer la BD.
        sut.Resultados.Single(r => r.Media.Id == 1).EstaEnBiblioteca.Should().BeTrue();
    }

    [Fact]
    public async Task CargarTendencias_SiLaApiFalla_DeberiaMostrarToastDeError()
    {
        // Arrange: la carga inicial del constructor debe completar SIN fallar antes de armar el
        // mock que lanza, para no contaminar el conteo de invocaciones del toast.
        var sut = CreateSut();
        await Task.Delay(50);
        _trackingMock.Setup(t => t.ObtenerAnimesTendenciaAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("network down"));

        // Act
        await sut.CargarTendenciasAsync();

        // Assert: el usuario no debe quedarse sin ninguna señal del fallo de red.
        _dialogMock.Verify(d => d.MostrarToast(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task EjecutarBusquedaEnVivo_SiLaApiFalla_DeberiaMostrarToastDeError()
    {
        // Arrange
        var sut = CreateSut();
        await Task.Delay(50);
        _trackingMock.Setup(t => t.BuscarAnimesEnVivoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("network down"));

        // Act
        sut.TextoBusqueda = "Bleach";
        await Task.Delay(500);

        // Assert
        _dialogMock.Verify(d => d.MostrarToast(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public void LimpiarBusqueda_DeberiaResetearTextoYCargarTendencias()
    {
        // Arrange
        var sut = CreateSut();
        sut.TextoBusqueda = "Solo Leveling";

        // Act
        sut.LimpiarBusquedaCommand.Execute(null);

        // Assert
        sut.TextoBusqueda.Should().BeEmpty();
    }

    [Fact]
    public async Task AñadirAnime_AnimeNuevo_DeberiaGuardarEnDbYNotificar()
    {
        // Arrange
        _dbMock.Setup(d => d.ObtenerTodosLosAnimesAsync()).ReturnsAsync(new List<AnimeItem>());
        var sut = CreateSut();
        var item = new AnimeBusquedaItem
        {
            Media = new AniListMedia
            {
                Id = 100,
                Title = new AniListTitle { Romaji = "Sousou no Frieren" },
                Episodes = 28,
                Status = "FINISHED"
            }
        };

        AnimeAñadidoMensaje? mensajeRecibido = null;
        WeakReferenceMessenger.Default.UnregisterAll(this);
        WeakReferenceMessenger.Default.Register<AgregarAnimeViewModelTests, AnimeAñadidoMensaje>(this, (r, m) =>
        {
            mensajeRecibido = m;
        });

        // Act
        await sut.AñadirAnimeAsync(item);

        // Assert
        item.EstaEnBiblioteca.Should().BeTrue();
        _dbMock.Verify(d => d.GuardarAnimeAsync(It.Is<AnimeItem>(a => a.AniListId == 100 && a.Titulo == "Sousou no Frieren")), Times.Once);
        mensajeRecibido.Should().NotBeNull();
        mensajeRecibido!.NuevoAnime.AniListId.Should().Be(100);

        GC.KeepAlive(this);
        WeakReferenceMessenger.Default.UnregisterAll(this);
    }

    [Fact]
    public async Task AñadirAnime_AnimeExistente_DeberiaMostrarDialogoYNoDuplicar()
    {
        // Arrange
        var animeExistente = new AnimeItem { AniListId = 200, Titulo = "Dungeon Meshi" };
        _dbMock.Setup(d => d.ObtenerTodosLosAnimesAsync()).ReturnsAsync(new List<AnimeItem> { animeExistente });
        _dbMock.Setup(d => d.ExisteAnimeAsync(It.IsAny<int>())).ReturnsAsync(true); // PERF-03

        var sut = CreateSut();
        var item = new AnimeBusquedaItem
        {
            Media = new AniListMedia
            {
                Id = 200,
                Title = new AniListTitle { Romaji = "Dungeon Meshi" }
            }
        };

        // Act
        await sut.AñadirAnimeAsync(item);

        // Assert
        item.EstaEnBiblioteca.Should().BeTrue();
        _dbMock.Verify(d => d.GuardarAnimeAsync(It.IsAny<AnimeItem>()), Times.Never);
        _dialogMock.Verify(d => d.MostrarDialogoAsync("Anime Existente", It.IsAny<string>(), false, "InformationOutline", "#FF9800"), Times.Once);
    }

    [Fact]
    public async Task VerEnBiblioteca_DeberiaEnviarNavegarMensajeDetalle()
    {
        // Arrange
        var animeExistente = new AnimeItem { AniListId = 300, Titulo = "Bleach" };
        _dbMock.Setup(d => d.ObtenerTodosLosAnimesAsync()).ReturnsAsync(new List<AnimeItem> { animeExistente });
        _dbMock.Setup(d => d.ExisteAnimeAsync(It.IsAny<int>())).ReturnsAsync(true); // PERF-03

        var sut = CreateSut();
        var item = new AnimeBusquedaItem
        {
            Media = new AniListMedia { Id = 300, Title = new AniListTitle { Romaji = "Bleach" } }
        };

        NavegarMensaje_Detalle? mensajeDetalle = null;
        WeakReferenceMessenger.Default.UnregisterAll(this);
        WeakReferenceMessenger.Default.Register<NavegarMensaje_Detalle>(this, (r, m) =>
        {
            mensajeDetalle = m;
        });

        // Act
        await sut.VerEnBibliotecaAsync(item);

        // Assert
        mensajeDetalle.Should().NotBeNull();
        mensajeDetalle!.AnimeSeleccionado.AniListId.Should().Be(300);

        WeakReferenceMessenger.Default.UnregisterAll(this);
    }
}
