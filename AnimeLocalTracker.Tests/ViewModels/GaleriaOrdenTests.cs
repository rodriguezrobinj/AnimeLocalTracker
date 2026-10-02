using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>Los dos órdenes "recientes" de la Galería: por estreno y por cuándo se añadió a la biblioteca.</summary>
public class GaleriaOrdenTests
{
    private static async Task<GaleriaViewModel> CrearAsync(params AnimeItem[] animes)
    {
        var db = new Mock<IDatabaseService>();
        db.Setup(d => d.ObtenerTodosLosAnimesAsync()).ReturnsAsync(animes.ToList());
        var sut = new GaleriaViewModel(Mock.Of<IAnimeTrackingService>(), db.Object, Mock.Of<IAuthService>(), Mock.Of<IDialogService>(),
            Mock.Of<IHttpClientFactory>(), Mock.Of<IImageCacheService>(), Mock.Of<IFileScannerService>());
        await Task.Delay(100); // la biblioteca se carga desde el constructor
        return sut;
    }

    private static List<string> Visibles(GaleriaViewModel sut) => sut.BibliotecaFiltrada!.Cast<AnimeItem>().Select(a => a.Titulo).ToList();

    [Fact]
    public async Task AnadidosRecientemente_PoneArribaLoUltimoQueAnadiste_YLosDeAntesAlFinal()
    {
        var hoy = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
        var sut = await CrearAsync(
            new AnimeItem { AniListId = 900, Titulo = "Antiguo en la biblioteca, nuevo en AniList" },           // sin fecha: de antes de guardarla
            new AnimeItem { AniListId = 100, Titulo = "Antiguo en la biblioteca, viejo en AniList" },
            new AnimeItem { AniListId = 1535, Titulo = "Death Note, añadido hoy", FechaAgregadoUtc = hoy },
            new AnimeItem { AniListId = 5000, Titulo = "Añadido ayer", FechaAgregadoUtc = hoy.AddDays(-1) });

        sut.CriterioOrdenSeleccionado = "Añadidos recientemente";

        Visibles(sut).Should().Equal(
            "Death Note, añadido hoy",
            "Añadido ayer",
            "Antiguo en la biblioteca, nuevo en AniList",
            "Antiguo en la biblioteca, viejo en AniList");
    }

    [Fact]
    public async Task EstrenoMasReciente_OrdenaPorAnioYTemporadaDeEstreno()
    {
        var sut = await CrearAsync(
            new AnimeItem { AniListId = 1, Titulo = "Verano 2023", AnioLanzamiento = 2023, Temporada = "SUMMER" },
            new AnimeItem { AniListId = 2, Titulo = "Otoño 2024", AnioLanzamiento = 2024, Temporada = "FALL" },
            new AnimeItem { AniListId = 3, Titulo = "Invierno 2024", AnioLanzamiento = 2024, Temporada = "WINTER" },
            new AnimeItem { AniListId = 4, Titulo = "Sin fecha de estreno" });

        sut.CriterioOrdenSeleccionado = "Estreno más reciente";

        Visibles(sut).Should().Equal("Otoño 2024", "Invierno 2024", "Verano 2023", "Sin fecha de estreno");
    }
}
