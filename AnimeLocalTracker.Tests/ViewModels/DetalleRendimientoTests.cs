using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Core;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>
/// Fase 2 de la investigación de la ficha (docs/investigacion-ficha-y-musica.md): la lista de episodios solo se toca cuando
/// de verdad cambia, y la ficha ya no consulta a AniList al abrirse.
/// </summary>
public class DetalleRendimientoTests
{
    private readonly Mock<IAnimeTrackingService> _tracking = new();
    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<IFileScannerService> _escaner = new();
    private readonly Mock<IDownloadService> _descargas = new();

    private async Task<DetalleViewModel> AbrirFichaAsync(int total, int[] vistos)
    {
        var anime = new AnimeItem { AniListId = 21, Titulo = "One Piece", TotalEpisodios = total, RutaCarpeta = @"C:\Anime\OP" };
        _escaner.Setup(e => e.EscanearEpisodiosAsync(anime.RutaCarpeta)).ReturnsAsync(new List<EpisodioItem>());
        _db.Setup(d => d.ObtenerRegistrosPorAnimeAsync(21))
            .ReturnsAsync(vistos.Select(n => new RegistroEpisodio { AniListId = 21, NumeroEpisodio = n, VistoLocal = true }).ToList());
        double p = 0;
        _descargas.Setup(d => d.EstaDescargando(It.IsAny<int>(), It.IsAny<int>(), out p)).Returns(false);

        var sut = new DetalleViewModel(_tracking.Object, _db.Object, Mock.Of<IAuthService>(), _escaner.Object, Mock.Of<IDialogService>(), _descargas.Object);
        await sut.InicializarAsync(anime);
        return sut;
    }

    private static List<NotifyCollectionChangedAction> Vigilar<T>(ColeccionReemplazable<T> coleccion)
    {
        var avisos = new List<NotifyCollectionChangedAction>();
        coleccion.CollectionChanged += (_, e) => avisos.Add(e.Action);
        return avisos;
    }

    // ── ColeccionReemplazable ──

    [Fact]
    public void ReemplazarSiCambia_MismoContenido_NoAvisaNiToca()
    {
        var coleccion = new ColeccionReemplazable<string> { "a", "b", "c" };
        var avisos = Vigilar(coleccion);

        coleccion.ReemplazarSiCambia("a,b,c".Split(',')).Should().BeFalse();

        avisos.Should().BeEmpty();
    }

    [Theory]
    [InlineData("c,b,a")]   // otro orden
    [InlineData("a,b")]     // menos elementos
    [InlineData("a,b,c,d")] // más elementos
    [InlineData("")]        // vacía
    public void ReemplazarSiCambia_ContenidoDistinto_SustituyeConUnSoloAviso(string nuevos)
    {
        var coleccion = new ColeccionReemplazable<string> { "a", "b", "c" };
        var avisos = Vigilar(coleccion);
        var esperado = nuevos.Length == 0 ? [] : nuevos.Split(',');

        coleccion.ReemplazarSiCambia(esperado).Should().BeTrue();

        coleccion.Should().Equal(esperado);
        avisos.Should().Equal(NotifyCollectionChangedAction.Reset);
    }

    // ── La lista de la ficha ──

    [Fact]
    public async Task AbrirFicha_LlenaLaListaConUnSoloAviso()
    {
        // Antes: vaciar + un aviso por fila (1.180 en One Piece).
        var anime = new AnimeItem { AniListId = 21, Titulo = "One Piece", TotalEpisodios = 300 };
        _db.Setup(d => d.ObtenerRegistrosPorAnimeAsync(21)).ReturnsAsync(new List<RegistroEpisodio>());
        double p = 0;
        _descargas.Setup(d => d.EstaDescargando(It.IsAny<int>(), It.IsAny<int>(), out p)).Returns(false);
        var sut = new DetalleViewModel(_tracking.Object, _db.Object, Mock.Of<IAuthService>(), _escaner.Object, Mock.Of<IDialogService>(), _descargas.Object);
        var avisos = Vigilar(sut.Episodios.EpisodiosDelAnime);

        await sut.InicializarAsync(anime);

        sut.Episodios.EpisodiosDelAnime.Should().HaveCount(300);
        // Uno al vaciar la lista de la ficha anterior y otro al llenarla; ningún aviso fila a fila.
        avisos.Should().Equal(NotifyCollectionChangedAction.Reset, NotifyCollectionChangedAction.Reset);
    }

    [Fact]
    public async Task MarcarVisto_ConFiltroTodos_NoRehaceLaLista()
    {
        // La fila ya se actualiza sola; rehacer la lista hacía perder la selección y el punto por donde ibas.
        var sut = await AbrirFichaAsync(total: 5, vistos: []);
        var avisos = Vigilar(sut.Episodios.EpisodiosDelAnime);

        await sut.Episodios.AlternarVistoEpisodioCommand.ExecuteAsync(sut.Episodios.EpisodiosDelAnime[2]);

        avisos.Should().BeEmpty();
        sut.Episodios.EpisodiosDelAnime[2].Visto.Should().BeTrue();
    }

    [Fact]
    public async Task MarcarVisto_ConFiltroNoVistos_SiCambiaLaLista()
    {
        var sut = await AbrirFichaAsync(total: 5, vistos: []);
        sut.Episodios.FiltroEpisodios = EpisodiosOrganizador.FiltroNoVistos;
        var avisos = Vigilar(sut.Episodios.EpisodiosDelAnime);

        await sut.Episodios.AlternarVistoEpisodioCommand.ExecuteAsync(sut.Episodios.EpisodiosDelAnime.First(e => e.NumeroEpisodio == 3));

        avisos.Should().Equal(NotifyCollectionChangedAction.Reset);
        sut.Episodios.EpisodiosDelAnime.Select(e => e.NumeroEpisodio).Should().BeEquivalentTo([1, 2, 4, 5]);
    }

    [Fact]
    public async Task CambiarElOrden_SustituyeLaListaDeUnaVez()
    {
        var sut = await AbrirFichaAsync(total: 4, vistos: []);
        var avisos = Vigilar(sut.Episodios.EpisodiosDelAnime);

        sut.Episodios.OrdenAscendente = true;

        avisos.Should().Equal(NotifyCollectionChangedAction.Reset);
        sut.Episodios.EpisodiosDelAnime.Select(e => e.NumeroEpisodio).Should().Equal(1, 2, 3, 4);
    }

    // ── Red al abrir ──

    [Fact]
    public async Task AbrirFicha_NoConsultaElAnimeAAniList()
    {
        // Antes hacía una petición por apertura para un aviso ("próximos episodios") que la cuenta atrás, con su dato
        // guardado, ya dejaba sin efecto.
        await AbrirFichaAsync(total: 3, vistos: []);

        _tracking.Verify(t => t.ObtenerAnimePorIdAsync(It.IsAny<int>()), Times.Never);
    }
}
