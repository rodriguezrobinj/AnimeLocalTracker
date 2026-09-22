using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
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
/// Navegación rápida entre fichas de anime (varios clics seguidos): la ficha anterior, si se
/// abandona antes de terminar de cargar, debe cancelar sus tareas de fondo en vez de dejarlas
/// seguir golpeando disco/red por un anime que ya no está en pantalla.
/// </summary>
[Collection("NavigationServiceTests")]
public class NavigationServiceDetalleTests : IDisposable
{
    private readonly Mock<IServiceProvider> _spMock = new();
    private readonly Mock<IDatabaseService> _dbMock = new();
    private readonly Mock<IFileScannerService> _fileScannerMock = new();
    private readonly Mock<IDownloadService> _downloadMock = new();
    private readonly NavigationService _navigationService;

    public NavigationServiceDetalleTests()
    {
        _dbMock.Setup(d => d.ObtenerRegistrosPorAnimeAsync(It.IsAny<int>())).ReturnsAsync(new List<RegistroEpisodio>());
        _fileScannerMock.Setup(f => f.EscanearEpisodiosAsync(It.IsAny<string>())).ReturnsAsync(new List<EpisodioItem>());
        double p = 0;
        _downloadMock.Setup(d => d.EstaDescargando(It.IsAny<int>(), It.IsAny<int>(), out p)).Returns(false);

        // Cada GetService(typeof(DetalleViewModel)) debe devolver una instancia NUEVA (transient real).
        _spMock.Setup(sp => sp.GetService(typeof(DetalleViewModel))).Returns(() => new DetalleViewModel(
            Mock.Of<IAnimeTrackingService>(), _dbMock.Object, Mock.Of<IAuthService>(),
            _fileScannerMock.Object, Mock.Of<IDialogService>(), _downloadMock.Object));

        _navigationService = new NavigationService(_spMock.Object);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        WeakReferenceMessenger.Default.UnregisterAll(_navigationService);
    }

    [Fact]
    public async Task NavegarADetalle_DosVecesSeguidas_CancelaLasCargasDeFondoDeLaFichaAnterior()
    {
        var animeA = new AnimeItem { AniListId = 1, Titulo = "Anime A" };
        var animeB = new AnimeItem { AniListId = 2, Titulo = "Anime B" };

        await _navigationService.InicializarDetalleAsync(new NavegarMensaje_Detalle(animeA));
        var detalleA = (DetalleViewModel)_navigationService.VistaActual;
        var campo = typeof(DetalleViewModel).GetField("_ctsCargaFicha", BindingFlags.NonPublic | BindingFlags.Instance);
        var ctsA = (CancellationTokenSource)campo!.GetValue(detalleA)!;
        ctsA.IsCancellationRequested.Should().BeFalse();

        // Act: el usuario navega a otro anime antes de que la ficha A haya terminado de asentarse.
        await _navigationService.InicializarDetalleAsync(new NavegarMensaje_Detalle(animeB));

        // Assert: la ficha A se descartó y sus cargas de fondo se cancelaron; la vista muestra B.
        ctsA.IsCancellationRequested.Should().BeTrue("la ficha anterior se descarta al navegar a otro anime");
        _navigationService.VistaActual.Should().BeOfType<DetalleViewModel>()
            .Which.AnimeSeleccionado.Should().Be(animeB);
    }
}
