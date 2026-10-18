using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Recomendaciones;
using AnimeLocalTracker.ViewModels;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>Ficha → "Parecidos en tu biblioteca".</summary>
public class DetalleParecidosTests
{
    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<IFileScannerService> _escaner = new();
    private readonly Mock<IParecidosService> _parecidos = new();
    private static readonly AnimeItem Erased = new() { AniListId = 21234, Titulo = "Erased" };
    private static readonly string[] TemasViajeEnElTiempo = { "Time Manipulation", "Thriller" };
    private static readonly string[] SoloThriller = { "Thriller" };

    private DetalleViewModel CrearSut()
    {
        _escaner.Setup(e => e.EscanearEpisodiosAsync(It.IsAny<string>())).ReturnsAsync(new List<EpisodioItem>());
        _db.Setup(d => d.ObtenerRegistrosPorAnimeAsync(It.IsAny<int>())).ReturnsAsync(new List<RegistroEpisodio>());
        return new DetalleViewModel(Mock.Of<IAnimeTrackingService>(), _db.Object, Mock.Of<IAuthService>(), _escaner.Object,
            Mock.Of<IDialogService>(), Mock.Of<IDownloadService>(), parecidos: _parecidos.Object);
    }

    private static AnimeItem Anime(int id) => new() { AniListId = id, Titulo = $"Anime {id}", RutaCarpeta = @"C:\NoExiste" };

    [Fact]
    public async Task AlAbrir_MuestraLosParecidos()
    {
        _parecidos.Setup(p => p.BuscarAsync(9253, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new Parecido(Erased, 0.8, TemasViajeEnElTiempo, 3) });
        var sut = CrearSut();

        await sut.InicializarAsync(Anime(9253));
        await sut.CargaParecidos;

        sut.Parecidos.Should().ContainSingle().Which.Anime.Should().BeSameAs(Erased);
        sut.HayParecidos.Should().BeTrue();
        sut.Parecidos[0].TextoTemas.Should().Be("Time Manipulation · Thriller");
        sut.Parecidos[0].ToString().Should().Be("Erased", "el lector de pantalla anuncia la fila con ToString()");
        sut.Parecidos[0].TextoEnDisco.Should().Be(string.Format(LocalizationService.T("Det_ParecidosEnDiscoFormato"), 3));
    }

    [Fact]
    public async Task SinParecidos_LaSeccionNoAparece()
    {
        _parecidos.Setup(p => p.BuscarAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<Parecido>());
        var sut = CrearSut();

        await sut.InicializarAsync(Anime(9253));
        await sut.CargaParecidos;

        sut.HayParecidos.Should().BeFalse();
    }

    [Fact]
    public async Task AlAbrirOtraFicha_NoQuedanLosParecidosDeLaAnterior()
    {
        _parecidos.Setup(p => p.BuscarAsync(9253, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new Parecido(Erased, 0.8, SoloThriller, 0) });
        _parecidos.Setup(p => p.BuscarAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<Parecido>());
        var sut = CrearSut();
        await sut.InicializarAsync(Anime(9253));
        await sut.CargaParecidos;

        await sut.InicializarAsync(Anime(1));
        await sut.CargaParecidos;

        sut.Parecidos.Should().BeEmpty();
        sut.HayParecidos.Should().BeFalse();
    }

    [Fact]
    public void AbrirParecido_NavegaASuFicha()
    {
        var recibidos = new List<NavegarMensaje_Detalle>();
        var receptor = new object();
        WeakReferenceMessenger.Default.Register<object, NavegarMensaje_Detalle>(receptor, (_, m) => { lock (recibidos) recibidos.Add(m); });
        try
        {
            CrearSut().AbrirParecidoCommand.Execute(new Parecido(Erased, 0.8, Array.Empty<string>(), 0));

            recibidos.Should().ContainSingle(m => ReferenceEquals(m.AnimeSeleccionado, Erased));
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(receptor);
        }
    }
}
