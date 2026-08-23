using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

public class CalendarioViewModelTests
{
    private static async Task EsperarCargaInicialAsync(CalendarioViewModel vm, int timeoutMs = 5000)
    {
        var inicio = DateTime.UtcNow;
        while (vm.EstaCargando && DateTime.UtcNow - inicio < TimeSpan.FromMilliseconds(timeoutMs))
        {
            await Task.Delay(25);
        }
    }

    [Fact]
    public async Task CargaInicial_ConAniListIdsDuplicados_NoDeberiaQuedarseCargando()
    {
        // Arrange: BD con AniListId duplicado (antes ToDictionary lanzaba y el spinner quedaba pegado)
        var dbMock = new Mock<IDatabaseService>();
        dbMock.Setup(d => d.ObtenerTodosLosAnimesAsync())
            .ReturnsAsync(new List<AnimeItem>
            {
                new() { AniListId = 21, Estado = "RELEASING", UrlPortada = "onepiece.png" },
                new() { AniListId = 21, Estado = "RELEASING", UrlPortada = "onepiece.png" },
                new() { AniListId = 154587, Estado = "FINISHED", UrlPortada = "frieren.png" }
            });

        var trackingMock = new Mock<IAnimeTrackingService>();
        trackingMock
            .Setup(t => t.ObtenerCalendarioEmisionAsync(It.IsAny<List<int>>(), It.IsAny<long>(), It.IsAny<long>()))
            .ReturnsAsync(new List<AiringEpisode>());

        // Act
        var vm = new CalendarioViewModel(dbMock.Object, trackingMock.Object);
        await EsperarCargaInicialAsync(vm);

        // Assert
        vm.EstaCargando.Should().BeFalse("el finally debe resetear el spinner incluso si algo falla");
        vm.TotalAnimesEnEmision.Should().Be(2);
        vm.EstaVacio.Should().BeTrue("el servicio no devolvió emisiones");

        // Los ids enviados deben estar deduplicados
        trackingMock.Verify(t => t.ObtenerCalendarioEmisionAsync(
            It.Is<List<int>>(ids => ids.Count == 2), It.IsAny<long>(), It.IsAny<long>()), Times.Once);
    }

    [Fact]
    public async Task CargaInicial_ConEmisiones_DeberiaDistribuirPorDiaSemana()
    {
        // Arrange: una emisión en fecha fija (miércoles 2026-08-19 21:00 UTC)
        var fechaMiercoles = DateTimeOffset.Parse("2026-08-19T21:00:00Z").DateTime;
        var animeLocal = new AnimeItem { AniListId = 21, Estado = "RELEASING", UrlPortada = "onepiece.png" };

        var dbMock = new Mock<IDatabaseService>();
        dbMock.Setup(d => d.ObtenerTodosLosAnimesAsync())
            .ReturnsAsync(new List<AnimeItem> { animeLocal });

        var trackingMock = new Mock<IAnimeTrackingService>();
        trackingMock
            .Setup(t => t.ObtenerCalendarioEmisionAsync(It.IsAny<List<int>>(), It.IsAny<long>(), It.IsAny<long>()))
            .ReturnsAsync(new List<AiringEpisode>
            {
                new()
                {
                    AniListId = 21,
                    Titulo = "One Piece",
                    NumeroEpisodio = 1172,
                    FechaEmision = fechaMiercoles
                }
            });

        // Act
        var vm = new CalendarioViewModel(dbMock.Object, trackingMock.Object);
        await EsperarCargaInicialAsync(vm);

        // Assert: la portada local (PortadaVisible) debe reemplazar a la de AniList
        vm.EstaCargando.Should().BeFalse();
        vm.TotalAnimesEnEmision.Should().Be(1);
        vm.Miercoles.Should().ContainSingle();
        vm.Miercoles[0].Titulo.Should().Be("One Piece");
        vm.Miercoles[0].UrlPortada.Should().Be(animeLocal.PortadaVisible,
            "el calendario debe usar la portada visible local del anime");
    }

    [Fact]
    public async Task CargaInicial_SinAnimes_DeberiaTerminarRapidoYQuedarVacio()
    {
        // Arrange
        var dbMock = new Mock<IDatabaseService>();
        dbMock.Setup(d => d.ObtenerTodosLosAnimesAsync()).ReturnsAsync(new List<AnimeItem>());

        var trackingMock = new Mock<IAnimeTrackingService>();

        // Act
        var vm = new CalendarioViewModel(dbMock.Object, trackingMock.Object);
        await EsperarCargaInicialAsync(vm);

        // Assert: no debe llamarse a la API sin ids
        vm.EstaCargando.Should().BeFalse();
        vm.EstaVacio.Should().BeTrue();
        trackingMock.Verify(t => t.ObtenerCalendarioEmisionAsync(
            It.IsAny<List<int>>(), It.IsAny<long>(), It.IsAny<long>()), Times.Never);
    }

    [Fact]
    public async Task CargarCalendarioCommand_ConCargaEnCurso_NoDeberiaEjecutarEnParalelo()
    {
        // Arrange
        var dbMock = new Mock<IDatabaseService>();
        dbMock.Setup(d => d.ObtenerTodosLosAnimesAsync()).ReturnsAsync(new List<AnimeItem>());
        var trackingMock = new Mock<IAnimeTrackingService>();

        var vm = new CalendarioViewModel(dbMock.Object, trackingMock.Object);
        await EsperarCargaInicialAsync(vm);

        // Act: disparar dos cargas consecutivas por el comando
        vm.CargarCalendarioCommand.Execute(null);
        vm.CargarCalendarioCommand.Execute(null);
        await EsperarCargaInicialAsync(vm);

        // Assert: no debe lanzar excepción por SemaphoreSlim ya liberado
        vm.EstaCargando.Should().BeFalse();
    }
}
