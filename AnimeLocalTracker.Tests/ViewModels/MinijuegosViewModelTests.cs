using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Minijuegos;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>Menú de la pestaña Minijuegos: qué juego está abierto y qué pasa al abrir/cerrar uno.</summary>
public class MinijuegosViewModelTests
{
    private static readonly string[] Titulos = ["Naruto", "Bleach", "Death Note", "Clannad", "Monster"];

    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<IClipPlayer> _player = new();

    private MinijuegosViewModel CrearSut()
    {
        var animes = Titulos
            .Select((t, i) => new AnimeItem { AniListId = i + 1, Titulo = t, Generos = "Action, Drama", AnioLanzamiento = 2000 + i, TotalEpisodios = 12 }).ToList();
        _db.Setup(d => d.ObtenerTodosLosAnimesAsync()).ReturnsAsync(animes);

        var themes = new Mock<IAnimeThemesService>();
        themes.Setup(t => t.ObtenerTemasAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<AnimeThemeInfo>());
        var descargas = new Mock<IAnimeThemesDownloadService>();
        descargas.Setup(d => d.ListarDescargasLocales(It.IsAny<int>())).Returns(new List<TemaLocalDisponible>());

        return new MinijuegosViewModel(
            new AdivinaAnimeViewModel(_db.Object),
            new AdivinaOpEdViewModel(_db.Object, themes.Object, descargas.Object, _player.Object),
            new AdivinaPersonajeViewModel(_db.Object, Mock.Of<IPersonajesService>()));
    }

    [Fact]
    public void AlEntrar_DeberiaMostrarElMenuSinJuegoAbierto()
    {
        var sut = CrearSut();

        sut.EnMenu.Should().BeTrue();
        sut.JuegoActivo.Should().BeNull();
    }

    [Fact]
    public async Task PrepararAsync_EnElMenu_NoDeberiaCargarNada()
    {
        var sut = CrearSut();

        await sut.PrepararAsync();

        _db.Verify(d => d.ObtenerTodosLosAnimesAsync(), Times.Never);
    }

    [Fact]
    public async Task AbrirAdivinaAnime_DeberiaAbrirEseJuegoYPrepararlo()
    {
        var sut = CrearSut();

        await sut.AbrirAdivinaAnimeCommand.ExecuteAsync(null);

        sut.JuegoActivo.Should().BeSameAs(sut.AdivinaAnime);
        sut.EnMenu.Should().BeFalse();
        sut.AdivinaAnime.AnimesDisponibles.Should().Be(5);
        sut.AdivinaAnime.PuedeJugar.Should().BeTrue();
    }

    [Fact]
    public async Task AbrirAdivinaOpEd_DeberiaAbrirEseJuegoYPrepararlo()
    {
        var sut = CrearSut();

        await sut.AbrirAdivinaOpEdCommand.ExecuteAsync(null);

        sut.JuegoActivo.Should().BeSameAs(sut.AdivinaOpEd);
        sut.AdivinaOpEd.PuedeJugar.Should().BeTrue();
    }

    [Fact]
    public async Task VolverAlMenu_DeberiaCerrarElJuegoYCortarElSonido()
    {
        var sut = CrearSut();
        await sut.AbrirAdivinaOpEdCommand.ExecuteAsync(null);
        _player.Invocations.Clear();

        sut.VolverAlMenuCommand.Execute(null);

        sut.EnMenu.Should().BeTrue();
        sut.JuegoActivo.Should().BeNull();
        _player.Verify(p => p.Detener(), Times.Once);
    }

    [Fact]
    public async Task PrepararAsync_ConUnJuegoAbierto_DeberiaPrepararEseJuego()
    {
        var sut = CrearSut();
        await sut.AbrirAdivinaAnimeCommand.ExecuteAsync(null);
        _db.Invocations.Clear();

        await sut.PrepararAsync(); // p. ej. vuelves a la pestaña Minijuegos con Adivina el anime abierto

        _db.Verify(d => d.ObtenerTodosLosAnimesAsync(), Times.Once);
    }

    [Fact]
    public async Task UnaPartidaEnCurso_DeberiaSobrevivirAVolverAlMenuYReabrir()
    {
        var sut = CrearSut();
        await sut.AbrirAdivinaAnimeCommand.ExecuteAsync(null);
        await sut.AdivinaAnime.IniciarPartidaCommand.ExecuteAsync(null);
        int ronda = sut.AdivinaAnime.RondaNumero;

        sut.VolverAlMenuCommand.Execute(null);
        await sut.AbrirAdivinaAnimeCommand.ExecuteAsync(null);

        sut.AdivinaAnime.EsJugando.Should().BeTrue("PrepararAsync no interrumpe una partida");
        sut.AdivinaAnime.RondaNumero.Should().Be(ronda);
    }
}
