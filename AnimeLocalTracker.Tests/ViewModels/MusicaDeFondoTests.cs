using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>
/// "Seguir sonando fuera de la ficha": con la opción activa, la música de una ficha sobrevive a su cierre, se controla desde
/// fuera y vuelve a la ficha de su anime tal como iba. Con la opción apagada, todo sigue como antes (se corta al salir).
/// </summary>
public class MusicaDeFondoTests : IDisposable
{
    private readonly Mock<IAnimeThemesService> _themes = new();
    private readonly Mock<IAnimeThemesDownloadService> _descargas = new();
    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<IFileScannerService> _escaner = new();
    private readonly Mock<IDownloadService> _downloadService = new();
    private readonly Mock<ISettingsService> _ajustes = new();
    private readonly AppSettings _config = new();
    private readonly MusicaDeFondoService _fondo = new();
    private readonly List<FakeAudioTrackPlayer> _reproductores = new();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _fondo.Dispose();
        foreach (var r in _reproductores) r.Dispose();
    }

    public MusicaDeFondoTests()
    {
        _themes.Setup(s => s.ObtenerTemasAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new List<AnimeThemeInfo> { Tema("OP1"), Tema("ED1") });
        _descargas.Setup(d => d.EstaDescargado(It.IsAny<int>(), It.IsAny<AnimeThemeInfo>())).Returns(true);
        _descargas.Setup(d => d.ObtenerRutaLocalEsperada(It.IsAny<int>(), It.IsAny<AnimeThemeInfo>()))
            .Returns((int id, AnimeThemeInfo t) => $@"C:\Music\{id}\{t.Slug}.mp3");
        _ajustes.Setup(s => s.ObtenerConfiguracion()).Returns(_config);
        _ajustes.Setup(s => s.GuardarConfiguracionAsync(It.IsAny<AppSettings>())).Returns(Task.CompletedTask);
        _escaner.Setup(e => e.EscanearEpisodiosAsync(It.IsAny<string>())).ReturnsAsync(new List<EpisodioItem>());
        _db.Setup(d => d.ObtenerRegistrosPorAnimeAsync(It.IsAny<int>())).ReturnsAsync(new List<RegistroEpisodio>());
        double p = 0;
        _downloadService.Setup(d => d.EstaDescargando(It.IsAny<int>(), It.IsAny<int>(), out p)).Returns(false);
    }

    private static AnimeThemeInfo Tema(string slug) => new()
    {
        Slug = slug,
        Tipo = slug.StartsWith("ED", StringComparison.Ordinal) ? "ED" : "OP",
        TituloCancion = $"Canción {slug}",
        AudioUrlOgg = $"https://a.animethemes.moe/{slug}.ogg"
    };

    private FakeAudioTrackPlayer Reproductor(DetalleViewModel ficha) => _porFicha[ficha];
    private readonly Dictionary<DetalleViewModel, FakeAudioTrackPlayer> _porFicha = new();

    private async Task<DetalleViewModel> AbrirFichaAsync(int aniListId, bool esperarTemas = true)
    {
        var reproductor = new FakeAudioTrackPlayer();
        _reproductores.Add(reproductor);
        var ficha = new DetalleViewModel(
            Mock.Of<IAnimeTrackingService>(), _db.Object, Mock.Of<IAuthService>(), _escaner.Object, Mock.Of<IDialogService>(), _downloadService.Object,
            settingsService: _ajustes.Object, animeThemesService: _themes.Object, animeThemesDownload: _descargas.Object,
            audioTrackPlayer: reproductor, musicaDeFondo: _fondo);
        _porFicha[ficha] = reproductor;
        await ficha.InicializarAsync(new AnimeItem { AniListId = aniListId, Titulo = $"Anime {aniListId}" });
        if (esperarTemas) await ficha.Musica.CargaTemasMusicalesTarea;
        return ficha;
    }

    private static void Sonar(DetalleViewModel ficha, int indice = 0) => ficha.Musica.ReproducirTemaCommand.Execute(ficha.Musica.TemasMusicales[indice]);

    // ── Opción apagada: como siempre ──

    [Fact]
    public async Task ConLaOpcionApagada_AlSalirDeLaFicha_LaMusicaSeCorta()
    {
        var ficha = await AbrirFichaAsync(7);
        Sonar(ficha);

        ficha.Dispose();
        ficha.Musica.AlOcultarLaFicha();

        _fondo.HayMusica.Should().BeFalse();
        Reproductor(ficha).Disposed.Should().BeTrue();
        Reproductor(ficha).EstaSonando.Should().BeFalse();
    }

    // ── Opción activa ──

    [Fact]
    public async Task ConLaOpcionActiva_AlSalirConUnTemaSonando_LaMusicaSigue()
    {
        _config.MusicaSigueFueraDeLaFicha = true;
        var ficha = await AbrirFichaAsync(7);
        ficha.Musica.SeguirFueraDeLaFicha.Should().BeTrue("la ficha lee el ajuste guardado");
        ficha.Musica.MostrandoPanelMusica = true;
        Sonar(ficha);

        ficha.Dispose();
        ficha.Musica.AlOcultarLaFicha(); // lo que hace la vista al dejar de verse

        _fondo.HayMusica.Should().BeTrue();
        _fondo.Activa.Should().BeSameAs(ficha.Musica);
        _fondo.TituloAnime.Should().Be("Anime 7");
        ficha.Musica.EsDeFondo.Should().BeTrue();
        ficha.Musica.MostrandoPanelMusica.Should().BeFalse();
        Reproductor(ficha).EstaSonando.Should().BeTrue();
        Reproductor(ficha).Disposed.Should().BeFalse();
    }

    [Fact]
    public async Task ConLaOpcionActiva_SiNoSuenaNadaOEstaEnPausa_NoSeQuedaDeFondo()
    {
        _config.MusicaSigueFueraDeLaFicha = true;
        var sinMusica = await AbrirFichaAsync(7);
        sinMusica.Dispose();
        _fondo.HayMusica.Should().BeFalse();

        var enPausa = await AbrirFichaAsync(8);
        Sonar(enPausa);
        Sonar(enPausa); // pausa
        enPausa.Dispose();

        _fondo.HayMusica.Should().BeFalse("una canción en pausa no es música que siga sonando");
        Reproductor(enPausa).Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task DeFondo_SePuedeControlar_YPasaSolaAlSiguienteTema()
    {
        _config.MusicaSigueFueraDeLaFicha = true;
        var ficha = await AbrirFichaAsync(7);
        Sonar(ficha);
        ficha.Dispose();
        var reproductor = Reproductor(ficha);

        _fondo.Activa!.AlternarTemaActualCommand.Execute(null);
        reproductor.EstaSonando.Should().BeFalse();
        _fondo.HayMusica.Should().BeTrue("en pausa la barra sigue ahí para reanudar");

        _fondo.Activa!.AlternarTemaActualCommand.Execute(null);
        reproductor.DispararTerminado();

        _fondo.HayMusica.Should().BeTrue("cambiar de tema no pasa por 'nada cargado'");
        _fondo.Activa!.TemaActual!.Slug.Should().Be("ED1");
        reproductor.EstaSonando.Should().BeTrue();
    }

    [Fact]
    public async Task Detener_CortaLaMusicaYLiberaElReproductor()
    {
        _config.MusicaSigueFueraDeLaFicha = true;
        var ficha = await AbrirFichaAsync(7);
        Sonar(ficha);
        ficha.Dispose();

        _fondo.DetenerMusicaCommand.Execute(null);

        _fondo.HayMusica.Should().BeFalse();
        Reproductor(ficha).EstaSonando.Should().BeFalse();
        Reproductor(ficha).Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task AlVolverALaFichaDelMismoAnime_RecibeSuMusicaTalComoIba()
    {
        _config.MusicaSigueFueraDeLaFicha = true;
        var primera = await AbrirFichaAsync(7);
        Sonar(primera, 1);
        var musica = primera.Musica;
        var tema = musica.TemaActual;
        primera.Dispose();

        var segunda = await AbrirFichaAsync(7, esperarTemas: false);

        segunda.Musica.Should().BeSameAs(musica);
        segunda.Musica.TemaActual.Should().BeSameAs(tema);
        segunda.Musica.EsDeFondo.Should().BeFalse();
        segunda.Musica.Anime.Should().BeSameAs(segunda.AnimeSeleccionado);
        _fondo.HayMusica.Should().BeFalse("la música vuelve a ser de la ficha: sobra la barra");
        Reproductor(primera).EstaSonando.Should().BeTrue("no se corta ni vuelve a empezar");
        Reproductor(segunda).Llamadas.Should().BeEmpty("el reproductor de la ficha nueva no llega a usarse");

        // Y al salir otra vez sin la opción, se corta como siempre.
        segunda.Musica.SeguirFueraDeLaFicha = false;
        segunda.Dispose();
        _fondo.HayMusica.Should().BeFalse();
        Reproductor(primera).Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task IrALaFichaDesdeLaBarra_AbreSuVentanaDeMusicaAlLlegar()
    {
        _config.MusicaSigueFueraDeLaFicha = true;
        var primera = await AbrirFichaAsync(7);
        Sonar(primera);
        primera.Dispose();

        _fondo.IrALaFichaCommand.Execute(null); // manda el mensaje de navegación; aquí la ficha se abre a mano
        var segunda = await AbrirFichaAsync(7, esperarTemas: false);

        segunda.Musica.MostrandoPanelMusica.Should().BeTrue();
    }

    [Fact]
    public async Task EnLaFichaDeOtroAnime_LaMusicaDeFondoSigue_HastaQueEsaFichaReproduceLaSuya()
    {
        _config.MusicaSigueFueraDeLaFicha = true;
        var primera = await AbrirFichaAsync(7);
        Sonar(primera);
        primera.Dispose();

        var otra = await AbrirFichaAsync(8);
        otra.Musica.Should().NotBeSameAs(primera.Musica);
        _fondo.HayMusica.Should().BeTrue();
        Reproductor(primera).EstaSonando.Should().BeTrue();

        Sonar(otra);

        _fondo.HayMusica.Should().BeFalse("nunca suenan dos a la vez");
        Reproductor(primera).Disposed.Should().BeTrue();
        Reproductor(otra).EstaSonando.Should().BeTrue();
    }

    [Fact]
    public async Task SiElReproductorFalla_LaBarraDesaparece()
    {
        _config.MusicaSigueFueraDeLaFicha = true;
        var ficha = await AbrirFichaAsync(7);
        Sonar(ficha);
        ficha.Dispose();

        Reproductor(ficha).DispararFallo();

        _fondo.HayMusica.Should().BeFalse();
    }

    [Fact]
    public async Task ElInterruptorDeLaVentana_SeGuardaEnLosAjustes()
    {
        var ficha = await AbrirFichaAsync(7);

        ficha.Musica.AlternarSeguirFueraDeLaFichaCommand.Execute(null);

        var limite = DateTime.UtcNow.AddSeconds(3);
        while (!_config.MusicaSigueFueraDeLaFicha && DateTime.UtcNow < limite) await Task.Delay(10);
        _config.MusicaSigueFueraDeLaFicha.Should().BeTrue();
    }

    [Fact]
    public void Detener_SinMusicaDeFondo_NoHaceNada()
    {
        var act = () => _fondo.Detener();

        act.Should().NotThrow();
        _fondo.Recuperar(7).Should().BeNull();
    }
}
