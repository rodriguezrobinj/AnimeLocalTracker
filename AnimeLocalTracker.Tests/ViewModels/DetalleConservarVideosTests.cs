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

    private async Task<DetalleViewModel> FichaAsync(bool conservarVideos, bool? enLaBaseDeDatos = null)
    {
        var anime = new AnimeItem { AniListId = 7, Titulo = "Frieren", RutaCarpeta = _carpeta, Estado = "FINISHED", TotalEpisodios = 1, ConservarVideos = conservarVideos };
        _escaner.Setup(e => e.EscanearEpisodiosAsync(_carpeta)).ReturnsAsync(new List<EpisodioItem>());
        _db.Setup(d => d.ObtenerRegistrosPorAnimeAsync(7)).ReturnsAsync(new List<RegistroEpisodio>());
        _db.Setup(d => d.ObtenerConservarVideosAsync(7)).ReturnsAsync(enLaBaseDeDatos ?? conservarVideos);
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
    public async Task AlAbrirLaFicha_ManditaLaBaseDeDatos_NoUnaCopiaVieja()
    {
        // La ficha puede abrirse con un AnimeItem que la Galería leyó antes de activar la protección.
        var sut = await FichaAsync(conservarVideos: false, enLaBaseDeDatos: true);

        sut.ConservarVideosAnime.Should().BeTrue();
        sut.AnimeSeleccionado!.ConservarVideos.Should().BeTrue("la copia en memoria se pone al día");
    }

    [Fact]
    public async Task AlPulsar_CambiaElInterruptor_YGuardaSoloEsaMarca()
    {
        var sut = await FichaAsync(conservarVideos: false);
        var guardados = new List<(int Id, bool Conservar)>();
        _db.Setup(d => d.GuardarConservarVideosAsync(It.IsAny<int>(), It.IsAny<bool>()))
            .Callback<int, bool>((id, valor) => guardados.Add((id, valor))).Returns(Task.CompletedTask);

        await sut.AlternarConservarVideosCommand.ExecuteAsync(null);
        sut.ConservarVideosAnime.Should().BeTrue();

        await sut.AlternarConservarVideosCommand.ExecuteAsync(null);
        sut.ConservarVideosAnime.Should().BeFalse();

        guardados.Should().Equal([(7, true), (7, false)], "cada pulsación guarda el valor nuevo");
        _db.Verify(d => d.ActualizarAnimeAsync(It.IsAny<AnimeItem>()), Times.Never);
    }

    [Fact]
    public async Task SiElGuardadoFalla_VuelveAComoEstabaYAvisa()
    {
        var sut = await FichaAsync(conservarVideos: false);
        _db.Setup(d => d.GuardarConservarVideosAsync(It.IsAny<int>(), It.IsAny<bool>())).ThrowsAsync(new IOException("disco lleno"));

        await sut.AlternarConservarVideosCommand.ExecuteAsync(null);

        sut.ConservarVideosAnime.Should().BeFalse("no se pudo guardar: mostrar 'protegido' sería mentir");
        sut.AnimeSeleccionado!.ConservarVideos.Should().BeFalse();
        _dialogos.Verify(d => d.MostrarDialogoAsync(LocalizationService.T("Det_ConservarVideos"), LocalizationService.T("Det_ConservarVideosErrorMsj"),
            false, It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task SiElGuardadoFalla_ConOtroAnimeYaSeleccionado_RevierteElAnimeCorrecto()
    {
        var sut = await FichaAsync(conservarVideos: false);
        var original = sut.AnimeSeleccionado!;
        var otro = new AnimeItem { AniListId = 8, Titulo = "Otro", ConservarVideos = true };
        _db.Setup(d => d.GuardarConservarVideosAsync(7, true)).Returns(async () =>
        {
            sut.AnimeSeleccionado = otro; // la ficha pasó a otro anime mientras se guardaba
            await Task.Yield();
            throw new IOException("disco lleno");
        });

        await sut.AlternarConservarVideosCommand.ExecuteAsync(null);

        original.ConservarVideos.Should().BeFalse("se revierte el anime sobre el que se pulsó");
        otro.ConservarVideos.Should().BeTrue("el otro anime no se toca");
    }
}
