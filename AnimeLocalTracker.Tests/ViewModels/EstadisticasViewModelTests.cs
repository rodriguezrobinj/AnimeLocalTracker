using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Franquicias;
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
        List<AnimeItem>? animes = null, List<RegistroEpisodio>? registros = null, IFranquiciaService? franquicias = null)
    {
        var dbMock = new Mock<IDatabaseService>();
        dbMock.Setup(d => d.ObtenerTodosLosAnimesAsync()).ReturnsAsync(animes ?? new List<AnimeItem>());
        dbMock.Setup(d => d.ObtenerTodosLosRegistrosAsync()).ReturnsAsync(registros ?? new List<RegistroEpisodio>());
        return (new EstadisticasViewModel(dbMock.Object, Mock.Of<IAnimeTrackingService>(), Mock.Of<IAuthService>(), Mock.Of<IDialogService>(),
            franquiciaService: franquicias), dbMock);
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
    public async Task CargarEstadisticas_EpisodiosImportadosSinDuracion_SeEstimanEnLasHoras()
    {
        // 10 episodios marcados en bloque (sin duración guardada): antes daban "0.0 h".
        var animes = new List<AnimeItem> { new() { AniListId = 1, Titulo = "Importado", TotalEpisodios = 10 } };
        var registros = Enumerable.Range(1, 10)
            .Select(i => new RegistroEpisodio { AniListId = 1, NumeroEpisodio = i, VistoLocal = true })
            .ToList();
        var (vm, _) = CrearVm(animes, registros);

        await vm.CargarEstadisticasAsync();

        vm.TotalEpisodiosVistos.Should().Be(10);
        vm.HorasVistasTexto.Should().StartWith("4", "10 episodios × 24 min = 4 h");
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
        var vm = new EstadisticasViewModel(dbMock.Object, Mock.Of<IAnimeTrackingService>(), Mock.Of<IAuthService>(), Mock.Of<IDialogService>());

        // Act
        await vm.CargarEstadisticasAsync();

        // Assert
        vm.HayError.Should().BeTrue();
        vm.MensajeError.Should().NotBeNullOrWhiteSpace();
    }

    // ───────────── Top por TIEMPO y por franquicia ─────────────

    private static List<RegistroEpisodio> Episodios(int anime, int cantidad, double segundos) =>
        Enumerable.Range(1, cantidad)
            .Select(i => new RegistroEpisodio { AniListId = anime, NumeroEpisodio = i, VistoLocal = true, TotalSegundos = segundos })
            .ToList();

    private static Mock<IFranquiciaService> Franquicias(IReadOnlyDictionary<int, int> mapa, bool sincronizar = false, IReadOnlyDictionary<int, int>? mapaTrasSincronizar = null)
    {
        var mock = new Mock<IFranquiciaService>();
        var llamadas = 0;
        mock.Setup(f => f.ObtenerMapaAsync(It.IsAny<IEnumerable<int>>()))
            .ReturnsAsync(() => ++llamadas == 1 || mapaTrasSincronizar == null ? mapa : mapaTrasSincronizar);
        mock.Setup(f => f.SincronizarAsync(It.IsAny<IEnumerable<int>>())).ReturnsAsync(sincronizar);
        return mock;
    }

    [Fact]
    public async Task Top_SeOrdenaPorTiempoVisto_NoPorNumeroDeEpisodios()
    {
        // Serie larguísima: 100 episodios de 5 min (500 min). Otra serie: 40 episodios de 24 min (960 min).
        var animes = new List<AnimeItem> { new() { AniListId = 1, Titulo = "Serie larguísima" }, new() { AniListId = 2, Titulo = "Serie normal" } };
        var registros = Episodios(1, 100, 300).Concat(Episodios(2, 40, 1440)).ToList();
        var (vm, _) = CrearVm(animes, registros);

        await vm.CargarEstadisticasAsync();

        vm.TopAnimes.Select(t => t.Titulo).Should().Equal("Serie normal", "Serie larguísima");
        vm.TopAnimes[0].Posicion.Should().Be(1);
        vm.TopAnimes[0].TiempoTexto.Should().Be("16 h");
        vm.TopAnimes[0].AnchoBarra.Should().BeApproximately(420.0, 1e-9);
        vm.TopAnimes[1].AnchoBarra.Should().BeLessThan(420.0);
    }

    [Fact]
    public async Task Top_DeberiaResolverLaPortadaDelTituloRepresentante()
    {
        // La portada del Top (usada en la tarjeta Wrapped) debe ser la del título que da nombre
        // a la franquicia (la 1.ª temporada), no la de la temporada con más tiempo visto.
        var animes = new List<AnimeItem>
        {
            new() { AniListId = 1, Titulo = "Danmachi", AnioLanzamiento = 2015, UrlPortada = "https://cdn/danmachi1.jpg" },
            new() { AniListId = 2, Titulo = "Danmachi V", AnioLanzamiento = 2024, UrlPortada = "https://cdn/danmachi5.jpg" }
        };
        var registros = Episodios(1, 13, 1440).Concat(Episodios(2, 15, 1440)).ToList();
        var franquicias = Franquicias(new Dictionary<int, int> { [1] = 1, [2] = 1 });
        var (vm, _) = CrearVm(animes, registros, franquicias.Object);

        await vm.CargarEstadisticasAsync();

        vm.TopAnimes.Should().ContainSingle();
        vm.TopAnimes[0].Titulo.Should().Be("Danmachi");
        vm.TopAnimes[0].RutaPortada.Should().Be("https://cdn/danmachi1.jpg");
    }

    [Fact]
    public async Task Top_SumaLaFranquiciaCompleta_TemporadasYPeliculas()
    {
        var animes = new List<AnimeItem>
        {
            new() { AniListId = 10, Titulo = "Serie" }, new() { AniListId = 11, Titulo = "Serie: Película" }, new() { AniListId = 20, Titulo = "Suelto" }
        };
        var registros = Episodios(10, 12, 1440).Concat(Episodios(11, 12, 1440)).Concat(Episodios(20, 20, 1440)).ToList();
        var mapa = new Dictionary<int, int> { [10] = 10, [11] = 10, [20] = 20 };
        var (vm, _) = CrearVm(animes, registros, Franquicias(mapa).Object);

        await vm.CargarEstadisticasAsync();

        vm.TopAnimes[0].Titulo.Should().Be("Serie");
        vm.TopAnimes[0].Titulos.Should().Be(2);
        vm.TopAnimes[0].EpisodiosVistos.Should().Be(24);
        vm.TopAnimes[0].DetalleTexto.Should().Contain("24").And.Contain("2");
    }

    [Fact]
    public async Task AnimeMasVisto_HablaDeLaMismaFranquiciaLider()
    {
        var animes = new List<AnimeItem> { new() { AniListId = 1, Titulo = "Líder" }, new() { AniListId = 2, Titulo = "Otro" } };
        var registros = Episodios(1, 30, 1440).Concat(Episodios(2, 5, 1440)).ToList();
        var (vm, _) = CrearVm(animes, registros);

        await vm.CargarEstadisticasAsync();

        vm.AnimeMasVisto.Should().Be("Líder");
        vm.AnimeMasVistoDetalle.Should().Contain("12 h").And.Contain("30");
    }

    [Fact]
    public async Task ElTopSeActualizaCuandoLaSincronizacionDeFranquiciasTrajoDatosNuevos()
    {
        // Al principio no se conoce ninguna relación; tras sincronizar, la película se une a la serie.
        var animes = new List<AnimeItem>
        {
            new() { AniListId = 10, Titulo = "Serie" }, new() { AniListId = 11, Titulo = "Película" }, new() { AniListId = 20, Titulo = "Suelto" }
        };
        var registros = Episodios(10, 12, 1440).Concat(Episodios(11, 12, 1440)).Concat(Episodios(20, 20, 1440)).ToList();
        var sinRelaciones = new Dictionary<int, int> { [10] = 10, [11] = 11, [20] = 20 };
        var conRelaciones = new Dictionary<int, int> { [10] = 10, [11] = 10, [20] = 20 };
        var servicio = Franquicias(sinRelaciones, sincronizar: true, mapaTrasSincronizar: conRelaciones);
        var (vm, _) = CrearVm(animes, registros, servicio.Object);

        await vm.CargarEstadisticasAsync();
        await vm.SincronizacionFranquicias;

        vm.TopAnimes[0].Titulo.Should().Be("Serie", "ya con la franquicia completa, 24 episodios superan a los 20 del suelto");
        vm.AnimeMasVisto.Should().Be("Serie");
    }

    [Fact]
    public async Task SiLaSincronizacionNoTrajoNada_NoSeRecalculaElTop()
    {
        var animes = new List<AnimeItem> { new() { AniListId = 1, Titulo = "A" } };
        var servicio = Franquicias(new Dictionary<int, int> { [1] = 1 }, sincronizar: false);
        var (vm, _) = CrearVm(animes, Episodios(1, 3, 1440), servicio.Object);

        await vm.CargarEstadisticasAsync();
        await vm.SincronizacionFranquicias;

        servicio.Verify(f => f.ObtenerMapaAsync(It.IsAny<IEnumerable<int>>()), Times.Once);
    }

    [Fact]
    public async Task UnFalloDeLaSincronizacionDeFranquicias_NoRompeLasEstadisticas()
    {
        var servicio = new Mock<IFranquiciaService>();
        servicio.Setup(f => f.ObtenerMapaAsync(It.IsAny<IEnumerable<int>>())).ReturnsAsync(new Dictionary<int, int>());
        servicio.Setup(f => f.SincronizarAsync(It.IsAny<IEnumerable<int>>())).ThrowsAsync(new InvalidOperationException("sin red"));
        var animes = new List<AnimeItem> { new() { AniListId = 1, Titulo = "A" } };
        var (vm, _) = CrearVm(animes, Episodios(1, 3, 1440), servicio.Object);

        await vm.CargarEstadisticasAsync();
        await vm.SincronizacionFranquicias;

        vm.HayError.Should().BeFalse();
        vm.TopAnimes.Should().ContainSingle();
    }

    // ───────────── Géneros traducidos ─────────────

    [Fact]
    public async Task Generos_SeMuestranTraducidos()
    {
        var animes = new List<AnimeItem>
        {
            new() { AniListId = 1, Titulo = "A", Generos = "Fantasy, Comedy" },
            new() { AniListId = 2, Titulo = "B", Generos = "Fantasy, Sci-Fi" }
        };
        var registros = Episodios(1, 2, 1440).Concat(Episodios(2, 2, 1440)).ToList();
        var (vm, _) = CrearVm(animes, registros);

        await vm.CargarEstadisticasAsync();

        string fantasia = LocalizationService.TraducirGenero("Fantasy");
        vm.GeneroFavorito.Should().Be(fantasia);
        vm.DonutGenerosCentro.Should().Be(fantasia);
        vm.VistosPorGenero.Select(g => g.Etiqueta).Should().Contain(new[] { fantasia, LocalizationService.TraducirGenero("Sci-Fi") });
        vm.DonutGeneros.Select(d => d.Label).Should().Contain(fantasia);
    }

    [Fact]
    public void TraducirGenero_EnEspanolNoDevuelveElNombreEnIngles()
    {
        // Regresión: Estadísticas mostraba "Fantasy", "Comedy", "Action"... sin traducir.
        if (LocalizationService.Instance.Idioma != "es") return;

        LocalizationService.TraducirGenero("Fantasy").Should().Be("Fantasía");
        LocalizationService.TraducirGenero("Comedy").Should().Be("Comedia");
        LocalizationService.TraducirGenero("Action").Should().Be("Acción");
    }

    // ───────────── Cambio de idioma ─────────────

    [Fact]
    public async Task AlCambiarDeIdioma_SeFuerzaRecalcularLasEstadisticas()
    {
        var (vm, _) = CrearVm();
        await vm.CargarEstadisticasAsync();
        vm.NecesitaRecargar().Should().BeFalse("acaba de cargarse");

        vm.Receive(new IdiomaCambiadoMensaje());

        vm.NecesitaRecargar().Should().BeTrue("los textos y géneros ya calculados están en el otro idioma");
    }
}
