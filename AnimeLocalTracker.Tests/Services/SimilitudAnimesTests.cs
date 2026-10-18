using System.Collections.Generic;
using System.Linq;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Recomendaciones;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

public class SimilitudAnimesTests
{
    private static string Tags(params (string Nombre, int Rank)[] tags)
        => EtiquetasAnime.Serializar(tags.Select(t => new AniListTag { Name = t.Nombre, Rank = t.Rank }));

    private static AnimeItem Anime(int id, string titulo, string generos, string sinopsis, string? etiquetas, string estado = "PLANNING", int total = 24)
        => new() { AniListId = id, Titulo = titulo, Generos = generos, Sinopsis = sinopsis, Etiquetas = etiquetas, EstadoUsuario = estado, TotalEpisodios = total };

    private static readonly AnimeItem SteinsGate = Anime(9253, "Steins;Gate", "Drama, Sci-Fi, Thriller",
        "A self-proclaimed mad scientist discovers that his microwave can send messages to the past, and time travel changes everything.<br>",
        Tags(("Time Manipulation", 95), ("Thriller", 80), ("Psychological", 70)), estado: "COMPLETED");

    private static readonly AnimeItem Erased = Anime(21234, "Erased", "Drama, Mystery, Thriller",
        "Satoru is sent back in time to his childhood to prevent a murder; every time travel jump changes the past.",
        Tags(("Time Manipulation", 90), ("Thriller", 85), ("Detective", 60)));

    private static readonly AnimeItem YuruCamp = Anime(34798, "Yuru Camp", "Comedy, Slice of Life",
        "Girls go camping in the mountains, cook noodles and enjoy the quiet winter scenery.",
        Tags(("Iyashikei", 95), ("Cute Girls Doing Cute Things", 85), ("Camping", 90)));

    private static IReadOnlyList<Parecido> Calcular(params AnimeItem[] biblioteca)
        => SimilitudAnimes.Calcular(biblioteca[0], biblioteca, new Dictionary<int, int>(), new Dictionary<int, int>());

    [Fact]
    public void ElQueComparteTemasSaleYElQueNoSeParece_No()
    {
        var parecidos = Calcular(SteinsGate, Erased, YuruCamp);

        parecidos.Should().ContainSingle().Which.Anime.Should().BeSameAs(Erased);
        parecidos[0].Puntuacion.Should().BeGreaterThan(SimilitudAnimes.Umbral);
    }

    [Fact]
    public void NuncaSaleElPropioAnime()
        => Calcular(SteinsGate, SteinsGate, Erased).Select(p => p.Anime.AniListId).Should().NotContain(SteinsGate.AniListId);

    [Theory]
    [InlineData("COMPLETED")]
    [InlineData("DROPPED")]
    public void TerminadosOAbandonados_NoSalen(string estado)
    {
        var erasedVisto = Anime(1, "Erased (visto)", Erased.Generos, Erased.Sinopsis, Erased.Etiquetas, estado);

        Calcular(SteinsGate, erasedVisto).Should().BeEmpty();
    }

    [Fact]
    public void ConTodosLosEpisodiosVistos_NoSale()
    {
        var parecidos = SimilitudAnimes.Calcular(SteinsGate, new[] { SteinsGate, Erased },
            new Dictionary<int, int> { [Erased.AniListId] = 24 }, new Dictionary<int, int>());

        parecidos.Should().BeEmpty();
    }

    [Fact]
    public void TemasCompartidos_PrimeroTagsLuegoGenerosTraducidosSinRepetir()
        => Calcular(SteinsGate, Erased)[0].Temas.Should().Equal("Time Manipulation", "Thriller", LocalizationService.TraducirGenero("Drama"));

    [Fact]
    public void EnDisco_SeCopiaDelConteo()
        => SimilitudAnimes.Calcular(SteinsGate, new[] { SteinsGate, Erased }, new Dictionary<int, int>(),
            new Dictionary<int, int> { [Erased.AniListId] = 4 })[0].EnDisco.Should().Be(4);

    [Fact]
    public void SinTags_BaseVieja_UsaGenerosYSinopsis()
    {
        var sgViejo = Anime(1, "Steins;Gate", SteinsGate.Generos, SteinsGate.Sinopsis, etiquetas: null, estado: "COMPLETED");
        var erasedViejo = Anime(2, "Erased", Erased.Generos, Erased.Sinopsis, etiquetas: null);
        var campViejo = Anime(3, "Yuru Camp", YuruCamp.Generos, YuruCamp.Sinopsis, etiquetas: null);

        Calcular(sgViejo, erasedViejo, campViejo).Select(p => p.Anime.Titulo).Should().Equal("Erased");
    }

    [Fact]
    public void SinopsisVaciaYSinTags_NoFalla()
        => Calcular(Anime(1, "A", "", "", null), Anime(2, "B", "", "", null)).Should().BeEmpty();

    [Fact]
    public void MasDeCincoParecidos_SoloLosCincoMejoresEnOrden()
    {
        var copias = Enumerable.Range(0, 8).Select(i => Anime(100 + i, $"Erased {i}", Erased.Generos, Erased.Sinopsis,
            Tags(("Time Manipulation", 90 - i * 5), ("Thriller", 85)))).ToArray();

        var parecidos = Calcular(new[] { SteinsGate }.Concat(copias).ToArray());

        parecidos.Should().HaveCount(SimilitudAnimes.Maximo);
        parecidos.Select(p => p.Puntuacion).Should().BeInDescendingOrder();
        parecidos[0].Anime.Titulo.Should().Be("Erased 0");
    }
}
