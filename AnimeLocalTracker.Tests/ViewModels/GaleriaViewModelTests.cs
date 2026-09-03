using System.Collections.Generic;
using System.Net.Http;
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

public class GaleriaViewModelTests
{
    // CA1861: arrays constantes reutilizados como campos estáticos
    private static readonly string[] GenerosEsperados = { "Acción", "Aventura", "Comedia", "Drama", "Fantasía", "Música", "Sobrenatural" };

    private readonly Mock<IAnimeTrackingService> _trackingServiceMock = new();
    private readonly Mock<IDatabaseService> _databaseServiceMock = new();
    private readonly Mock<IAuthService> _authServiceMock = new();
    private readonly Mock<IDialogService> _dialogServiceMock = new();
    private readonly Mock<IHttpClientFactory> _httpClientFactoryMock = new();
    private readonly Mock<IImageCacheService> _imageCacheServiceMock = new();
    private readonly Mock<IFileScannerService> _fileScannerServiceMock = new();

    private GaleriaViewModel CreateSut(List<AnimeItem>? animes = null, List<RegistroEpisodio>? registros = null)
    {
        _databaseServiceMock
            .Setup(d => d.ObtenerTodosLosAnimesAsync())
            .ReturnsAsync(animes ?? new List<AnimeItem>());

        _databaseServiceMock
            .Setup(d => d.ObtenerTodosLosRegistrosAsync())
            .ReturnsAsync(registros ?? new List<RegistroEpisodio>());

        return new GaleriaViewModel(
            _trackingServiceMock.Object,
            _databaseServiceMock.Object,
            _authServiceMock.Object,
            _dialogServiceMock.Object,
            _httpClientFactoryMock.Object,
            _imageCacheServiceMock.Object,
            _fileScannerServiceMock.Object
        );
    }

    [Fact]
    public async Task CargarBibliotecaAsync_DeberiaLlenarColeccionYActualizarPropiedades()
    {
        // Arrange
        var lista = new List<AnimeItem>
        {
            new() { AniListId = 1, Titulo = "Bleach", EstadoUsuario = "CURRENT" },
            new() { AniListId = 2, Titulo = "Death Note", EstadoUsuario = "COMPLETED" }
        };

        // Act
        var sut = CreateSut(lista);
        await Task.Delay(100); // Dar tiempo a la tarea asíncrona del constructor

        // Assert
        sut.BibliotecaLocales.Should().HaveCount(2);
        sut.TotalAnimesBiblioteca.Should().Be(2);
        sut.BibliotecaVacia.Should().BeFalse();
    }

    [Fact]
    public void Receive_AnimeAnadidoMensaje_DeberiaAgregarALaBibliotecaSiNoExiste()
    {
        // Arrange
        var sut = CreateSut();
        var nuevoAnime = new AnimeItem { AniListId = 50, Titulo = "Solo Leveling" };

        // Act
        sut.Receive(new AnimeAñadidoMensaje(nuevoAnime));

        // Assert
        sut.BibliotecaLocales.Should().ContainSingle(a => a.AniListId == 50);
        sut.TotalAnimesBiblioteca.Should().Be(1);
    }

    [Fact]
    public void CambiarFiltroEstadoCommand_DeberiaActualizarFiltroEstado()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.CambiarFiltroEstadoCommand.Execute("Completados");

        // Assert
        sut.FiltroEstado.Should().Be("Completados");
    }

    [Fact]
    public async Task ElegirQueVerHoy_ConEpisodiosNoVistos_DeberiaNavegarAlReproductor()
    {
        // Arrange
        var anime = new AnimeItem { AniListId = 50, Titulo = "One Piece", EstadoUsuario = "CURRENT", RutaCarpeta = "C:\\Anime\\OnePiece" };
        var episodiosLocales = new List<EpisodioItem>
        {
            new() { NumeroEpisodio = 1, RutaCompleta = "C:\\Anime\\OnePiece\\ep01.mkv", Visto = true },
            new() { NumeroEpisodio = 2, RutaCompleta = "C:\\Anime\\OnePiece\\ep02.mkv", Visto = false },
            new() { NumeroEpisodio = 3, RutaCompleta = "C:\\Anime\\OnePiece\\ep03.mkv", Visto = false }
        };
        _fileScannerServiceMock
            .Setup(s => s.EscanearEpisodiosAsync(anime.RutaCarpeta))
            .ReturnsAsync(episodiosLocales);
        _databaseServiceMock
            .Setup(d => d.ObtenerRegistrosPorAnimeAsync(50))
            .ReturnsAsync(new List<RegistroEpisodio> { new() { AniListId = 50, NumeroEpisodio = 1, VistoLocal = true } });

        var sut = CreateSut(new List<AnimeItem> { anime });
        await Task.Delay(100);

        NavegarMensaje_Reproductor? recibido = null;
        var recipient = new Recipient();
        WeakReferenceMessenger.Default.Register<NavegarMensaje_Reproductor>(recipient,
            (r, m) => recibido = m);
        try
        {
            // Act
            await sut.ElegirQueVerHoyCommand.ExecuteAsync(null);

            // Assert: navega al reproductor con el siguiente episodio no visto cronológicamente (episodio 2)
            recibido.Should().NotBeNull();
            recibido!.AnimeId.Should().Be(50);
            recibido.TituloAnime.Should().Be("One Piece");
            recibido.Episodio.Should().Be(2);
            recibido.EpisodiosDisponibles.Should().HaveCount(3);
            recibido.EpisodiosDisponibles!.Should().OnlyContain(e => !string.IsNullOrWhiteSpace(e.RutaCompleta));
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }
    }

    [Fact]
    public async Task ElegirQueVerHoy_TodosVistos_DeberiaMostrarDialogoYNoNavegar()
    {
        // Arrange
        var anime = new AnimeItem { AniListId = 50, Titulo = "One Piece", EstadoUsuario = "CURRENT", RutaCarpeta = "C:\\Anime\\OnePiece" };
        var episodiosLocales = new List<EpisodioItem>
        {
            new() { NumeroEpisodio = 1, RutaCompleta = "C:\\Anime\\OnePiece\\ep01.mkv" }
        };
        _fileScannerServiceMock
            .Setup(s => s.EscanearEpisodiosAsync(anime.RutaCarpeta))
            .ReturnsAsync(episodiosLocales);
        _databaseServiceMock
            .Setup(d => d.ObtenerRegistrosPorAnimeAsync(50))
            .ReturnsAsync(new List<RegistroEpisodio> { new() { AniListId = 50, NumeroEpisodio = 1, VistoLocal = true } });

        var sut = CreateSut(new List<AnimeItem> { anime });
        await Task.Delay(100);

        NavegarMensaje_Reproductor? recibido = null;
        var recipient = new Recipient();
        WeakReferenceMessenger.Default.Register<NavegarMensaje_Reproductor>(recipient,
            (r, m) => recibido = m);
        try
        {
            // Act
            await sut.ElegirQueVerHoyCommand.ExecuteAsync(null);

            // Assert
            recibido.Should().BeNull();
            _dialogServiceMock.Verify(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }
    }

    [Fact]
    public async Task ElegirQueVerHoy_SinCarpetas_DeberiaMostrarDialogoInformativo()
    {
        // Arrange
        var anime = new AnimeItem { AniListId = 50, Titulo = "One Piece", EstadoUsuario = "CURRENT" };
        var sut = CreateSut(new List<AnimeItem> { anime });
        await Task.Delay(100);

        // Act
        await sut.ElegirQueVerHoyCommand.ExecuteAsync(null);

        // Assert
        _dialogServiceMock.Verify(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        sut.EstaBuscandoQueVer.Should().BeFalse();
    }

    [Fact]
    public async Task ElegirQueVerHoy_SinEpisodiosSinVer_DeberiaPreferirAnimesEnCurso()
    {
        // Arrange: anime COMPLETED en biblioteca pero CURRENT también. Solo CURRENT tiene no vistos.
        var completado = new AnimeItem { AniListId = 1, Titulo = "Bleach", EstadoUsuario = "COMPLETED", RutaCarpeta = "C:\\Anime\\Bleach" };
        var enCurso = new AnimeItem { AniListId = 2, Titulo = "Jujutsu Kaisen", EstadoUsuario = "CURRENT", RutaCarpeta = "C:\\Anime\\JJK" };

        _fileScannerServiceMock
            .Setup(s => s.EscanearEpisodiosAsync("C:\\Anime\\Bleach"))
            .ReturnsAsync(new List<EpisodioItem> { new() { NumeroEpisodio = 1, RutaCompleta = "C:\\Anime\\Bleach\\ep01.mkv" }, });
        _fileScannerServiceMock
            .Setup(s => s.EscanearEpisodiosAsync("C:\\Anime\\JJK"))
            .ReturnsAsync(new List<EpisodioItem>
            {
                new() { NumeroEpisodio = 1, RutaCompleta = "C:\\Anime\\JJK\\ep01.mkv", Visto = true },
                new() { NumeroEpisodio = 2, RutaCompleta = "C:\\Anime\\JJK\\ep02.mkv", Visto = false }
            });
        _databaseServiceMock
            .Setup(d => d.ObtenerRegistrosPorAnimeAsync(1))
            .ReturnsAsync(new List<RegistroEpisodio>());
        _databaseServiceMock
            .Setup(d => d.ObtenerRegistrosPorAnimeAsync(2))
            .ReturnsAsync(new List<RegistroEpisodio>());

        var sut = CreateSut(new List<AnimeItem> { completado, enCurso });
        await Task.Delay(100);

        NavegarMensaje_Reproductor? recibido = null;
        var recipient = new Recipient();
        WeakReferenceMessenger.Default.Register<NavegarMensaje_Reproductor>(recipient,
            (r, m) => recibido = m);
        try
        {
            // Act
            await sut.ElegirQueVerHoyCommand.ExecuteAsync(null);

            // Assert: prefiere el anime en curso, no el completado
            recibido.Should().NotBeNull();
            recibido!.AnimeId.Should().Be(2);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }
    }

    [Fact]
    public async Task ActualizarGenerosDisponibles_DeberiaExtraerGenerosUnicosOrdenados()
    {
        // Arrange
        var animes = new List<AnimeItem>
        {
            new() { AniListId = 1, Titulo = "Frieren", Generos = "Aventura, Fantasía, Drama" },
            new() { AniListId = 2, Titulo = "Jujutsu Kaisen", Generos = "Acción, Fantasía, Sobrenatural" },
            new() { AniListId = 3, Titulo = "Bocchi the Rock!", Generos = "Comedia, Música" }
        };

        // Act
        var sut = CreateSut(animes);
        await Task.Delay(100);

        // Assert
        sut.GenerosDisponibles.Should().Contain("Todos los géneros");
        sut.GenerosDisponibles.Should().Contain(GenerosEsperados);
        sut.GenerosDisponibles[0].Should().Be("Todos los géneros");
    }

    [Fact]
    public async Task FiltrarPorGenero_DeberiaMostrarSoloAnimesConEseGenero()
    {
        // Arrange
        var animes = new List<AnimeItem>
        {
            new() { AniListId = 1, Titulo = "Frieren", Generos = "Aventura, Fantasía" },
            new() { AniListId = 2, Titulo = "Kaguya-sama", Generos = "Comedia, Romance" },
            new() { AniListId = 3, Titulo = "Jujutsu Kaisen", Generos = "Acción, Fantasía" }
        };

        var sut = CreateSut(animes);
        await Task.Delay(100);

        // Act
        sut.GeneroSeleccionado = "Romance";

        // Assert
        var filtrados = sut.BibliotecaFiltrada!.Cast<AnimeItem>().ToList();
        filtrados.Should().HaveCount(1);
        filtrados.Should().ContainSingle(a => a.Titulo == "Kaguya-sama");
        sut.HayFiltrosActivos.Should().BeTrue();
    }

    [Fact]
    public async Task FiltrarPorTexto_DeberiaBuscarEnTituloYSinonimos()
    {
        // Arrange
        var animes = new List<AnimeItem>
        {
            new() { AniListId = 1, Titulo = "Attack on Titan", NombresAlternativos = "Shingeki no Kyojin" },
            new() { AniListId = 2, Titulo = "Demon Slayer", NombresAlternativos = "Kimetsu no Yaiba" }
        };

        var sut = CreateSut(animes);
        await Task.Delay(100);

        // Act: Búsqueda por nombre alternativo
        sut.TextoBusqueda = "Shingeki";

        // Assert
        var filtrados = sut.BibliotecaFiltrada!.Cast<AnimeItem>().ToList();
        filtrados.Should().HaveCount(1);
        filtrados[0].Titulo.Should().Be("Attack on Titan");
    }

    [Fact]
    public async Task FiltrarPorSoloPendientes_DeberiaExcluirCompletados()
    {
        // Arrange
        var animes = new List<AnimeItem>
        {
            new() { AniListId = 1, Titulo = "Frieren", TotalEpisodios = 28, EstadoUsuario = "CURRENT" },
            new() { AniListId = 2, Titulo = "Death Note", TotalEpisodios = 1, EstadoUsuario = "COMPLETED" }
        };

        var registros = new List<RegistroEpisodio>
        {
            new() { AniListId = 2, NumeroEpisodio = 1, VistoLocal = true }
        };

        var sut = CreateSut(animes, registros);
        await Task.Delay(100);

        // Act
        sut.SoloConEpisodiosPendientes = true;

        // Assert
        var filtrados = sut.BibliotecaFiltrada!.Cast<AnimeItem>().ToList();
        filtrados.Should().HaveCount(1);
        filtrados[0].Titulo.Should().Be("Frieren");
    }

    [Fact]
    public async Task FiltrarPorSoloConCarpetaLocal_DeberiaExcluirAnimesSinRuta()
    {
        // Arrange
        var animes = new List<AnimeItem>
        {
            new() { AniListId = 1, Titulo = "Frieren", RutaCarpeta = "C:\\Anime\\Frieren" },
            new() { AniListId = 2, Titulo = "One Piece", RutaCarpeta = "" }
        };

        var sut = CreateSut(animes);
        await Task.Delay(100);

        // Act
        sut.SoloConCarpetaLocal = true;

        // Assert
        var filtrados = sut.BibliotecaFiltrada!.Cast<AnimeItem>().ToList();
        filtrados.Should().HaveCount(1);
        filtrados[0].Titulo.Should().Be("Frieren");
    }

    [Fact]
    public async Task AplicarOrdenacion_PorTitulo_DeberiaOrdenarCorrectamente()
    {
        // Arrange
        var animes = new List<AnimeItem>
        {
            new() { AniListId = 1, Titulo = "Bleach" },
            new() { AniListId = 2, Titulo = "Zom 100" },
            new() { AniListId = 3, Titulo = "Attack on Titan" }
        };

        var sut = CreateSut(animes);
        await Task.Delay(100);

        // Act 1: Título Descendente (Z - A)
        sut.CriterioOrdenSeleccionado = "Título (Z - A)";
        var desc = sut.BibliotecaFiltrada!.Cast<AnimeItem>().ToList();
        desc[0].Titulo.Should().Be("Zom 100");
        desc[1].Titulo.Should().Be("Bleach");
        desc[2].Titulo.Should().Be("Attack on Titan");

        // Act 2: Título Ascendente (A - Z)
        sut.CriterioOrdenSeleccionado = "Título (A - Z)";
        var asc = sut.BibliotecaFiltrada!.Cast<AnimeItem>().ToList();
        asc[0].Titulo.Should().Be("Attack on Titan");
        asc[1].Titulo.Should().Be("Bleach");
        asc[2].Titulo.Should().Be("Zom 100");
    }

    [Fact]
    public async Task AplicarOrdenacion_PorTotalEpisodios_DeberiaOrdenarCorrectamente()
    {
        // Arrange
        var animes = new List<AnimeItem>
        {
            new() { AniListId = 1, Titulo = "Película", TotalEpisodios = 1 },
            new() { AniListId = 2, Titulo = "Serie Larga", TotalEpisodios = 100 },
            new() { AniListId = 3, Titulo = "Serie Media", TotalEpisodios = 24 }
        };

        var sut = CreateSut(animes);
        await Task.Delay(100);

        // Act: Más Episodios
        sut.CriterioOrdenSeleccionado = "Más Episodios";
        var ordenados = sut.BibliotecaFiltrada!.Cast<AnimeItem>().ToList();
        ordenados[0].Titulo.Should().Be("Serie Larga");
        ordenados[1].Titulo.Should().Be("Serie Media");
        ordenados[2].Titulo.Should().Be("Película");
    }

    [Fact]
    public async Task LimpiarFiltros_DeberiaRestablecerTodosLosFiltrosYOrden()
    {
        // Arrange
        var animes = new List<AnimeItem>
        {
            new() { AniListId = 1, Titulo = "Frieren", Generos = "Aventura" },
            new() { AniListId = 2, Titulo = "Bleach", Generos = "Acción" }
        };

        var sut = CreateSut(animes);
        await Task.Delay(100);

        sut.TextoBusqueda = "Frieren";
        sut.FiltroEstado = "Viendo";
        sut.GeneroSeleccionado = "Aventura";
        sut.SoloConEpisodiosPendientes = true;
        sut.SoloConCarpetaLocal = true;
        sut.CriterioOrdenSeleccionado = "Título (Z - A)";

        sut.HayFiltrosActivos.Should().BeTrue();

        // Act
        sut.LimpiarFiltrosCommand.Execute(null);

        // Assert
        sut.TextoBusqueda.Should().BeEmpty();
        sut.FiltroEstado.Should().Be("Todos");
        sut.GeneroSeleccionado.Should().Be("Todos los géneros");
        sut.SoloConEpisodiosPendientes.Should().BeFalse();
        sut.SoloConCarpetaLocal.Should().BeFalse();
        sut.CriterioOrdenSeleccionado.Should().Be("Título (A - Z)");
        sut.HayFiltrosActivos.Should().BeFalse();
        sut.CantidadFiltrosAvanzadosActivos.Should().Be(0);
        sut.TieneFiltrosAvanzadosActivos.Should().BeFalse();
    }

    [Fact]
    public async Task CantidadFiltrosAvanzadosActivos_DeberiaContarFiltrosCorrectamente()
    {
        // Arrange
        var animes = new List<AnimeItem>
        {
            new() { AniListId = 1, Titulo = "Frieren", Generos = "Aventura" }
        };

        var sut = CreateSut(animes);
        await Task.Delay(100);

        sut.CantidadFiltrosAvanzadosActivos.Should().Be(0);
        sut.TieneFiltrosAvanzadosActivos.Should().BeFalse();

        // Act & Assert progresivo
        sut.GeneroSeleccionado = "Aventura";
        sut.CantidadFiltrosAvanzadosActivos.Should().Be(1);
        sut.TieneFiltrosAvanzadosActivos.Should().BeTrue();

        sut.SoloConEpisodiosPendientes = true;
        sut.CantidadFiltrosAvanzadosActivos.Should().Be(2);

        sut.CriterioOrdenSeleccionado = "Más Recientes";
        sut.CantidadFiltrosAvanzadosActivos.Should().Be(3);

        sut.SoloConCarpetaLocal = true;
        sut.CantidadFiltrosAvanzadosActivos.Should().Be(4);

        // Toggle panel
        sut.PanelFiltrosVisible.Should().BeFalse();
        sut.TogglePanelFiltrosCommand.Execute(null);
        sut.PanelFiltrosVisible.Should().BeTrue();
    }

    public class Recipient : IRecipient<NavegarMensaje_Reproductor>
    {
        public void Receive(NavegarMensaje_Reproductor message) { }
    }
}
