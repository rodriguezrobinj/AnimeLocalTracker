using System.Collections.Generic;
using System.Linq;
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

/// <summary>
/// "Qué veo hoy": ahora abre un panel con ruleta y el usuario decide (Ver ahora / Elegir otro / Cerrar)
/// en vez de saltar directo al reproductor.
/// </summary>
public class GaleriaQueVeoHoyTests
{
    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<IFileScannerService> _scanner = new();
    private readonly Mock<IDialogService> _dialog = new();

    public sealed class Receptor : IRecipient<NavegarMensaje_Reproductor>
    {
        public NavegarMensaje_Reproductor? Recibido { get; private set; }
        public void Receive(NavegarMensaje_Reproductor message) => Recibido = message;
    }

    private static AnimeItem Anime(int id, string estado, string titulo = "") => new()
    {
        AniListId = id,
        Titulo = string.IsNullOrEmpty(titulo) ? $"Anime {id}" : titulo,
        EstadoUsuario = estado,
        RutaCarpeta = $"C:\\Anime\\{id}"
    };

    /// <summary>Configura el escáner y la BD: episodios 1..total en disco, vistos los indicados.</summary>
    private void Configurar(AnimeItem anime, int totalEpisodios, params int[] vistos)
    {
        var episodios = Enumerable.Range(1, totalEpisodios)
            .Select(n => new EpisodioItem { NumeroEpisodio = n, RutaCompleta = $"{anime.RutaCarpeta}\\ep{n:D2}.mkv" })
            .ToList();
        _scanner.Setup(s => s.EscanearEpisodiosAsync(anime.RutaCarpeta)).ReturnsAsync(episodios);
        _db.Setup(d => d.ObtenerRegistrosPorAnimeAsync(anime.AniListId))
           .ReturnsAsync(vistos.Select(n => new RegistroEpisodio { AniListId = anime.AniListId, NumeroEpisodio = n, VistoLocal = true }).ToList());
    }

    private async Task<GaleriaViewModel> CrearAsync(params AnimeItem[] animes)
    {
        _db.Setup(d => d.ObtenerTodosLosAnimesAsync()).ReturnsAsync(animes.ToList());
        _db.Setup(d => d.ObtenerTodosLosRegistrosAsync()).ReturnsAsync(new List<RegistroEpisodio>());

        var sut = new GaleriaViewModel(
            Mock.Of<IAnimeTrackingService>(), _db.Object, Mock.Of<IAuthService>(), _dialog.Object,
            Mock.Of<IHttpClientFactory>(), Mock.Of<IImageCacheService>(), _scanner.Object);
        await Task.Delay(100); // el constructor carga la biblioteca en segundo plano
        return sut;
    }

    [Fact]
    public async Task ElegirQueVerHoy_ConEpisodiosNoVistos_DeberiaGirarYNoNavegarHastaConfirmar()
    {
        var anime = Anime(50, "CURRENT", "One Piece");
        Configurar(anime, totalEpisodios: 3, vistos: 1);
        var sut = await CrearAsync(anime);

        var receptor = new Receptor();
        WeakReferenceMessenger.Default.Register<NavegarMensaje_Reproductor>(receptor, (r, m) => ((Receptor)r).Receive(m));
        try
        {
            await sut.ElegirQueVerHoyCommand.ExecuteAsync(null);

            // Antes saltaba directo al reproductor sin dejar opción; ahora primero gira la ruleta.
            sut.FaseActualQueVer.Should().Be(FaseQueVer.Girando);
            sut.QueVerElegido!.AniListId.Should().Be(50);
            sut.QueVerEpisodioNumero.Should().Be(2, "es el siguiente episodio no visto en orden cronológico");
            receptor.Recibido.Should().BeNull("no se navega hasta que el usuario pulse Ver ahora");

            sut.TerminarGiroQueVerCommand.Execute(null);
            sut.FaseActualQueVer.Should().Be(FaseQueVer.Resultado);
            receptor.Recibido.Should().BeNull();

            sut.VerElegidoQueVerCommand.Execute(null);

            receptor.Recibido.Should().NotBeNull();
            receptor.Recibido!.AnimeId.Should().Be(50);
            receptor.Recibido.TituloAnime.Should().Be("One Piece");
            receptor.Recibido.Episodio.Should().Be(2);
            receptor.Recibido.EpisodiosDisponibles.Should().HaveCount(3);
            sut.FaseActualQueVer.Should().Be(FaseQueVer.Oculto, "el panel se cierra al ir al reproductor");
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(receptor);
        }
    }

    [Fact]
    public async Task ElegirQueVerHoy_DeberiaArmarLaRuletaConElGanadorEnSuIndice()
    {
        var animes = Enumerable.Range(1, 12).Select(i => { var a = Anime(i, "CURRENT"); a.UrlPortada = $"https://img/{i}.jpg"; return a; }).ToArray();
        foreach (var a in animes) Configurar(a, 2);
        var sut = await CrearAsync(animes);

        await sut.ElegirQueVerHoyCommand.ExecuteAsync(null);

        sut.RuletaPortadasQueVer.Should().HaveCount(40);
        sut.RuletaPortadasQueVer[sut.RuletaIndiceGanadorQueVer].AniListId.Should().Be(sut.QueVerElegido!.AniListId);
    }

    [Fact]
    public async Task ElegirQueVerHoy_TodosVistos_DeberiaMostrarEstadoVacioSinDialogoNiNavegar()
    {
        var anime = Anime(50, "CURRENT");
        Configurar(anime, totalEpisodios: 1, vistos: 1);
        var sut = await CrearAsync(anime);

        await sut.ElegirQueVerHoyCommand.ExecuteAsync(null);

        sut.FaseActualQueVer.Should().Be(FaseQueVer.SinPendientes);
        sut.QueVerMensaje.Should().NotBeNullOrWhiteSpace();
        sut.EstaBuscandoQueVer.Should().BeFalse();
        _dialog.Verify(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ElegirQueVerHoy_SinCarpetas_DeberiaMostrarEstadoSinCarpeta()
    {
        var sut = await CrearAsync(new AnimeItem { AniListId = 50, Titulo = "One Piece", EstadoUsuario = "CURRENT" });

        await sut.ElegirQueVerHoyCommand.ExecuteAsync(null);

        sut.FaseActualQueVer.Should().Be(FaseQueVer.SinCarpeta);
        sut.EstaBuscandoQueVer.Should().BeFalse();
    }

    [Fact]
    public async Task ElegirQueVerHoy_DeberiaPreferirAnimesEnCurso()
    {
        var planeado = Anime(1, "PLANNING");
        var enCurso = Anime(2, "CURRENT");
        Configurar(planeado, 3);
        Configurar(enCurso, 3, vistos: 1);
        var sut = await CrearAsync(planeado, enCurso);

        await sut.ElegirQueVerHoyCommand.ExecuteAsync(null);

        sut.QueVerElegido!.AniListId.Should().Be(2);
    }

    [Theory]
    [InlineData("COMPLETED")]
    [InlineData("DROPPED")]
    public async Task ElegirQueVerHoy_NuncaDeberiaSugerirCompletadosNiAbandonados(string estado)
    {
        // Un anime completado en AniList cuyos episodios se vieron fuera de la app no tiene registros locales:
        // antes aparecía recomendado desde el episodio 1.
        var anime = Anime(1, estado);
        Configurar(anime, totalEpisodios: 12);
        var sut = await CrearAsync(anime);

        await sut.ElegirQueVerHoyCommand.ExecuteAsync(null);

        sut.FaseActualQueVer.Should().Be(FaseQueVer.SinPendientes);
        sut.QueVerElegido.Should().BeNull();
    }

    [Fact]
    public async Task ElegirQueVerHoy_SiLosEnCursoEstanAlDia_DeberiaCaerAPlaneados()
    {
        var alDia = Anime(1, "CURRENT");
        var planeado = Anime(2, "PLANNING");
        Configurar(alDia, 2, 1, 2);
        Configurar(planeado, totalEpisodios: 2);
        var sut = await CrearAsync(alDia, planeado);

        await sut.ElegirQueVerHoyCommand.ExecuteAsync(null);

        sut.QueVerElegido!.AniListId.Should().Be(2);
    }

    [Fact]
    public async Task ElegirQueVerHoy_EnElFallbackTambienDescartaEpisodiosSinNumero()
    {
        // FUN-004: el segundo bucle antiguo no filtraba NumeroEpisodio > 0.
        var planeado = Anime(1, "PLANNING");
        _scanner.Setup(s => s.EscanearEpisodiosAsync(planeado.RutaCarpeta))
                .ReturnsAsync(new List<EpisodioItem> { new() { NumeroEpisodio = 0, RutaCompleta = "C:\\Anime\\1\\especial.mkv" } });
        _db.Setup(d => d.ObtenerRegistrosPorAnimeAsync(1)).ReturnsAsync(new List<RegistroEpisodio>());
        var sut = await CrearAsync(planeado);

        await sut.ElegirQueVerHoyCommand.ExecuteAsync(null);

        sut.FaseActualQueVer.Should().Be(FaseQueVer.SinPendientes);
    }

    [Fact]
    public async Task OtroQueVerHoy_NoDeberiaRepetirLoYaMostrado()
    {
        var a = Anime(1, "CURRENT");
        var b = Anime(2, "CURRENT");
        Configurar(a, 2);
        Configurar(b, 2);
        var sut = await CrearAsync(a, b);

        await sut.ElegirQueVerHoyCommand.ExecuteAsync(null);
        sut.TerminarGiroQueVerCommand.Execute(null);
        int primero = sut.QueVerElegido!.AniListId;

        sut.OtroQueVerHoyCommand.CanExecute(null).Should().BeTrue();
        await sut.OtroQueVerHoyCommand.ExecuteAsync(null);
        sut.FaseActualQueVer.Should().Be(FaseQueVer.Girando);
        int segundo = sut.QueVerElegido!.AniListId;
        segundo.Should().NotBe(primero);

        // Con todo mostrado el ciclo se reinicia, pero nunca repite el que acaba de salir.
        sut.TerminarGiroQueVerCommand.Execute(null);
        await sut.OtroQueVerHoyCommand.ExecuteAsync(null);
        sut.QueVerElegido!.AniListId.Should().Be(primero);
    }

    [Fact]
    public async Task OtroQueVerHoy_ConUnSoloCandidato_DeberiaQuedarDeshabilitado()
    {
        var unico = Anime(1, "CURRENT");
        Configurar(unico, 2);
        var sut = await CrearAsync(unico);

        await sut.ElegirQueVerHoyCommand.ExecuteAsync(null);
        sut.TerminarGiroQueVerCommand.Execute(null);

        sut.HayOtraOpcionQueVer.Should().BeFalse();
        sut.OtroQueVerHoyCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task ElegirQueVerHoy_ConBibliotecaGrande_NoDeberiaEscanearTodasLasCarpetas()
    {
        // Antes se recorrían las carpetas de TODA la biblioteca (secuencialmente) en cada clic.
        var animes = Enumerable.Range(1, 40).Select(i => Anime(i, "CURRENT")).ToArray();
        foreach (var a in animes) Configurar(a, 2);
        var sut = await CrearAsync(animes);

        await sut.ElegirQueVerHoyCommand.ExecuteAsync(null);

        sut.FaseActualQueVer.Should().Be(FaseQueVer.Girando);
        _scanner.Invocations.Count(i => i.Method.Name == nameof(IFileScannerService.EscanearEpisodiosAsync))
            .Should().BeLessThanOrEqualTo(12, "basta con reunir unos 8 candidatos");
    }

    [Fact]
    public async Task CerrarQueVerHoy_DeberiaDejarElPanelOcultoYPermitirVolverAAbrir()
    {
        var anime = Anime(1, "CURRENT");
        Configurar(anime, 2);
        var sut = await CrearAsync(anime);

        await sut.ElegirQueVerHoyCommand.ExecuteAsync(null);
        sut.QueVerHoyAbierto.Should().BeTrue();
        sut.SePuedeAyudarAverQueVer.Should().BeFalse("mientras el panel está abierto el botón queda ocupado");

        sut.CerrarQueVerHoyCommand.Execute(null);

        sut.FaseActualQueVer.Should().Be(FaseQueVer.Oculto);
        sut.QueVerElegido.Should().BeNull();
        sut.ElegirQueVerHoyCommand.CanExecute(null).Should().BeTrue();

        await sut.ElegirQueVerHoyCommand.ExecuteAsync(null);
        sut.FaseActualQueVer.Should().Be(FaseQueVer.Girando);
    }

    [Fact]
    public async Task VerElegido_SoloDeberiaPoderUsarseCuandoTerminoElGiro()
    {
        var anime = Anime(1, "CURRENT");
        Configurar(anime, 2);
        var sut = await CrearAsync(anime);

        await sut.ElegirQueVerHoyCommand.ExecuteAsync(null);

        sut.VerElegidoQueVerCommand.CanExecute(null).Should().BeFalse("la ruleta todavía está girando");
        sut.TerminarGiroQueVerCommand.Execute(null);
        sut.VerElegidoQueVerCommand.CanExecute(null).Should().BeTrue();
    }
}
