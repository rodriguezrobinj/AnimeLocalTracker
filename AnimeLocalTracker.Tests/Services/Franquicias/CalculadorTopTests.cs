#pragma warning disable CA1861 // Datos constantes de prueba: un arreglo por llamada es lo más legible aquí
using System.Collections.Generic;
using System.Linq;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services.Franquicias;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services.Franquicias;

public class CalculadorTopTests
{
    private static readonly IReadOnlyDictionary<int, int> SinFranquicias = new Dictionary<int, int>();

    private static AnimeItem Anime(int id, string titulo, int anio = 0) => new() { AniListId = id, Titulo = titulo, AnioLanzamiento = anio };

    private static List<RegistroEpisodio> Episodios(int anime, int cantidad, double segundos) =>
        Enumerable.Range(1, cantidad)
            .Select(i => new RegistroEpisodio { AniListId = anime, NumeroEpisodio = i, VistoLocal = true, TotalSegundos = segundos })
            .ToList();

    [Fact]
    public void SinVistos_EsVacio()
    {
        CalculadorTop.Calcular(new List<AnimeItem> { Anime(1, "A") }, new List<RegistroEpisodio>(), SinFranquicias).Should().BeEmpty();
    }

    [Fact]
    public void OrdenaPorTiempoNoPorCantidadDeEpisodios()
    {
        // "Serie larga": 100 episodios de 5 min = 500 min. "Serie normal": 30 episodios de 24 min = 720 min.
        var animes = new List<AnimeItem> { Anime(1, "Serie larga"), Anime(2, "Serie normal") };
        var vistos = Episodios(1, 100, 300).Concat(Episodios(2, 30, 1440)).ToList();

        var top = CalculadorTop.Calcular(animes, vistos, SinFranquicias);

        top.Select(f => f.Titulo).Should().Equal("Serie normal", "Serie larga");
    }

    [Fact]
    public void SumaTodaLaFranquicia_TemporadasYPeliculas()
    {
        // Franquicia F (id 10 = serie principal, 11 = película): juntas superan a un anime suelto largo
        var animes = new List<AnimeItem> { Anime(10, "Principal"), Anime(11, "Película"), Anime(20, "Suelto") };
        var vistos = Episodios(10, 20, 1440)      // 480 min
            .Concat(Episodios(11, 1, 6000))       // 100 min
            .Concat(Episodios(20, 30, 1200))      // 600 min: aún supera a la franquicia (580 min)
            .ToList();
        var franquicias = new Dictionary<int, int> { [10] = 10, [11] = 10, [20] = 20 };

        var top = CalculadorTop.Calcular(animes, vistos, franquicias);

        top[0].Titulo.Should().Be("Suelto");   // 600 min
        top[1].Titulo.Should().Be("Principal"); // 580 min sumando la película
        top[1].Segundos.Should().Be(20 * 1440 + 6000);
        top[1].Episodios.Should().Be(21);
        top[1].Titulos.Should().Be(2);
    }

    [Fact]
    public void LaFranquiciaSumadaPuedeSuperarAlAnimeSuelto()
    {
        var animes = new List<AnimeItem> { Anime(10, "Principal"), Anime(11, "Segunda temporada"), Anime(20, "Suelto") };
        var vistos = Episodios(10, 12, 1440).Concat(Episodios(11, 12, 1440)).Concat(Episodios(20, 20, 1440)).ToList();
        var franquicias = new Dictionary<int, int> { [10] = 10, [11] = 10, [20] = 20 };

        var top = CalculadorTop.Calcular(animes, vistos, franquicias);

        top[0].Titulo.Should().Be("Principal", "12 + 12 episodios de la franquicia superan a los 20 del anime suelto");
        top[0].Titulos.Should().Be(2);
    }

    [Fact]
    public void LaFranquiciaSeLlamaComoSuPrimeraTemporada_NoComoLaMasVista()
    {
        // Danmachi: la temporada 5 (2024) es la más larga, pero la franquicia debe llamarse como la 1.ª
        var animes = new List<AnimeItem> { Anime(1, "Danmachi", 2015), Anime(2, "Danmachi II", 2019), Anime(3, "Danmachi V", 2024) };
        var vistos = Episodios(1, 13, 1440).Concat(Episodios(2, 12, 1440)).Concat(Episodios(3, 15, 1440)).ToList();
        var franquicias = new Dictionary<int, int> { [1] = 1, [2] = 1, [3] = 1 };

        CalculadorTop.Calcular(animes, vistos, franquicias).Single().Titulo.Should().Be("Danmachi");
    }

    [Fact]
    public void UnaPeliculaAntiguaQueVesPoco_NoDaNombreALaFranquicia()
    {
        // La película (2013) pesa <20 % de la serie (2020): no debe ser el nombre de la franquicia
        var animes = new List<AnimeItem> { Anime(1, "Película vieja", 2013), Anime(2, "Serie", 2020) };
        var vistos = Episodios(1, 1, 5400).Concat(Episodios(2, 50, 1440)).ToList();
        var franquicias = new Dictionary<int, int> { [1] = 1, [2] = 1 };

        CalculadorTop.Calcular(animes, vistos, franquicias).Single().Titulo.Should().Be("Serie");
    }

    [Fact]
    public void ElNombreDeLaFranquiciaEsElDelTituloMasVisto()
    {
        var animes = new List<AnimeItem> { Anime(1, "One Piece Film"), Anime(2, "One Piece"), Anime(3, "One Piece Special") };
        var vistos = Episodios(1, 1, 6000).Concat(Episodios(2, 500, 1440)).Concat(Episodios(3, 1, 1400)).ToList();
        var franquicias = new Dictionary<int, int> { [1] = 1, [2] = 1, [3] = 1 };

        CalculadorTop.Calcular(animes, vistos, franquicias).Single().Titulo.Should().Be("One Piece");
    }

    [Fact]
    public void EnEmpateDeTiempo_ElRepresentanteEsElMasAntiguo()
    {
        var animes = new List<AnimeItem> { Anime(1, "Remake", anio: 2020), Anime(2, "Original", anio: 1999) };
        var vistos = Episodios(1, 10, 1440).Concat(Episodios(2, 10, 1440)).ToList();
        var franquicias = new Dictionary<int, int> { [1] = 1, [2] = 1 };

        CalculadorTop.Calcular(animes, vistos, franquicias).Single().Titulo.Should().Be("Original");
    }

    [Fact]
    public void AniListIdRepresentante_EsElMismoTituloQueDaNombreALaFranquicia()
    {
        // Usado para resolver la portada (p. ej. en la tarjeta Wrapped): debe ser el AniListId
        // de la 1.ª temporada ("Danmachi"), no el de la temporada más vista ("Danmachi V").
        var animes = new List<AnimeItem> { Anime(1, "Danmachi", 2015), Anime(2, "Danmachi II", 2019), Anime(3, "Danmachi V", 2024) };
        var vistos = Episodios(1, 13, 1440).Concat(Episodios(2, 12, 1440)).Concat(Episodios(3, 15, 1440)).ToList();
        var franquicias = new Dictionary<int, int> { [1] = 1, [2] = 1, [3] = 1 };

        CalculadorTop.Calcular(animes, vistos, franquicias).Single().AniListIdRepresentante.Should().Be(1);
    }

    [Fact]
    public void EpisodiosSinDuracion_UsanElPromedioGlobal_NoElDeSuGrupo()
    {
        // Promedio global de los que tienen duración: 1440 s. Los 10 episodios de B no tienen duración.
        var animes = new List<AnimeItem> { Anime(1, "A"), Anime(2, "B") };
        var vistos = Episodios(1, 10, 1440)
            .Concat(Enumerable.Range(1, 10).Select(i => new RegistroEpisodio { AniListId = 2, NumeroEpisodio = i, VistoLocal = true }))
            .ToList();

        var top = CalculadorTop.Calcular(animes, vistos, SinFranquicias);

        top.Should().HaveCount(2);
        top.Select(f => f.Segundos).Should().OnlyContain(s => s == 14400);
    }

    [Fact]
    public void AnimeQueNoEstaEnLaBiblioteca_UsaTituloDeRespaldo()
    {
        var top = CalculadorTop.Calcular(new List<AnimeItem>(), Episodios(77, 3, 1440), SinFranquicias);

        top.Single().Titulo.Should().Be("Anime 77");
    }

    [Fact]
    public void SoloDevuelveLaCantidadPedida()
    {
        var animes = Enumerable.Range(1, 8).Select(i => Anime(i, $"A{i}")).ToList();
        var vistos = animes.SelectMany(a => Episodios(a.AniListId, a.AniListId, 1440)).ToList();

        var top = CalculadorTop.Calcular(animes, vistos, SinFranquicias, cantidad: 5);

        top.Should().HaveCount(5);
        top[0].Titulo.Should().Be("A8");
    }

    [Fact]
    public void LosEpisodiosRepetidosDelMismoNumero_NoInflanElConteoDeEpisodios()
    {
        var animes = new List<AnimeItem> { Anime(1, "A") };
        var vistos = new List<RegistroEpisodio>
        {
            new() { AniListId = 1, NumeroEpisodio = 1, VistoLocal = true, TotalSegundos = 1440 },
            new() { AniListId = 1, NumeroEpisodio = 1, VistoLocal = true, TotalSegundos = 1440 }
        };

        CalculadorTop.Calcular(animes, vistos, SinFranquicias).Single().Episodios.Should().Be(1);
    }
}
