using System;
using System.Collections.Generic;
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
/// Fase 4 de la investigación de la ficha (docs/investigacion-ficha-y-musica.md): el botón principal de la lista ("lo
/// siguiente") y la casilla para ir a un episodio en series largas.
/// </summary>
public class DetalleInterfazTests
{
    private const int Id = 21;

    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<IFileScannerService> _escaner = new();
    private readonly Mock<IDialogService> _dialogos = new();
    private readonly Mock<IDownloadService> _descargas = new();

    private async Task<DetalleViewModel> AbrirFichaAsync(int total, int[] enDisco, int[] vistos, (int Ep, double Progreso, DateTime Fecha)[]? aMedias = null)
    {
        var anime = new AnimeItem { AniListId = Id, Titulo = "Frieren", TotalEpisodios = total, RutaCarpeta = @"C:\Anime\Frieren" };
        _escaner.Setup(e => e.EscanearEpisodiosAsync(anime.RutaCarpeta))
            .ReturnsAsync(enDisco.Select(n => new EpisodioItem { NumeroEpisodio = n, RutaCompleta = $@"C:\Anime\Frieren\Episodio {n}.mp4" }).ToList());

        var registros = vistos.Select(n => new RegistroEpisodio { AniListId = Id, NumeroEpisodio = n, VistoLocal = true }).ToList();
        foreach (var (ep, progreso, fecha) in aMedias ?? [])
            registros.Add(new RegistroEpisodio { AniListId = Id, NumeroEpisodio = ep, ProgresoSegundos = progreso, TotalSegundos = 1400, UltimaReproduccion = fecha });
        _db.Setup(d => d.ObtenerRegistrosPorAnimeAsync(Id)).ReturnsAsync(registros);

        double p = 0;
        _descargas.Setup(d => d.EstaDescargando(It.IsAny<int>(), It.IsAny<int>(), out p)).Returns(false);

        var sut = new DetalleViewModel(Mock.Of<IAnimeTrackingService>(), _db.Object, Mock.Of<IAuthService>(), _escaner.Object, _dialogos.Object, _descargas.Object);
        await sut.InicializarAsync(anime);
        return sut;
    }

    // ── Botón principal ──

    [Fact]
    public async Task AccionPrincipal_ConUnEpisodioAMedias_OfreceReanudarElMasReciente()
    {
        var sut = await AbrirFichaAsync(total: 6, enDisco: [2, 4, 5], vistos: [1],
            aMedias: [(2, 300, new DateTime(2026, 9, 1)), (4, 500, new DateTime(2026, 9, 20))]);

        sut.Episodios.TieneAccionPrincipal.Should().BeTrue();
        sut.Episodios.AccionPrincipalTexto.Should().Be(string.Format(LocalizationService.T("Det_AccionReanudarFormato"), 4));
        sut.Episodios.AccionPrincipalIcono.Should().Be("Play");
    }

    [Fact]
    public async Task AccionPrincipal_SinNadaAMedias_OfreceVerElPrimeroSinVer()
    {
        var sut = await AbrirFichaAsync(total: 5, enDisco: [3, 4], vistos: [1, 2]);

        sut.Episodios.AccionPrincipalTexto.Should().Be(string.Format(LocalizationService.T("Det_AccionVerFormato"), 3));
        sut.Episodios.AccionPrincipalIcono.Should().Be("Play");
    }

    [Fact]
    public async Task AccionPrincipal_SiElSiguienteNoEstaEnDisco_OfreceDescargarloYLoDescarga()
    {
        var sut = await AbrirFichaAsync(total: 5, enDisco: [], vistos: [1, 2]);
        sut.Episodios.AccionPrincipalTexto.Should().Be(string.Format(LocalizationService.T("Det_AccionDescargarFormato"), 3));
        sut.Episodios.AccionPrincipalIcono.Should().Be("DownloadOutline");

        await sut.Episodios.EjecutarAccionPrincipalCommand.ExecuteAsync(null);

        _descargas.Verify(d => d.IniciarDescargaEpisodioAsync(Id, It.IsAny<string>(), It.IsAny<string>(), 3, It.IsAny<IEnumerable<string>?>()), Times.Once);
        sut.Episodios.TieneAccionPrincipal.Should().BeFalse("ese episodio ya se está descargando: no se ofrece otra vez");
    }

    [Fact]
    public async Task AccionPrincipal_ConTodoVisto_NoOfreceNada()
    {
        var sut = await AbrirFichaAsync(total: 3, enDisco: [3], vistos: [1, 2, 3]);

        sut.Episodios.TieneAccionPrincipal.Should().BeFalse();
        sut.Episodios.AccionPrincipalTexto.Should().BeEmpty();
    }

    [Fact]
    public async Task AccionPrincipal_AlMarcarVistoElSiguiente_PasaAlQueLeSigue()
    {
        var sut = await AbrirFichaAsync(total: 4, enDisco: [2, 3], vistos: [1]);

        await sut.Episodios.AlternarVistoEpisodioCommand.ExecuteAsync(sut.Episodios.Todos.First(e => e.NumeroEpisodio == 2));

        sut.Episodios.AccionPrincipalTexto.Should().Be(string.Format(LocalizationService.T("Det_AccionVerFormato"), 3));
    }

    // ── Ir a un episodio ──

    [Theory]
    [InlineData(12, false)]
    [InlineData(31, true)]
    public async Task IrAEpisodio_SoloSeOfreceEnSeriesLargas(int total, bool esperado)
    {
        var sut = await AbrirFichaAsync(total: total, enDisco: [], vistos: []);

        sut.Episodios.MostrarIrAEpisodio.Should().Be(esperado);
    }

    [Fact]
    public async Task BuscarParaIr_DevuelveElEpisodioDeLaLista()
    {
        var sut = await AbrirFichaAsync(total: 40, enDisco: [], vistos: []);

        sut.Episodios.BuscarParaIr(" 27 ")!.NumeroEpisodio.Should().Be(27);
        _dialogos.Verify(d => d.MostrarToast(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    public async Task BuscarParaIr_TextoQueNoEsUnEpisodio_NoHaceNada(string texto)
    {
        var sut = await AbrirFichaAsync(total: 40, enDisco: [], vistos: []);

        sut.Episodios.BuscarParaIr(texto).Should().BeNull();
        _dialogos.Verify(d => d.MostrarToast(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task BuscarParaIr_EpisodioOcultoPorElFiltro_LoAvisa()
    {
        var sut = await AbrirFichaAsync(total: 40, enDisco: [], vistos: [5]);
        sut.Episodios.FiltroEpisodios = EpisodiosOrganizador.FiltroNoVistos;

        sut.Episodios.BuscarParaIr("5").Should().BeNull();

        _dialogos.Verify(d => d.MostrarToast(LocalizationService.T("Det_IrAEpisodio"), It.Is<string>(m => m.Contains('5')), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }
}
