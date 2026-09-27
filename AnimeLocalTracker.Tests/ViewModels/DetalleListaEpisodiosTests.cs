using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>
/// La lista de capítulos de la Ficha debe reflejar los episodios emitidos oficialmente (según AniList), no el número que
/// tenga por nombre un archivo local: un video mal nombrado como "Episodio 3000.mp4" en una carpeta de un anime de 12
/// episodios no debe agrandar la lista a 3000 filas — pero un episodio real solo un poco por delante (un preestreno o
/// una filtración de 1-2 días) sí debe verse, sin esperar a que AniList lo cuente como emitido. Ver
/// <see cref="AnimeLocalTracker.Core.EpisodiosOrganizadorTests"/> para la lógica pura.
/// </summary>
public class DetalleListaEpisodiosTests
{
    private readonly Mock<IAnimeTrackingService> _tracking = new();
    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<IAuthService> _auth = new();
    private readonly Mock<IFileScannerService> _escaner = new();
    private readonly Mock<IDialogService> _dialogos = new();
    private readonly Mock<IDownloadService> _descargas = new();

    private DetalleViewModel CrearSut() => new(_tracking.Object, _db.Object, _auth.Object, _escaner.Object, _dialogos.Object, _descargas.Object);

    private async Task<DetalleViewModel> AbrirFichaAsync(int totalEpisodios, List<EpisodioItem> encontrados, int episodiosVistos = 0)
    {
        var anime = new AnimeItem { AniListId = 7, Titulo = "Anime de prueba", RutaCarpeta = "C:\\Anime\\Prueba", TotalEpisodios = totalEpisodios };
        var registros = Enumerable.Range(1, episodiosVistos).Select(n => new RegistroEpisodio { AniListId = 7, NumeroEpisodio = n, VistoLocal = true }).ToList();

        _escaner.Setup(e => e.EscanearEpisodiosAsync(anime.RutaCarpeta)).ReturnsAsync(encontrados);
        _db.Setup(d => d.ObtenerRegistrosPorAnimeAsync(7)).ReturnsAsync(registros);

        var sut = CrearSut();
        await sut.InicializarAsync(anime);
        return sut;
    }

    private static EpisodioItem Archivo(int numero) => new() { NumeroEpisodio = numero, RutaCompleta = $"C:\\Anime\\Prueba\\Episodio {numero}.mp4" };

    [Fact]
    public async Task UnArchivoLocalConNumeroMuyPorEncimaDelOficial_NoAgrandaLaLista()
    {
        var sut = await AbrirFichaAsync(totalEpisodios: 12, encontrados: [Archivo(1), Archivo(2), Archivo(3000)]);

        sut.EpisodiosDelAnime.Should().HaveCount(12);
        sut.EpisodiosDelAnime.Should().NotContain(e => e.NumeroEpisodio == 3000);
    }

    [Fact]
    public async Task ElArchivoConNumeroAbsurdo_SigueDescargadoEnDiscoPeroSinFilaPropia()
    {
        // El archivo no desaparece del equipo del usuario: solo no se le dedica una fila que no tiene sentido en la lista oficial.
        var sut = await AbrirFichaAsync(totalEpisodios: 12, encontrados: [Archivo(1), Archivo(4000)]);

        sut.EpisodiosDelAnime.Should().ContainSingle(e => e.NumeroEpisodio == 1 && e.Descargado);
        sut.EpisodiosDelAnime.Should().HaveCount(12);
    }

    [Fact]
    public async Task ArchivosDentroDelRangoOficial_SeMuestranNormalmente()
    {
        var sut = await AbrirFichaAsync(totalEpisodios: 12, encontrados: [Archivo(1), Archivo(5), Archivo(12)]);

        sut.EpisodiosDelAnime.Where(e => e.Descargado).Select(e => e.NumeroEpisodio).Should().BeEquivalentTo([1, 5, 12]);
    }

    [Fact]
    public async Task SinTotalOficialTodavia_ConfiaEnLosArchivosLocalesComoAntes()
    {
        // Anime recién añadido, sin sincronizar aún con AniList: no hay dato oficial con el que comparar.
        var sut = await AbrirFichaAsync(totalEpisodios: 0, encontrados: [Archivo(1), Archivo(2), Archivo(12)]);

        sut.EpisodiosDelAnime.Should().HaveCount(12);
    }

    [Fact]
    public async Task LoVistoPorEncimaDelTotalOficial_SigueMostrandoseAunqueElOficialEsteDesactualizado()
    {
        var sut = await AbrirFichaAsync(totalEpisodios: 12, encontrados: [], episodiosVistos: 13);

        sut.EpisodiosDelAnime.Should().HaveCount(13);
    }

    [Fact]
    public async Task VariosArchivosConNumerosAbsurdos_NoDisparanElLimiteDeSeguridadNiTardan()
    {
        var sut = await AbrirFichaAsync(totalEpisodios: 12, encontrados: [Archivo(3000), Archivo(50000), Archivo(999999)]);

        sut.EpisodiosDelAnime.Should().HaveCount(12);
    }

    [Fact]
    public async Task UnEpisodioFiltradoUnPocoAntesDeLaEmisionOficial_SiApareceEnLaLista()
    {
        // El episodio 13 ya está descargado (preestreno/filtración) aunque AniList todavía marque 12 como el último emitido.
        var sut = await AbrirFichaAsync(totalEpisodios: 12, encontrados: [Archivo(1), Archivo(13)]);

        sut.EpisodiosDelAnime.Should().Contain(e => e.NumeroEpisodio == 13 && e.Descargado);
        sut.EpisodiosDelAnime.Should().HaveCount(13);
    }
}
