using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Minijuegos;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>Sección "Minijuegos" dentro de la Galería: selector Biblioteca | Minijuegos, sin perder la partida al cambiar.</summary>
public class GaleriaMinijuegosTests
{
    private static readonly string[] Titulos = ["Naruto", "Bleach", "Death Note", "Clannad", "Monster", "Gintama", "Berserk", "Trigun"];

    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<IClipPlayer> _player = new();
    private readonly MinijuegosViewModel _minijuegos;

    public GaleriaMinijuegosTests()
    {
        var animes = Titulos.Select((t, i) => new AnimeItem { AniListId = i + 1, Titulo = t, Generos = "Action", AnioLanzamiento = 2000 + i, TotalEpisodios = 12 }).ToList();
        _db.Setup(d => d.ObtenerTodosLosAnimesAsync()).ReturnsAsync(animes);
        _db.Setup(d => d.ObtenerTodosLosRegistrosAsync()).ReturnsAsync(new List<RegistroEpisodio>());

        var themes = new Mock<IAnimeThemesService>();
        themes.Setup(t => t.ObtenerTemasAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<AnimeThemeInfo>());
        var descargas = new Mock<IAnimeThemesDownloadService>();
        descargas.Setup(d => d.ListarDescargasLocales(It.IsAny<int>())).Returns(new List<TemaLocalDisponible>());

        _minijuegos = new MinijuegosViewModel(
            new AdivinaAnimeViewModel(_db.Object),
            new AdivinaOpEdViewModel(_db.Object, themes.Object, descargas.Object, _player.Object),
            new AdivinaPersonajeViewModel(_db.Object, Mock.Of<IPersonajesService>()));
    }

    private GaleriaViewModel CrearSut(bool conMinijuegos = true)
    {
        var cache = new Mock<IImageCacheService>();
        cache.Setup(c => c.ObtenerPortadaEnMemoria(It.IsAny<int>())).Returns((ImageSource?)null);
        cache.Setup(c => c.ObtenerPortadaAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<int>())).ReturnsAsync((ImageSource?)null);

        return new GaleriaViewModel(
            Mock.Of<IAnimeTrackingService>(), _db.Object, Mock.Of<IAuthService>(), Mock.Of<IDialogService>(),
            Mock.Of<IHttpClientFactory>(), cache.Object, Mock.Of<IFileScannerService>(),
            conMinijuegos ? _minijuegos : null);
    }

    [Fact]
    public void AlAbrirLaGaleria_SeVeLaBiblioteca_YLosMinijuegosNoSeCargan()
    {
        var sut = CrearSut();

        sut.MostrandoMinijuegos.Should().BeFalse();
        sut.MostrandoBiblioteca.Should().BeTrue();
        sut.HayMinijuegos.Should().BeTrue();
        sut.ContenidoMinijuegos.Should().BeNull("la vista de los juegos solo existe con la sección abierta");
    }

    [Fact]
    public void SinViewModelDeMinijuegos_NoSeOfreceElSelector()
    {
        var sut = CrearSut(conMinijuegos: false);

        sut.HayMinijuegos.Should().BeFalse();
    }

    [Fact]
    public async Task MostrarMinijuegos_CambiaDeSeccion_YPreparaLosJuegos()
    {
        var sut = CrearSut();

        await sut.MostrarMinijuegosCommand.ExecuteAsync(null);

        sut.MostrandoMinijuegos.Should().BeTrue();
        sut.MostrandoBiblioteca.Should().BeFalse();
        sut.ContenidoMinijuegos.Should().BeSameAs(_minijuegos);
        _minijuegos.EnMenu.Should().BeTrue();
    }

    [Fact]
    public async Task MostrarMinijuegos_AvisaDelCambioDeLasPropiedadesQueUsaLaVista()
    {
        var sut = CrearSut();
        var avisadas = new List<string?>();
        sut.PropertyChanged += (_, e) => avisadas.Add(e.PropertyName);

        await sut.MostrarMinijuegosCommand.ExecuteAsync(null);

        avisadas.Should().Contain([nameof(GaleriaViewModel.MostrandoMinijuegos), nameof(GaleriaViewModel.MostrandoBiblioteca), nameof(GaleriaViewModel.ContenidoMinijuegos)]);
    }

    [Fact]
    public async Task MostrarMinijuegos_SinViewModel_NoHaceNada()
    {
        var sut = CrearSut(conMinijuegos: false);

        await sut.MostrarMinijuegosCommand.ExecuteAsync(null);

        sut.MostrandoMinijuegos.Should().BeFalse();
    }

    [Fact]
    public async Task MostrarBiblioteca_VuelveALaBiblioteca_YCortaElAudioDelJuegoAbierto()
    {
        var sut = CrearSut();
        await sut.MostrarMinijuegosCommand.ExecuteAsync(null);
        await _minijuegos.AbrirAdivinaOpEdCommand.ExecuteAsync(null);
        _player.Invocations.Clear();

        sut.MostrarBibliotecaCommand.Execute(null);

        sut.MostrandoMinijuegos.Should().BeFalse();
        sut.ContenidoMinijuegos.Should().BeNull();
        _player.Verify(p => p.Detener(), Times.AtLeastOnce, "un clip de Adivina el OP/ED no debe seguir sonando en la biblioteca");
    }

    [Fact]
    public async Task CambiarDeSeccion_NoCierraElJuegoAbierto_LaPartidaSigueAlVolver()
    {
        var sut = CrearSut();
        await sut.MostrarMinijuegosCommand.ExecuteAsync(null);
        await _minijuegos.AbrirAdivinaAnimeCommand.ExecuteAsync(null);
        await _minijuegos.AdivinaAnime.IniciarPartidaCommand.ExecuteAsync(null);
        _minijuegos.AdivinaAnime.RondaNumero.Should().Be(1);

        sut.MostrarBibliotecaCommand.Execute(null);
        await sut.MostrarMinijuegosCommand.ExecuteAsync(null);

        _minijuegos.JuegoActivo.Should().BeSameAs(_minijuegos.AdivinaAnime);
        _minijuegos.AdivinaAnime.EsJugando.Should().BeTrue("la partida en curso sobrevive al cambio de sección");
        _minijuegos.AdivinaAnime.RondaNumero.Should().Be(1);
    }

    [Fact]
    public void MostrarBiblioteca_YaEnLaBiblioteca_NoHaceNada()
    {
        var sut = CrearSut();

        sut.MostrarBibliotecaCommand.Execute(null);

        sut.MostrandoBiblioteca.Should().BeTrue();
        _player.Verify(p => p.Detener(), Times.Never);
    }

    [Fact]
    public async Task AlEntrarAsync_ConLaSeccionDeMinijuegosAbierta_RefrescaLosAnimesDisponibles()
    {
        var sut = CrearSut();
        await sut.MostrarMinijuegosCommand.ExecuteAsync(null);
        await _minijuegos.AbrirAdivinaAnimeCommand.ExecuteAsync(null);
        _db.Invocations.Clear();

        await sut.AlEntrarAsync();

        _db.Verify(d => d.ObtenerTodosLosAnimesAsync(), Times.AtLeastOnce);
    }

    [Fact]
    public async Task AlEntrarAsync_ConLaBiblioteca_NoTocaLosMinijuegos()
    {
        var sut = CrearSut();
        await Task.Delay(100); // deja terminar la carga inicial de la biblioteca
        _db.Invocations.Clear();

        await sut.AlEntrarAsync();

        _db.Verify(d => d.ObtenerTodosLosAnimesAsync(), Times.Never);
    }

    [Fact]
    public async Task DetenerJuegoActivo_SinJuegoAbierto_NoFalla()
    {
        await _minijuegos.PrepararAsync();

        Action detener = () => _minijuegos.DetenerJuegoActivo();

        detener.Should().NotThrow();
    }
}
