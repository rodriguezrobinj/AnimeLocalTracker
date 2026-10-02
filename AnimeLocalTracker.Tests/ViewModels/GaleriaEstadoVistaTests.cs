using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>Lo que la Galería enseña mientras carga y cómo se mantiene al día cuando algo cambia fuera de ella.</summary>
public class GaleriaEstadoVistaTests
{
    private readonly Mock<IDatabaseService> _database = new();

    private GaleriaViewModel Crear() => new(
        new Mock<IAnimeTrackingService>().Object, _database.Object, new Mock<IAuthService>().Object, new Mock<IDialogService>().Object,
        new Mock<IHttpClientFactory>().Object, new Mock<IImageCacheService>().Object, new Mock<IFileScannerService>().Object);

    private async Task<GaleriaViewModel> CrearCargadaAsync(params AnimeItem[] animes)
    {
        _database.Setup(d => d.ObtenerTodosLosAnimesAsync()).ReturnsAsync(animes.ToList());
        _database.Setup(d => d.ObtenerTodosLosRegistrosAsync()).ReturnsAsync(new List<RegistroEpisodio>());
        var sut = Crear();
        await Task.Delay(100); // la biblioteca se carga desde el constructor
        return sut;
    }

    private static List<string> Visibles(GaleriaViewModel sut) => sut.BibliotecaFiltrada!.Cast<AnimeItem>().Select(a => a.Titulo).ToList();

    [Fact]
    public async Task MientrasCarga_NoDiceQueLaBibliotecaEstaVacia()
    {
        var lectura = new TaskCompletionSource<List<AnimeItem>>();
        _database.Setup(d => d.ObtenerTodosLosAnimesAsync()).Returns(lectura.Task);
        _database.Setup(d => d.ObtenerTodosLosRegistrosAsync()).ReturnsAsync(new List<RegistroEpisodio>());

        var sut = Crear();

        sut.EstaCargando.Should().BeTrue();
        sut.BibliotecaVacia.Should().BeFalse("todavía no se sabe si está vacía");

        lectura.SetResult(new List<AnimeItem>());
        await Task.Delay(100);

        sut.EstaCargando.Should().BeFalse();
        sut.BibliotecaVacia.Should().BeTrue();
    }

    [Theory]
    [InlineData("pokemon")]
    [InlineData("POKÉMON")]
    public async Task Buscador_NoDistingueTildesNiMayusculas(string busqueda)
    {
        var sut = await CrearCargadaAsync(
            new AnimeItem { AniListId = 1, Titulo = "Pokémon" },
            new AnimeItem { AniListId = 2, Titulo = "Bleach" });

        sut.TextoBusqueda = busqueda;

        Visibles(sut).Should().Equal("Pokémon");
    }

    [Theory]
    [InlineData("Viendo", "Bleach,Naruto")] // lo que estás viendo, también si lo estás viendo de nuevo
    [InlineData("EnPausa", "Monster")]
    [InlineData("Abandonados", "Berserk")]
    public async Task FiltroDeEstado_CubreTodosLosEstados(string filtro, string esperados)
    {
        var sut = await CrearCargadaAsync(
            new AnimeItem { AniListId = 1, Titulo = "Bleach", EstadoUsuario = "CURRENT" },
            new AnimeItem { AniListId = 2, Titulo = "Naruto", EstadoUsuario = "REPEATING" },
            new AnimeItem { AniListId = 3, Titulo = "Monster", EstadoUsuario = "PAUSED" },
            new AnimeItem { AniListId = 4, Titulo = "Berserk", EstadoUsuario = "DROPPED" },
            new AnimeItem { AniListId = 5, Titulo = "Clannad", EstadoUsuario = "PLANNING" });

        sut.CambiarFiltroEstadoCommand.Execute(filtro);

        Visibles(sut).Should().Equal(esperados.Split(','));
    }

    [Fact]
    public async Task FiltroDeTemporada_SeConservaAlCambiarDeIdioma()
    {
        var sut = await CrearCargadaAsync(
            new AnimeItem { AniListId = 1, Titulo = "Frieren", Temporada = "FALL", AnioLanzamiento = 2023 },
            new AnimeItem { AniListId = 2, Titulo = "Dandadan", Temporada = "WINTER", AnioLanzamiento = 2024 });
        sut.TemporadasDisponibles.Should().Equal("", "WINTER", "FALL"); // códigos: el texto lo pone la vista en el idioma de cada momento

        sut.TemporadaSeleccionada = "FALL";
        sut.Receive(new IdiomaCambiadoMensaje());

        sut.TemporadaSeleccionada.Should().Be("FALL");
        Visibles(sut).Should().Equal("Frieren");
        sut.HayFiltrosActivos.Should().BeTrue();
    }

    [Fact]
    public async Task AlVerUnEpisodio_ElFiltroDePendientesSeActualizaSolo()
    {
        var pelicula = new AnimeItem { AniListId = 1, Titulo = "Your Name", TotalEpisodios = 1 };
        var sut = await CrearCargadaAsync(pelicula, new AnimeItem { AniListId = 2, Titulo = "Bleach", TotalEpisodios = 366 });
        sut.SoloConEpisodiosPendientes = true;
        _database.Setup(d => d.ObtenerRegistrosPorAnimeAsync(1))
            .ReturnsAsync(new List<RegistroEpisodio> { new() { AniListId = 1, NumeroEpisodio = 1, VistoLocal = true } });

        sut.Receive(new EpisodioActualizadoMensaje(1, 1, VistoLocal: true));
        await Task.Delay(150);

        pelicula.EpisodiosVistos.Should().Be(1);
        Visibles(sut).Should().Equal("Bleach");
    }

    [Fact]
    public async Task AlVolverALaGaleria_ElFiltroDeEstadoReflejaLoCambiadoEnLaFicha()
    {
        var bleach = new AnimeItem { AniListId = 1, Titulo = "Bleach", EstadoUsuario = "CURRENT" };
        var sut = await CrearCargadaAsync(bleach, new AnimeItem { AniListId = 2, Titulo = "Naruto", EstadoUsuario = "CURRENT" });
        sut.CambiarFiltroEstadoCommand.Execute("Viendo");

        bleach.EstadoUsuario = "COMPLETED"; // lo que hace el editor de seguimiento de la Ficha
        await sut.AlEntrarAsync();

        Visibles(sut).Should().Equal("Naruto");
    }
}
