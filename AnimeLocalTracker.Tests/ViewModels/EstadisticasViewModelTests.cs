using System;
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
/// Cobertura del panel de estadísticas (TST-01): agregación correcta, género solo
/// de animes con episodios vistos, volumen (PER-01) y estado de error (EST-03).
/// </summary>
public class EstadisticasViewModelTests
{
    // CA1861: arrays constantes reutilizados como campos estáticos
    private static readonly string[] GenerosEsperados = { "Acción", "Drama", "Comedia" };

    private static (EstadisticasViewModel Vm, Mock<IDatabaseService> Db) CrearVm(
        List<AnimeItem>? animes = null, List<RegistroEpisodio>? registros = null)
    {
        var dbMock = new Mock<IDatabaseService>();
        dbMock.Setup(d => d.ObtenerTodosLosAnimesAsync()).ReturnsAsync(animes ?? new List<AnimeItem>());
        dbMock.Setup(d => d.ObtenerTodosLosRegistrosAsync()).ReturnsAsync(registros ?? new List<RegistroEpisodio>());
        return (new EstadisticasViewModel(dbMock.Object), dbMock);
    }

    [Fact]
    public async Task CargarEstadisticas_BibliotecaVacia_DeberiaMostrarCerosSinError()
    {
        // Act
        var (vm, _) = CrearVm();
        await vm.CargarEstadisticasAsync();

        // Assert
        vm.HayError.Should().BeFalse();
        vm.TotalAnimes.Should().Be(0);
        vm.TotalEpisodiosVistos.Should().Be(0);
        vm.PorcentajeCompletado.Should().Be(0);
        vm.AnimesEnProceso.Should().Be(0);
        vm.RachaActual.Should().Be("0 días");
    }

    [Fact]
    public async Task CargarEstadisticas_ConDatos_DeberiaCalcularResumenYGeneros()
    {
        // Arrange: 2 animes con vistos + 1 sin ningún episodio visto
        var animes = new List<AnimeItem>
        {
            new() { AniListId = 1, Titulo = "Shonen A", TotalEpisodios = 12, Generos = "Acción, Drama" },
            new() { AniListId = 2, Titulo = "Comedia B", TotalEpisodios = 24, Generos = "Comedia" },
            new() { AniListId = 3, Titulo = "Terror C", TotalEpisodios = 10, Generos = "Terror" }
        };
        var registros = new List<RegistroEpisodio>();
        registros.AddRange(Enumerable.Range(1, 6).Select(i => new RegistroEpisodio
        {
            AniListId = 1, NumeroEpisodio = i, VistoLocal = true, FavoritoLocal = i == 1
        }));
        registros.AddRange(Enumerable.Range(1, 3).Select(i => new RegistroEpisodio { AniListId = 2, NumeroEpisodio = i, VistoLocal = true }));
        registros.Add(new RegistroEpisodio { AniListId = 2, NumeroEpisodio = 4, VistoLocal = false });

        // Act
        var (vm, _) = CrearVm(animes, registros);
        await vm.CargarEstadisticasAsync();

        // Assert
        vm.HayError.Should().BeFalse();
        vm.TotalAnimes.Should().Be(3);
        vm.TotalEpisodiosVistos.Should().Be(9, "6 del anime 1 + 3 del anime 2");
        vm.TotalFavoritos.Should().Be(1);
        vm.AnimesEnProceso.Should().Be(2, "los animes 1 y 2 están a medio ver; el 3 no tiene vistos");
        vm.PorcentajeCompletado.Should().BeApproximately(9 * 100.0 / (12 + 24 + 10), 0.01);
        vm.DonutEstadoCentro.Should().Be("3");

        // Géneros: solo de animes con episodios vistos → "Terror" no debe aparecer
        var etiquetasGenero = vm.VistosPorGenero.Select(g => g.Etiqueta).ToList();
        etiquetasGenero.Should().Contain(GenerosEsperados);
        etiquetasGenero.Should().NotContain("Terror");
    }

    [Fact]
    public async Task CargarEstadisticas_ConVolumen_DeberiaProcesarSinPerderConteos()
    {
        // Arrange (PER-01): 10.000 registros distribuidos en 200 animes
        var animes = Enumerable.Range(1, 200)
            .Select(i => new AnimeItem { AniListId = i, Titulo = $"Anime {i}", TotalEpisodios = 100, Generos = "Acción" })
            .ToList();
        var registros = Enumerable.Range(1, 10000)
            .Select(i => new RegistroEpisodio { AniListId = (i % 200) + 1, NumeroEpisodio = i, VistoLocal = true })
            .ToList();

        // Act
        var (vm, _) = CrearVm(animes, registros);
        await vm.CargarEstadisticasAsync();

        // Assert: la indexación por lookup (O(A+R)) no debe perder ni inventar conteos
        vm.HayError.Should().BeFalse();
        vm.TotalAnimes.Should().Be(200);
        vm.TotalEpisodiosVistos.Should().Be(10000);
        vm.AnimesEnProceso.Should().Be(200);
    }

    [Fact]
    public async Task CargarEstadisticas_FalloDeBaseDatos_DeberiaMarcarErrorVisible()
    {
        // Arrange (EST-03)
        var dbMock = new Mock<IDatabaseService>();
        dbMock.Setup(d => d.ObtenerTodosLosAnimesAsync()).ThrowsAsync(new InvalidOperationException("db corrupta"));
        var vm = new EstadisticasViewModel(dbMock.Object);

        // Act
        await vm.CargarEstadisticasAsync();

        // Assert
        vm.HayError.Should().BeTrue();
        vm.MensajeError.Should().NotBeNullOrWhiteSpace();
    }
}
