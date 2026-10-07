using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>Ficha: el interruptor "Conservar los videos" se carga, se guarda y no miente si el guardado falla.</summary>
public class DetalleConservarVideosTests : IDisposable
{
    private readonly Mock<IAnimeTrackingService> _tracking = new();
    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<IFileScannerService> _escaner = new();
    private readonly Mock<IDialogService> _dialogos = new();
    private readonly Mock<IDownloadService> _descargas = new();
    private readonly Mock<IEmisionMonitorService> _monitor = new();
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), $"ficha_conservar_{Guid.NewGuid():N}");

    public DetalleConservarVideosTests() => Directory.CreateDirectory(_carpeta);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(_carpeta, recursive: true); } catch { /* ignore */ }
    }

    private async Task<DetalleViewModel> FichaAsync(bool conservarVideos)
    {
        var anime = new AnimeItem { AniListId = 7, Titulo = "Frieren", RutaCarpeta = _carpeta, Estado = "FINISHED", TotalEpisodios = 1, ConservarVideos = conservarVideos };
        _escaner.Setup(e => e.EscanearEpisodiosAsync(_carpeta)).ReturnsAsync(new List<EpisodioItem>());
        _db.Setup(d => d.ObtenerRegistrosPorAnimeAsync(7)).ReturnsAsync(new List<RegistroEpisodio>());
        double p = 0;
        _descargas.Setup(d => d.EstaDescargando(It.IsAny<int>(), It.IsAny<int>(), out p)).Returns(false);

        var sut = new DetalleViewModel(_tracking.Object, _db.Object, Mock.Of<IAuthService>(), _escaner.Object, _dialogos.Object, _descargas.Object,
            monitorEmision: _monitor.Object);
        await sut.InicializarAsync(anime);
        return sut;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AlAbrirLaFicha_ElInterruptorRefleja_LoGuardado(bool guardado)
    {
        var sut = await FichaAsync(guardado);

        sut.ConservarVideosAnime.Should().Be(guardado);
    }

    [Fact]
    public async Task AlPulsar_CambiaElInterruptor_YGuardaElAnime()
    {
        var sut = await FichaAsync(conservarVideos: false);
        // Se anota el valor en el momento de cada guardado: es el mismo objeto AnimeItem y después cambia.
        var guardados = new List<(int Id, bool Conservar)>();
        _db.Setup(d => d.ActualizarAnimeAsync(It.IsAny<AnimeItem>())).Callback<AnimeItem>(a => guardados.Add((a.AniListId, a.ConservarVideos))).Returns(Task.CompletedTask);

        await sut.AlternarConservarVideosCommand.ExecuteAsync(null);
        sut.ConservarVideosAnime.Should().BeTrue();

        await sut.AlternarConservarVideosCommand.ExecuteAsync(null);
        sut.ConservarVideosAnime.Should().BeFalse();

        guardados.Should().Equal([(7, true), (7, false)], "cada pulsación guarda el valor nuevo");
    }

    [Fact]
    public async Task SiElGuardadoFalla_VuelveAComoEstabaYAvisa()
    {
        var sut = await FichaAsync(conservarVideos: false);
        _db.Setup(d => d.ActualizarAnimeAsync(It.IsAny<AnimeItem>())).ThrowsAsync(new IOException("disco lleno"));

        await sut.AlternarConservarVideosCommand.ExecuteAsync(null);

        sut.ConservarVideosAnime.Should().BeFalse("no se pudo guardar: mostrar 'protegido' sería mentir");
        sut.AnimeSeleccionado!.ConservarVideos.Should().BeFalse();
        _dialogos.Verify(d => d.MostrarDialogoAsync(LocalizationService.T("Det_ConservarVideos"), LocalizationService.T("Det_ConservarVideosErrorMsj"),
            false, It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }
}
